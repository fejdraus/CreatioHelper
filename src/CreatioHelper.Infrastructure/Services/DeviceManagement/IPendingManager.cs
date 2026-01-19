using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services.DeviceManagement;

/// <summary>
/// Manages pending devices and folders awaiting user acceptance.
/// Based on Syncthing's pending device/folder handling from lib/model/model.go
///
/// Key behaviors:
/// - Track pending devices (devices that connected but aren't in config)
/// - Track pending folders (folders offered by connected devices)
/// - Provide accept/reject operations
/// - Clean up stale pending items
/// </summary>
public interface IPendingManager
{
    /// <summary>
    /// Add a pending device that connected but isn't in our config.
    /// </summary>
    Task AddPendingDeviceAsync(PendingDevice device, CancellationToken cancellationToken = default);

    /// <summary>
    /// Add a pending folder offered by a device.
    /// </summary>
    Task AddPendingFolderAsync(PendingFolder folder, CancellationToken cancellationToken = default);

    /// <summary>
    /// Accept a pending device - add it to configuration.
    /// </summary>
    Task<SyncDevice> AcceptDeviceAsync(string deviceId, string? customName = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Accept a pending folder - add it to configuration.
    /// </summary>
    Task<SyncFolder> AcceptFolderAsync(string folderId, string localPath, string? customLabel = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reject (ignore) a pending device.
    /// </summary>
    Task RejectDeviceAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reject (ignore) a pending folder from a specific device.
    /// </summary>
    Task RejectFolderAsync(string folderId, string offeredByDeviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all pending devices.
    /// </summary>
    Task<IEnumerable<PendingDevice>> GetPendingDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all pending folders.
    /// </summary>
    Task<IEnumerable<PendingFolder>> GetPendingFoldersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get pending folders offered by a specific device.
    /// </summary>
    Task<IEnumerable<PendingFolder>> GetPendingFoldersForDeviceAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a device is pending.
    /// </summary>
    Task<bool> IsDevicePendingAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if a folder is pending from a specific device.
    /// </summary>
    Task<bool> IsFolderPendingAsync(string folderId, string offeredByDeviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove stale pending items that haven't been seen for a while.
    /// </summary>
    Task CleanupStalePendingItemsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clear all pending devices and folders.
    /// </summary>
    Task ClearAllPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the current status.
    /// </summary>
    PendingManagerStatus GetStatus();
}

/// <summary>
/// Represents a device that connected but isn't in our configuration.
/// </summary>
public class PendingDevice
{
    /// <summary>
    /// Device ID.
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Device name (from ClusterConfig).
    /// </summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// Address the device connected from.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// When the device was first seen.
    /// </summary>
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the device was last seen.
    /// </summary>
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Connection type used.
    /// </summary>
    public string ConnectionType { get; set; } = "tcp";

    /// <summary>
    /// Client version string.
    /// </summary>
    public string ClientVersion { get; set; } = string.Empty;

    /// <summary>
    /// Number of times the device has connected.
    /// </summary>
    public int ConnectionAttempts { get; set; } = 1;

    /// <summary>
    /// Whether the device has been rejected (ignored).
    /// </summary>
    public bool IsRejected { get; set; }
}

/// <summary>
/// Represents a folder offered by a connected device that we don't have.
/// </summary>
public class PendingFolder
{
    /// <summary>
    /// Folder ID.
    /// </summary>
    public string FolderId { get; set; } = string.Empty;

    /// <summary>
    /// Folder label (from ClusterConfig).
    /// </summary>
    public string FolderLabel { get; set; } = string.Empty;

    /// <summary>
    /// Device ID that offered this folder.
    /// </summary>
    public string OfferedByDeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Device name that offered this folder.
    /// </summary>
    public string OfferedByDeviceName { get; set; } = string.Empty;

    /// <summary>
    /// When the folder was first offered.
    /// </summary>
    public DateTime FirstOffered { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the folder was last seen in ClusterConfig.
    /// </summary>
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Whether the folder is encrypted on the offering device.
    /// </summary>
    public bool ReceiveEncrypted { get; set; }

    /// <summary>
    /// Whether this folder has been rejected (ignored).
    /// </summary>
    public bool IsRejected { get; set; }
}

/// <summary>
/// Status of the pending manager.
/// </summary>
public class PendingManagerStatus
{
    /// <summary>
    /// Number of pending devices.
    /// </summary>
    public int PendingDeviceCount { get; set; }

    /// <summary>
    /// Number of pending folders.
    /// </summary>
    public int PendingFolderCount { get; set; }

    /// <summary>
    /// Number of rejected devices.
    /// </summary>
    public int RejectedDeviceCount { get; set; }

    /// <summary>
    /// Number of rejected folders.
    /// </summary>
    public int RejectedFolderCount { get; set; }

    /// <summary>
    /// Time of last pending item addition.
    /// </summary>
    public DateTime? LastPendingAddition { get; set; }

    /// <summary>
    /// Statistics.
    /// </summary>
    public PendingManagerStatistics Statistics { get; set; } = new();
}

/// <summary>
/// Statistics for the pending manager.
/// </summary>
public class PendingManagerStatistics
{
    public long TotalDevicesAccepted { get; set; }
    public long TotalDevicesRejected { get; set; }
    public long TotalFoldersAccepted { get; set; }
    public long TotalFoldersRejected { get; set; }
}
