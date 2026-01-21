namespace CreatioHelper.Agent.Services.SyncEvents;

/// <summary>
/// Abstraction for sync event sources.
/// Allows unified handling of events from both built-in sync engine and external Syncthing.
/// </summary>
public interface ISyncEventSource : IAsyncDisposable
{
    /// <summary>
    /// Name of the event source for logging
    /// </summary>
    string SourceName { get; }

    /// <summary>
    /// Whether the event source is connected and operational
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Start listening for events
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop listening for events
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Event fired when sync activity starts on a folder
    /// </summary>
    event EventHandler<SyncActivityEventArgs>? SyncStarted;

    /// <summary>
    /// Event fired when sync activity completes on a folder
    /// </summary>
    event EventHandler<SyncActivityEventArgs>? SyncCompleted;

    /// <summary>
    /// Event fired when a file transfer starts
    /// </summary>
    event EventHandler<FileTransferEventArgs>? FileTransferStarted;

    /// <summary>
    /// Event fired when a file transfer completes
    /// </summary>
    event EventHandler<FileTransferEventArgs>? FileTransferCompleted;

    /// <summary>
    /// Event fired when folder state changes
    /// </summary>
    event EventHandler<FolderStateEventArgs>? FolderStateChanged;

    /// <summary>
    /// Check if a specific folder is currently syncing
    /// </summary>
    Task<bool> IsFolderSyncingAsync(string folderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get completion percentage for a folder (0-100)
    /// </summary>
    Task<double> GetFolderCompletionAsync(string folderId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Event args for sync activity events
/// </summary>
public class SyncActivityEventArgs : EventArgs
{
    public string FolderId { get; init; } = string.Empty;
    public string? FolderLabel { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? DeviceId { get; init; }
}

/// <summary>
/// Event args for file transfer events
/// </summary>
public class FileTransferEventArgs : EventArgs
{
    public string FolderId { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty; // "update", "delete", "metadata"
    public long Size { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? Error { get; init; }
}

/// <summary>
/// Event args for folder state changes
/// </summary>
public class FolderStateEventArgs : EventArgs
{
    public string FolderId { get; init; } = string.Empty;
    public FolderSyncState State { get; init; }
    public FolderSyncState PreviousState { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? Error { get; init; }
}

/// <summary>
/// Folder sync states (unified across sync sources)
/// </summary>
public enum FolderSyncState
{
    Unknown,
    Idle,
    Scanning,
    ScanWaiting,
    Syncing,
    SyncWaiting,
    SyncPreparing,
    Cleaning,
    CleanWaiting,
    Error,
    Paused
}
