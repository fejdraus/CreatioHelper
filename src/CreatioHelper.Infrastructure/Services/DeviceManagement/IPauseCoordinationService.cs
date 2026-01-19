using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services.DeviceManagement;

/// <summary>
/// Service for coordinating pause/resume operations on devices and folders.
/// Based on Syncthing's pause handling from lib/model/model.go
///
/// Key behaviors:
/// - Pause individual devices (stops sync with that device)
/// - Pause individual folders (stops sync of that folder)
/// - Handle in-progress transfers gracefully
/// - Emit appropriate events
/// - Support graceful shutdown with pause timeout
/// </summary>
public interface IPauseCoordinationService
{
    /// <summary>
    /// Pause a device - stops all sync activity with that device.
    /// </summary>
    /// <param name="deviceId">Device to pause</param>
    /// <param name="graceful">Whether to wait for in-progress transfers</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<PauseResult> PauseDeviceAsync(string deviceId, bool graceful = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resume a paused device.
    /// </summary>
    Task<PauseResult> ResumeDeviceAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pause a folder - stops all sync activity for that folder.
    /// </summary>
    /// <param name="folderId">Folder to pause</param>
    /// <param name="graceful">Whether to wait for in-progress transfers</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<PauseResult> PauseFolderAsync(string folderId, bool graceful = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resume a paused folder.
    /// </summary>
    Task<PauseResult> ResumeFolderAsync(string folderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pause all devices.
    /// </summary>
    Task<PauseAllResult> PauseAllDevicesAsync(bool graceful = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resume all paused devices.
    /// </summary>
    Task<PauseAllResult> ResumeAllDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Pause all folders.
    /// </summary>
    Task<PauseAllResult> PauseAllFoldersAsync(bool graceful = true, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resume all paused folders.
    /// </summary>
    Task<PauseAllResult> ResumeAllFoldersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a device is paused.
    /// </summary>
    Task<bool> IsDevicePausedAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a folder is paused.
    /// </summary>
    Task<bool> IsFolderPausedAsync(string folderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all paused devices.
    /// </summary>
    Task<IEnumerable<SyncDevice>> GetPausedDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all paused folders.
    /// </summary>
    Task<IEnumerable<SyncFolder>> GetPausedFoldersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Register a callback to be notified when in-progress operations should stop.
    /// </summary>
    void RegisterPauseCallback(string id, Func<CancellationToken, Task> callback);

    /// <summary>
    /// Unregister a pause callback.
    /// </summary>
    void UnregisterPauseCallback(string id);

    /// <summary>
    /// Get the current service status.
    /// </summary>
    PauseServiceStatus GetStatus();
}

/// <summary>
/// Result of a pause/resume operation.
/// </summary>
public class PauseResult
{
    /// <summary>
    /// Whether the operation succeeded.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// The current paused state after the operation.
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Whether the operation was already in the requested state.
    /// </summary>
    public bool AlreadyInState { get; set; }

    /// <summary>
    /// Number of in-progress operations that were waited for.
    /// </summary>
    public int WaitedForOperations { get; set; }

    /// <summary>
    /// Time taken to complete the pause (including graceful wait).
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Error message if operation failed.
    /// </summary>
    public string? Error { get; set; }
}

/// <summary>
/// Result of pausing/resuming all devices or folders.
/// </summary>
public class PauseAllResult
{
    /// <summary>
    /// Number of successful operations.
    /// </summary>
    public int SuccessCount { get; set; }

    /// <summary>
    /// Number of failed operations.
    /// </summary>
    public int FailedCount { get; set; }

    /// <summary>
    /// Number of items already in requested state.
    /// </summary>
    public int AlreadyInStateCount { get; set; }

    /// <summary>
    /// Total time taken.
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Errors that occurred.
    /// </summary>
    public Dictionary<string, string> Errors { get; set; } = new();
}

/// <summary>
/// Status of the pause coordination service.
/// </summary>
public class PauseServiceStatus
{
    /// <summary>
    /// Number of paused devices.
    /// </summary>
    public int PausedDeviceCount { get; set; }

    /// <summary>
    /// Number of paused folders.
    /// </summary>
    public int PausedFolderCount { get; set; }

    /// <summary>
    /// Number of registered pause callbacks.
    /// </summary>
    public int RegisteredCallbacks { get; set; }

    /// <summary>
    /// Graceful pause timeout.
    /// </summary>
    public TimeSpan GracefulPauseTimeout { get; set; }

    /// <summary>
    /// Statistics.
    /// </summary>
    public PauseServiceStatistics Statistics { get; set; } = new();
}

/// <summary>
/// Statistics for pause operations.
/// </summary>
public class PauseServiceStatistics
{
    public long TotalDevicePauses { get; set; }
    public long TotalDeviceResumes { get; set; }
    public long TotalFolderPauses { get; set; }
    public long TotalFolderResumes { get; set; }
    public long TotalGracefulWaits { get; set; }
}
