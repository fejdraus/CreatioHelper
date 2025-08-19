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
    Task<SyncFolder> AddFolderAsync(string folderId, string label, string path, FolderType type = FolderType.SendReceive);
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
    Task<SyncStatistics> GetStatisticsAsync();
    Task<SyncConfiguration> GetConfigurationAsync();
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
    public int GlobalFiles { get; set; }
    public int LocalFiles { get; set; }
    public int NeedFiles { get; set; }
    public int NeedDeletes { get; set; }
    public DateTime LastScan { get; set; }
    public DateTime LastSync { get; set; }
    public List<string> Errors { get; set; } = new();
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