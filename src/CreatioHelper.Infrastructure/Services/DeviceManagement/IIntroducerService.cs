using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services.DeviceManagement;

/// <summary>
/// Service for handling device introductions.
/// Based on Syncthing's introducer functionality from lib/model/model.go
///
/// When a device is marked as an introducer:
/// - It can introduce other devices to us
/// - Introduced devices are automatically added to our configuration
/// - If introducer shares a folder with us and an introduced device, we add that device too
/// </summary>
public interface IIntroducerService
{
    /// <summary>
    /// Process an introduction from an introducer device.
    /// Called when receiving ClusterConfig from an introducer.
    /// </summary>
    /// <param name="introducerDeviceId">The device ID of the introducer</param>
    /// <param name="introducedDevices">Devices being introduced</param>
    /// <param name="folderShares">Folder shares from the introducer's ClusterConfig</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result of the introduction processing</returns>
    Task<IntroductionResult> ProcessIntroductionAsync(
        string introducerDeviceId,
        IEnumerable<IntroducedDevice> introducedDevices,
        IEnumerable<IntroducedFolderShare> folderShares,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mark a device as an introducer.
    /// </summary>
    Task SetIntroducerAsync(string deviceId, bool isIntroducer, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all devices introduced by a specific introducer.
    /// </summary>
    Task<IEnumerable<SyncDevice>> GetIntroducedDevicesAsync(string introducerDeviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a device was introduced (not manually added).
    /// </summary>
    Task<bool> IsIntroducedDeviceAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove devices that were introduced by a specific introducer.
    /// Called when an introducer is removed or its SkipIntroductionRemovals is false.
    /// </summary>
    Task RemoveIntroducedDevicesAsync(string introducerDeviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the current status of the introducer service.
    /// </summary>
    IntroducerServiceStatus GetStatus();
}

/// <summary>
/// Represents a device being introduced by an introducer.
/// </summary>
public class IntroducedDevice
{
    /// <summary>
    /// Device ID being introduced.
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Device name.
    /// </summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// Device addresses.
    /// </summary>
    public List<string> Addresses { get; set; } = new();

    /// <summary>
    /// Certificate name (for TLS verification).
    /// </summary>
    public string CertificateName { get; set; } = string.Empty;

    /// <summary>
    /// Compression mode.
    /// </summary>
    public string Compression { get; set; } = "metadata";

    /// <summary>
    /// Whether this introduced device is also an introducer.
    /// </summary>
    public bool IsIntroducer { get; set; }

    /// <summary>
    /// Maximum request size in KiB.
    /// </summary>
    public int MaxRequestKiB { get; set; } = 2048;
}

/// <summary>
/// Represents a folder share from an introducer's ClusterConfig.
/// </summary>
public class IntroducedFolderShare
{
    /// <summary>
    /// Folder ID.
    /// </summary>
    public string FolderId { get; set; } = string.Empty;

    /// <summary>
    /// Folder label.
    /// </summary>
    public string FolderLabel { get; set; } = string.Empty;

    /// <summary>
    /// Devices sharing this folder (including their Device IDs).
    /// </summary>
    public List<string> SharedWithDevices { get; set; } = new();

    /// <summary>
    /// Whether the folder is encrypted on the introducer.
    /// </summary>
    public bool Encrypted { get; set; }
}

/// <summary>
/// Result of processing introductions.
/// </summary>
public class IntroductionResult
{
    /// <summary>
    /// Whether any changes were made.
    /// </summary>
    public bool ChangesMade { get; set; }

    /// <summary>
    /// Devices that were newly added.
    /// </summary>
    public List<string> AddedDevices { get; set; } = new();

    /// <summary>
    /// Devices that were updated.
    /// </summary>
    public List<string> UpdatedDevices { get; set; } = new();

    /// <summary>
    /// Devices that were removed (if introducer was removed).
    /// </summary>
    public List<string> RemovedDevices { get; set; } = new();

    /// <summary>
    /// Folder shares that were added to devices.
    /// </summary>
    public List<FolderShareChange> FolderShareChanges { get; set; } = new();

    /// <summary>
    /// Errors that occurred during processing.
    /// </summary>
    public List<string> Errors { get; set; } = new();
}

/// <summary>
/// Represents a change in folder sharing.
/// </summary>
public class FolderShareChange
{
    public string FolderId { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public FolderShareChangeType ChangeType { get; set; }
}

public enum FolderShareChangeType
{
    Added,
    Removed
}

/// <summary>
/// Status of the introducer service.
/// </summary>
public class IntroducerServiceStatus
{
    /// <summary>
    /// Whether the service is running.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// Number of introducer devices.
    /// </summary>
    public int IntroducerCount { get; set; }

    /// <summary>
    /// Number of introduced devices.
    /// </summary>
    public int IntroducedDeviceCount { get; set; }

    /// <summary>
    /// Last introduction processed.
    /// </summary>
    public DateTime? LastIntroductionTime { get; set; }

    /// <summary>
    /// Statistics about introductions.
    /// </summary>
    public IntroducerStatistics Statistics { get; set; } = new();
}

/// <summary>
/// Statistics about introductions.
/// </summary>
public class IntroducerStatistics
{
    public long TotalIntroductionsProcessed { get; set; }
    public long DevicesAdded { get; set; }
    public long DevicesRemoved { get; set; }
    public long FolderSharesAdded { get; set; }
}
