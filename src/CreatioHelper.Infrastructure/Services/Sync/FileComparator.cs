using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Compares local and remote files to determine sync actions
/// Based on Syncthing's file comparison logic
/// </summary>
public class FileComparator
{
    private readonly ILogger<FileComparator> _logger;

    public FileComparator(ILogger<FileComparator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Creates sync plan by comparing local and remote files
    /// </summary>
    public SyncPlan CreateSyncPlan(string folderId, string deviceId, 
        List<SyncFileInfo> localFiles, List<SyncFileInfo> remoteFiles)
    {
        var plan = new SyncPlan(folderId, deviceId);
        
        _logger.LogInformation("Creating sync plan for folder {FolderId} with device {DeviceId}: {LocalCount} local, {RemoteCount} remote files", 
            folderId, deviceId, localFiles.Count, remoteFiles.Count);

        var localFileMap = localFiles.ToDictionary(f => f.RelativePath, f => f);
        var remoteFileMap = remoteFiles.ToDictionary(f => f.RelativePath, f => f);

        // Find files that exist on both sides - check for modifications
        foreach (var localFile in localFiles)
        {
            if (remoteFileMap.TryGetValue(localFile.RelativePath, out var remoteFile))
            {
                var comparison = CompareFiles(localFile, remoteFile);
                HandleFileComparison(plan, localFile, remoteFile, comparison);
            }
            else
            {
                // File exists locally but not remotely
                // Check if remote index contains this file as deleted
                var deletedRemoteFile = remoteFiles.FirstOrDefault(f => f.RelativePath == localFile.RelativePath && f.IsDeleted);
                
                if (deletedRemoteFile != null)
                {
                    // File was deleted remotely - delete it locally
                    plan.AddDeleteAction(localFile.RelativePath, FileActionReason.DeletedRemotely);
                    _logger.LogDebug("File was deleted remotely, deleting locally: {FileName}", localFile.RelativePath);
                }
                else
                {
                    // File is truly new locally - upload it
                    plan.AddUploadAction(localFile, FileActionReason.NewFile);
                    _logger.LogDebug("Local file not found remotely: {FileName}", localFile.RelativePath);
                }
            }
        }

        // Find files that exist only remotely - download them (unless they're deleted)
        foreach (var remoteFile in remoteFiles)
        {
            if (!localFileMap.ContainsKey(remoteFile.RelativePath))
            {
                if (!remoteFile.IsDeleted)
                {
                    plan.AddDownloadAction(remoteFile, FileActionReason.NewFile);
                    _logger.LogDebug("Remote file not found locally: {FileName}", remoteFile.RelativePath);
                }
                else
                {
                    _logger.LogDebug("Skipping deleted remote file: {FileName}", remoteFile.RelativePath);
                }
            }
        }

        _logger.LogInformation("Sync plan created: {Download} downloads, {Upload} uploads, {Delete} deletes, {Conflicts} conflicts",
            plan.TotalFilesToDownload, plan.TotalFilesToUpload, plan.FilesToDelete.Count, plan.Conflicts.Count);

        return plan;
    }

    /// <summary>
    /// Compares two files and determines the relationship
    /// </summary>
    private FileComparisonResult CompareFiles(SyncFileInfo localFile, SyncFileInfo remoteFile)
    {
        // Check if files are identical
        if (AreFilesIdentical(localFile, remoteFile))
        {
            return FileComparisonResult.Identical;
        }

        // Check for type mismatch (file vs directory)
        if (localFile.Type != remoteFile.Type)
        {
            return FileComparisonResult.TypeMismatch;
        }

        // Check if one was deleted
        if (localFile.IsDeleted && !remoteFile.IsDeleted)
        {
            return FileComparisonResult.LocalDeleted;
        }
        
        if (!localFile.IsDeleted && remoteFile.IsDeleted)
        {
            return FileComparisonResult.RemoteDeleted;
        }

        if (localFile.IsDeleted && remoteFile.IsDeleted)
        {
            return FileComparisonResult.BothDeleted;
        }

        // Compare vector clocks to determine which is newer
        var localVectorClock = ConvertToBepVectorClock(localFile.Vector);
        var remoteVectorClock = ConvertToBepVectorClock(remoteFile.Vector);
        var vectorComparison = localVectorClock.Compare(remoteVectorClock);
        
        switch (vectorComparison)
        {
            case VectorClockComparison.Greater:
                return FileComparisonResult.LocalNewer;
                
            case VectorClockComparison.Lesser:
                return FileComparisonResult.RemoteNewer;
                
            case VectorClockComparison.Concurrent:
                // Files modified concurrently - conflict
                return FileComparisonResult.Conflict;
                
            case VectorClockComparison.Equal:
                // Vector clocks equal but content different - check modification time
                if (localFile.ModifiedTime > remoteFile.ModifiedTime)
                    return FileComparisonResult.LocalNewer;
                else if (remoteFile.ModifiedTime > localFile.ModifiedTime)
                    return FileComparisonResult.RemoteNewer;
                else
                    return FileComparisonResult.Conflict; // Same time but different content
        }

        return FileComparisonResult.Conflict;
    }

    /// <summary>
    /// Handles the result of file comparison
    /// </summary>
    private void HandleFileComparison(SyncPlan plan, SyncFileInfo localFile, SyncFileInfo remoteFile, 
        FileComparisonResult comparison)
    {
        switch (comparison)
        {
            case FileComparisonResult.Identical:
                // No action needed
                _logger.LogTrace("Files identical: {FileName}", localFile.RelativePath);
                break;

            case FileComparisonResult.LocalNewer:
                // Pass remoteFile for delta upload - remote has blocks we can compare against
                plan.AddUploadAction(localFile, FileActionReason.ModifiedContent, remoteFile);
                _logger.LogDebug("Local file newer: {FileName} (remote has {RemoteBlocks} blocks for delta comparison)",
                    localFile.RelativePath, remoteFile.Blocks.Count);
                break;

            case FileComparisonResult.RemoteNewer:
                plan.AddDownloadAction(remoteFile, FileActionReason.ModifiedContent);
                _logger.LogDebug("Remote file newer: {FileName}", remoteFile.RelativePath);
                break;

            case FileComparisonResult.LocalDeleted:
                plan.AddDeleteAction(localFile.RelativePath, FileActionReason.DeletedLocally);
                _logger.LogDebug("File deleted locally: {FileName}", localFile.RelativePath);
                break;

            case FileComparisonResult.RemoteDeleted:
                plan.AddDeleteAction(remoteFile.RelativePath, FileActionReason.DeletedRemotely);
                _logger.LogDebug("File deleted remotely: {FileName}", remoteFile.RelativePath);
                break;

            case FileComparisonResult.BothDeleted:
                // No action needed
                _logger.LogTrace("File deleted on both sides: {FileName}", localFile.RelativePath);
                break;

            case FileComparisonResult.TypeMismatch:
                plan.AddConflict(localFile, remoteFile, ConflictType.TypeMismatch);
                _logger.LogWarning("Type mismatch conflict: {FileName}", localFile.RelativePath);
                break;

            case FileComparisonResult.Conflict:
                plan.AddConflict(localFile, remoteFile, ConflictType.BothModified);
                _logger.LogWarning("Content conflict: {FileName}", localFile.RelativePath);
                break;
        }
    }

    /// <summary>
    /// Checks if two files are identical
    /// </summary>
    private bool AreFilesIdentical(SyncFileInfo localFile, SyncFileInfo remoteFile)
    {
        return localFile.Hash == remoteFile.Hash &&
               localFile.Size == remoteFile.Size &&
               localFile.Type == remoteFile.Type &&
               !localFile.IsDeleted &&
               !remoteFile.IsDeleted;
    }

    /// <summary>
    /// Converts VectorClock to BepVectorClock for comparison
    /// </summary>
    private BepVectorClock ConvertToBepVectorClock(VectorClock vectorClock)
    {
        var bepVectorClock = new BepVectorClock();
        
        foreach (var counter in vectorClock.Counters)
        {
            // Convert device ID string to ulong (simple hash for now)
            var deviceIdHash = (ulong)counter.Key.GetHashCode();
            bepVectorClock.Update(deviceIdHash, (ulong)counter.Value);
        }
        
        return bepVectorClock;
    }
}

/// <summary>
/// Result of comparing two files
/// </summary>
public enum FileComparisonResult
{
    Identical,
    LocalNewer,
    RemoteNewer,
    LocalDeleted,
    RemoteDeleted,
    BothDeleted,
    TypeMismatch,
    Conflict
}

