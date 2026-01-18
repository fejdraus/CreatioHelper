using CreatioHelper.Domain.Common;
using CreatioHelper.Domain.Enums;

namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Plan for synchronization actions based on file comparison
/// </summary>
public class SyncPlan : AggregateRoot
{
    public string FolderId { get; private set; } = string.Empty;
    public string DeviceId { get; private set; } = string.Empty;
    public DateTime CreatedAt { get; private set; }
    
    // Files to download from remote device
    public List<FileAction> FilesToDownload { get; private set; } = new();
    
    // Files to upload to remote device  
    public List<FileAction> FilesToUpload { get; private set; } = new();
    
    // Files to delete locally
    public List<FileAction> FilesToDelete { get; private set; } = new();
    
    // Conflicts that need resolution
    public List<FileConflict> Conflicts { get; private set; } = new();
    
    public long TotalBytesToDownload { get; private set; }
    public long TotalBytesToUpload { get; private set; }
    public int TotalFilesToDownload { get; private set; }
    public int TotalFilesToUpload { get; private set; }

    private SyncPlan() { } // For EF Core

    public SyncPlan(string folderId, string deviceId)
    {
        FolderId = folderId;
        DeviceId = deviceId;
        CreatedAt = DateTime.UtcNow;
    }

    public void AddDownloadAction(SyncFileInfo remoteFile, FileActionReason reason)
    {
        var action = new FileAction(remoteFile.Name, FileActionType.Download, reason, remoteFile.Size, remoteFile);
        FilesToDownload.Add(action);
        TotalBytesToDownload += remoteFile.Size;
        TotalFilesToDownload++;
    }

    public void AddUploadAction(SyncFileInfo localFile, FileActionReason reason)
    {
        AddUploadAction(localFile, reason, null);
    }

    /// <summary>
    /// Add upload action with remote file info for delta upload
    /// </summary>
    /// <param name="localFile">Local file to upload</param>
    /// <param name="reason">Reason for upload</param>
    /// <param name="remoteFile">Current state of file on remote device (for delta comparison)</param>
    public void AddUploadAction(SyncFileInfo localFile, FileActionReason reason, SyncFileInfo? remoteFile)
    {
        var action = new FileAction(localFile.Name, FileActionType.Upload, reason, localFile.Size, localFile);
        action.RemoteFileInfo = remoteFile;
        FilesToUpload.Add(action);
        TotalBytesToUpload += localFile.Size;
        TotalFilesToUpload++;
    }

    public void AddDeleteAction(string fileName, FileActionReason reason)
    {
        var action = new FileAction(fileName, FileActionType.Delete, reason, 0, null);
        FilesToDelete.Add(action);
    }

    public void AddConflict(SyncFileInfo localFile, SyncFileInfo remoteFile, ConflictType conflictType)
    {
        var conflict = new FileConflict(localFile.Name, conflictType, localFile, remoteFile);
        Conflicts.Add(conflict);
    }

    public bool HasWork => FilesToDownload.Any() || FilesToUpload.Any() || FilesToDelete.Any();

    /// <summary>
    /// Sort files to download according to the specified pull order.
    /// </summary>
    public void SortDownloads(SyncPullOrder order)
    {
        FilesToDownload = order switch
        {
            SyncPullOrder.Alphabetic => FilesToDownload.OrderBy(f => f.FileName).ToList(),
            SyncPullOrder.SmallestFirst => FilesToDownload.OrderBy(f => f.FileSize).ToList(),
            SyncPullOrder.LargestFirst => FilesToDownload.OrderByDescending(f => f.FileSize).ToList(),
            SyncPullOrder.OldestFirst => FilesToDownload.OrderBy(f => f.FileInfo?.ModifiedTime ?? DateTime.MaxValue).ToList(),
            SyncPullOrder.NewestFirst => FilesToDownload.OrderByDescending(f => f.FileInfo?.ModifiedTime ?? DateTime.MinValue).ToList(),
            _ => FilesToDownload.OrderBy(_ => Guid.NewGuid()).ToList() // Random
        };
    }

