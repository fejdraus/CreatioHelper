using CreatioHelper.Domain.Common;

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
        var action = new FileAction(localFile.Name, FileActionType.Upload, reason, localFile.Size, localFile);
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
    public SyncFileInfo? RemoteFile => FileInfo; // Alias for clarity

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
    TypeMismatch // file vs directory
}