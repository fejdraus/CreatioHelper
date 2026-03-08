using CreatioHelper.Application.DTOs;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Main synchronization engine interface (based on Syncthing model)
/// </summary>
public interface ISyncEngine
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync();
    Task<SyncDevice> AddDeviceAsync(string deviceId, string name, string? certificateFingerprint = null, List<string>? addresses = null);
    Task<bool> RemoveDeviceAsync(string deviceId);
    Task RemoveFolderAsync(string folderId, CancellationToken cancellationToken = default);
    Task<SyncFolder> AddFolderAsync(string folderId, string label, string path, string type = "sendreceive");

    /// <summary>
    /// Add a folder with full configuration - Syncthing compatible
    /// </summary>
    Task<SyncFolder> AddFolderAsync(FolderConfiguration config);

    /// <summary>
    /// Update folder configuration - Syncthing compatible
    /// </summary>
    Task<SyncFolder> UpdateFolderAsync(FolderConfiguration config);

    Task ShareFolderWithDeviceAsync(string folderId, string deviceId);
    Task UnshareFolderFromDeviceAsync(string folderId, string deviceId);
    Task PauseFolderAsync(string folderId);
    Task ResumeFolderAsync(string folderId);
    Task PauseDeviceAsync(string deviceId);
    Task ResumeDeviceAsync(string deviceId);
    Task ScanFolderAsync(string folderId, bool deep = false);
    Task<SyncStatus> GetSyncStatusAsync(string folderId);
    Task<List<SyncDevice>> GetDevicesAsync();
    Task<List<SyncFolder>> GetFoldersAsync();
    Task<SyncFolder?> GetFolderAsync(string folderId);
    Task<SyncStatistics> GetStatisticsAsync();
    Task<SyncConfiguration> GetConfigurationAsync();
    string DeviceId { get; } // Device ID property

    /// <summary>
    /// Apply configuration changes from ConfigXml to the sync engine.
    /// Compares current state with new config and applies changes incrementally.
    /// </summary>
    /// <param name="config">The ConfigXml to apply</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ApplyConfigurationAsync(ConfigXml config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reload configuration from the config.xml file.
    /// Equivalent to calling IConfigXmlService.LoadAsync() then ApplyConfigurationAsync().
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ReloadConfigurationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Override local changes to global state (for SendOnly folders)
    /// </summary>
    Task<bool> OverrideFolderAsync(string folderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revert local changes to match global state (for ReceiveOnly folders)
    /// </summary>
    Task<bool> RevertFolderAsync(string folderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get completion status for a device on a folder
    /// </summary>
    Task<FolderCompletionStatus> GetCompletionAsync(string folderId, string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get list of files that need to be synchronized
    /// </summary>
    Task<FolderNeedList> GetNeedListAsync(string folderId, int page = 1, int perPage = 100, CancellationToken cancellationToken = default);

    event EventHandler<FolderSyncedEventArgs> FolderSynced;
    event EventHandler<ConflictDetectedEventArgs> ConflictDetected;
    event EventHandler<SyncErrorEventArgs> SyncError;
}

public class SyncStatus
{
    public string FolderId { get; set; } = string.Empty;
    public SyncState State { get; set; } = SyncState.Idle;
    public long GlobalBytes { get; set; }
    public long LocalBytes { get; set; }
    public long NeedBytes { get; set; }
    public long GlobalFiles { get; set; }
    public long LocalFiles { get; set; }
    public long NeedFiles { get; set; }
    public long NeedDeletes { get; set; }
    public DateTime LastScan { get; set; }
    public DateTime LastSync { get; set; }
    public List<string> Errors { get; set; } = new();
    
    // Additional properties for Syncthing compatibility
    public long TotalFiles { get; set; }
    public long TotalDirectories { get; set; }
    public long TotalBytes { get; set; }
    public int LocalDirectories { get; set; }
    public int OutOfSyncFiles { get; set; }
    public long OutOfSyncBytes { get; set; }
    public long Version { get; set; }
    public long Sequence { get; set; }
}

public class SyncStatistics
{
    public long TotalBytesIn { get; set; }
    public long TotalBytesOut { get; set; }
    public int TotalFilesReceived { get; set; }
    public int TotalFilesSent { get; set; }
    public DateTime StartTime { get; set; }
    public TimeSpan Uptime { get; set; }
    public int ConnectedDevices { get; set; }
    public int TotalDevices { get; set; }
    public int SyncedFolders { get; set; }
    public int TotalFolders { get; set; }
    
    // Block-level deduplication statistics
    public long TotalBytesDeduped { get; set; }
    public int TotalBlocksDeduped { get; set; }
    public double DeduplicationRatio => TotalBytesIn > 0 ? (double)TotalBytesDeduped / TotalBytesIn : 0.0;
}

public class FolderSyncedEventArgs : EventArgs
{
    public string FolderId { get; }
    public SyncSummary Summary { get; }
    
    public FolderSyncedEventArgs(string folderId, SyncSummary summary)
    {
        FolderId = folderId;
        Summary = summary;
    }
}

public class ConflictDetectedEventArgs : EventArgs
{
    public string FolderId { get; }
    public string FilePath { get; }
    public List<ConflictVersion> Versions { get; }
    
    public ConflictDetectedEventArgs(string folderId, string filePath, List<ConflictVersion> versions)
    {
        FolderId = folderId;
        FilePath = filePath;
        Versions = versions;
    }
}

public class SyncErrorEventArgs : EventArgs
{
    public string FolderId { get; }
    public string? DeviceId { get; }
    public string Error { get; }
    public Exception? Exception { get; }
    
    public SyncErrorEventArgs(string folderId, string error, string? deviceId = null, Exception? exception = null)
    {
        FolderId = folderId;
        DeviceId = deviceId;
        Error = error;
        Exception = exception;
    }
}

public class SyncSummary
{
    public int FilesTransferred { get; set; }
    public long BytesTransferred { get; set; }
    public int FilesDeleted { get; set; }
    public int Conflicts { get; set; }
    public TimeSpan Duration { get; set; }
    public List<string> Errors { get; set; } = new();
}

public class ConflictVersion
{
    public string DeviceId { get; set; } = string.Empty;
    public DateTime ModifiedTime { get; set; }
    public long Size { get; set; }
    public string Hash { get; set; } = string.Empty;
}

public enum SyncState
{
    Idle,
    Scanning,
    Syncing,
    Error,
    Paused
}

/// <summary>
/// Completion status for a device on a folder
/// </summary>
public class FolderCompletionStatus
{
    /// <summary>
    /// Completion percentage (0-100)
    /// </summary>
    public double Completion { get; set; }

    /// <summary>
    /// Total global bytes
    /// </summary>
    public long GlobalBytes { get; set; }

    /// <summary>
    /// Bytes needed to complete sync
    /// </summary>
    public long NeedBytes { get; set; }

    /// <summary>
    /// Total global items (files + directories)
    /// </summary>
    public long GlobalItems { get; set; }

    /// <summary>
    /// Items needed to complete sync
    /// </summary>
    public long NeedItems { get; set; }

    /// <summary>
    /// Deletes needed
    /// </summary>
    public long NeedDeletes { get; set; }

    /// <summary>
    /// Current sequence number
    /// </summary>
    public long Sequence { get; set; }

    /// <summary>
    /// Remote device state (idle, syncing, etc.)
    /// </summary>
    public string RemoteState { get; set; } = "unknown";
}

/// <summary>
/// List of files that need to be synchronized
/// </summary>
public class FolderNeedList
{
    /// <summary>
    /// Files that need to be downloaded
    /// </summary>
    public List<NeedFile> Files { get; set; } = new();

    /// <summary>
    /// Current page
    /// </summary>
    public int Page { get; set; } = 1;

    /// <summary>
    /// Items per page
    /// </summary>
    public int PerPage { get; set; } = 100;

    /// <summary>
    /// Total number of files needed
    /// </summary>
    public int Total { get; set; }

    /// <summary>
    /// Overall progress information
    /// </summary>
    public SyncProgress Progress { get; set; } = new();
}

/// <summary>
/// Information about a file that needs synchronization
/// </summary>
public class NeedFile
{
    /// <summary>
    /// File name (relative path)
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// File size in bytes
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Last modification time
    /// </summary>
    public DateTime ModifiedTime { get; set; }

    /// <summary>
    /// File type (file, directory, symlink)
    /// </summary>
    public string Type { get; set; } = "file";

    /// <summary>
    /// Devices that have this file
    /// </summary>
    public List<string> Availability { get; set; } = new();
}

/// <summary>
/// Sync progress information
/// </summary>
public class SyncProgress
{
    /// <summary>
    /// Total bytes to transfer
    /// </summary>
    public long BytesTotal { get; set; }

    /// <summary>
    /// Bytes already transferred
    /// </summary>
    public long BytesDone { get; set; }

    /// <summary>
    /// Completion percentage
    /// </summary>
    public double Percentage => BytesTotal > 0 ? (double)BytesDone / BytesTotal * 100 : 100;
}