    /// <summary>
    /// Sort files to upload according to the specified pull order.
    /// </summary>
    public void SortUploads(SyncPullOrder order)
    {
        FilesToUpload = order switch
        {
            SyncPullOrder.Alphabetic => FilesToUpload.OrderBy(f => f.FileName).ToList(),
            SyncPullOrder.SmallestFirst => FilesToUpload.OrderBy(f => f.FileSize).ToList(),
            SyncPullOrder.LargestFirst => FilesToUpload.OrderByDescending(f => f.FileSize).ToList(),
            SyncPullOrder.OldestFirst => FilesToUpload.OrderBy(f => f.FileInfo?.ModifiedTime ?? DateTime.MaxValue).ToList(),
            SyncPullOrder.NewestFirst => FilesToUpload.OrderByDescending(f => f.FileInfo?.ModifiedTime ?? DateTime.MinValue).ToList(),
            _ => FilesToUpload.OrderBy(_ => Guid.NewGuid()).ToList() // Random
        };
    }

    public override bool IsValid()
    {
        return !string.IsNullOrEmpty(FolderId) && !string.IsNullOrEmpty(DeviceId);
    }

    public override IEnumerable<string> GetBrokenRules()
    {
        var rules = new List<string>();
        
        if (string.IsNullOrEmpty(FolderId))
            rules.Add("Folder ID cannot be empty");
            
        if (string.IsNullOrEmpty(DeviceId))
            rules.Add("Device ID cannot be empty");
            
        return rules;
    }
}

/// <summary>
/// Action to perform on a file
/// </summary>
public class FileAction
{
    public string FileName { get; }
    public FileActionType ActionType { get; }
    public FileActionReason Reason { get; }
    public long FileSize { get; }
    public DateTime CreatedAt { get; }
    public SyncFileInfo? FileInfo { get; } // Reference to the file info
    
    // Delta sync properties
    public object? DeltaSyncPlan { get; set; } // Reference to DeltaSyncPlan for optimized transfers
    public long? OptimizedSize { get; set; } // Actual bytes to transfer after delta optimization

    /// <summary>
    /// For download: alias for FileInfo (remote file we're downloading)
    /// For upload: current state of file on remote device (for delta comparison)
    /// </summary>
    public SyncFileInfo? RemoteFileInfo { get; set; }

    /// <summary>
    /// Alias for backward compatibility (download scenarios)
    /// </summary>
    public SyncFileInfo? RemoteFile => RemoteFileInfo ?? FileInfo;
    
    // Block-level deduplication properties
    public object? OptimizedPlan { get; set; } // Reference to OptimizedTransferPlan for block deduplication
    public object? SyncthingBlockDiff { get; set; } // Reference to SyncthingFileDiff for block-level differences

    public FileAction(string fileName, FileActionType actionType, FileActionReason reason, long fileSize, SyncFileInfo? fileInfo)
    {
        FileName = fileName;
        ActionType = actionType;
        Reason = reason;
        FileSize = fileSize;
        FileInfo = fileInfo;
        CreatedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// File conflict information
/// </summary>
public class FileConflict
{
    public string FileName { get; }
    public ConflictType ConflictType { get; }
    public SyncFileInfo LocalFile { get; }
    public SyncFileInfo RemoteFile { get; }
    public DateTime DetectedAt { get; }

    public FileConflict(string fileName, ConflictType conflictType, SyncFileInfo localFile, SyncFileInfo remoteFile)
    {
        FileName = fileName;
        ConflictType = conflictType;
        LocalFile = localFile;
        RemoteFile = remoteFile;
        DetectedAt = DateTime.UtcNow;
    }
}

/// <summary>
/// Type of action to perform
/// </summary>
public enum FileActionType
{
    Download,
    Upload,
    Delete
}

/// <summary>
/// Reason for the action
/// </summary>
public enum FileActionReason
{
    NewFile,
    ModifiedContent,
    ModifiedMetadata,
    DeletedRemotely,
    DeletedLocally,
    Conflict
}

/// <summary>
/// Type of conflict
/// </summary>
public enum ConflictType
{
    BothModified,
    LocalDeletedRemoteModified,
    LocalModifiedRemoteDeleted,
    TypeMismatch, // file vs directory
    DeletedLocallyModifiedRemotely,
    ModifiedLocallyDeletedRemotely,
    PermissionConflict,
    UnexpectedLocalChange,
    ConcurrentModification
}