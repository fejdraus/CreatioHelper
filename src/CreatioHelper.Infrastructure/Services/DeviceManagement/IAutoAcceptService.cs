using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services.DeviceManagement;

/// <summary>
/// Service for automatically accepting folders from trusted devices.
/// Based on Syncthing's AutoAcceptFolders functionality from lib/model/model.go
///
/// When a device has AutoAcceptFolders enabled:
/// - Folders shared by that device are automatically added to configuration
/// - The folder path is determined by configuration (default folder path + folder label)
/// - Events are emitted for auto-accepted folders
/// </summary>
public interface IAutoAcceptService
{
    /// <summary>
    /// Process a folder offer from a device, potentially auto-accepting it.
    /// </summary>
    /// <param name="deviceId">Device offering the folder</param>
    /// <param name="folderId">Folder ID</param>
    /// <param name="folderLabel">Folder label</param>
    /// <param name="receiveEncrypted">Whether the folder should be receive-encrypted</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the auto-accept check</returns>
    Task<AutoAcceptResult> ProcessFolderOfferAsync(
        string deviceId,
        string folderId,
        string folderLabel,
        bool receiveEncrypted = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enable or disable auto-accept for a device.
    /// </summary>
    Task SetAutoAcceptAsync(string deviceId, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a device has auto-accept enabled.
    /// </summary>
    Task<bool> IsAutoAcceptEnabledAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all devices with auto-accept enabled.
    /// </summary>
    Task<IEnumerable<SyncDevice>> GetAutoAcceptDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Set the default folder path for auto-accepted folders.
    /// </summary>
    void SetDefaultFolderPath(string path);

    /// <summary>
    /// Get the current service status.
    /// </summary>
    AutoAcceptServiceStatus GetStatus();
}

/// <summary>
/// Result of auto-accept folder processing.
/// </summary>
public class AutoAcceptResult
{
    /// <summary>
    /// Whether the folder was auto-accepted.
    /// </summary>
    public bool WasAutoAccepted { get; set; }

    /// <summary>
    /// Whether the folder was added to pending (if not auto-accepted).
    /// </summary>
    public bool AddedToPending { get; set; }

    /// <summary>
    /// Whether the folder already exists in configuration.
    /// </summary>
    public bool FolderAlreadyExists { get; set; }

    /// <summary>
    /// Whether the folder is in the device's ignored list.
    /// </summary>
    public bool IsIgnored { get; set; }

    /// <summary>
    /// The accepted/created folder (if auto-accepted).
    /// </summary>
    public SyncFolder? AcceptedFolder { get; set; }

    /// <summary>
    /// The path where the folder was created (if auto-accepted).
    /// </summary>
    public string? FolderPath { get; set; }

    /// <summary>
    /// Reason for not auto-accepting (if applicable).
    /// </summary>
    public string? Reason { get; set; }
}

/// <summary>
/// Status of the auto-accept service.
/// </summary>
public class AutoAcceptServiceStatus
{
    /// <summary>
    /// Whether the service is active.
    /// </summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Default folder path for auto-accepted folders.
    /// </summary>
    public string DefaultFolderPath { get; set; } = string.Empty;

    /// <summary>
    /// Number of devices with auto-accept enabled.
    /// </summary>
    public int AutoAcceptDeviceCount { get; set; }

    /// <summary>
    /// Statistics.
    /// </summary>
    public AutoAcceptStatistics Statistics { get; set; } = new();
}

/// <summary>
/// Statistics for auto-accept operations.
/// </summary>
public class AutoAcceptStatistics
{
    /// <summary>
    /// Total folders auto-accepted.
    /// </summary>
    public long TotalFoldersAutoAccepted { get; set; }

    /// <summary>
    /// Total folder offers processed.
    /// </summary>
    public long TotalOffersProcessed { get; set; }

    /// <summary>
    /// Total offers rejected due to ignored list.
    /// </summary>
    public long OffersIgnored { get; set; }

    /// <summary>
    /// Total offers added to pending.
    /// </summary>
    public long OffersAddedToPending { get; set; }
}
