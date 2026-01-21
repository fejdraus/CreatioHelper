using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Unified configuration manager for folders and devices.
/// Uses config.xml as the single source of truth (like Syncthing).
/// Runtime statistics are kept in memory and optionally persisted separately.
/// </summary>
public interface IConfigurationManager
{
    /// <summary>
    /// Event raised when configuration changes
    /// </summary>
    event EventHandler<ConfigurationChangedEventArgs>? ConfigurationChanged;

    #region Initialization

    /// <summary>
    /// Initialize the configuration manager, loading from config.xml
    /// </summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if configuration has been initialized
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// Get the current configuration (read-only snapshot)
    /// </summary>
    ConfigXml GetCurrentConfig();

    #endregion

    #region Folder Operations

    /// <summary>
    /// Get folder by ID
    /// </summary>
    Task<SyncFolder?> GetFolderAsync(string folderId);

    /// <summary>
    /// Get all folders
    /// </summary>
    Task<IReadOnlyList<SyncFolder>> GetAllFoldersAsync();

    /// <summary>
    /// Add or update a folder
    /// </summary>
    Task UpsertFolderAsync(SyncFolder folder);

    /// <summary>
    /// Delete a folder
    /// </summary>
    Task DeleteFolderAsync(string folderId);

    /// <summary>
    /// Get folders shared with a specific device
    /// </summary>
    Task<IReadOnlyList<SyncFolder>> GetFoldersSharedWithDeviceAsync(string deviceId);

    /// <summary>
    /// Update folder runtime statistics (in memory only, not persisted to config.xml)
    /// </summary>
    void UpdateFolderStatistics(string folderId, long fileCount, long totalSize, DateTime? lastScan);

    /// <summary>
    /// Get folder runtime statistics
    /// </summary>
    FolderStatistics? GetFolderStatistics(string folderId);

    #endregion

    #region Device Operations

    /// <summary>
    /// Get device by ID
    /// </summary>
    Task<SyncDevice?> GetDeviceAsync(string deviceId);

    /// <summary>
    /// Get all devices
    /// </summary>
    Task<IReadOnlyList<SyncDevice>> GetAllDevicesAsync();

    /// <summary>
    /// Add or update a device
    /// </summary>
    Task UpsertDeviceAsync(SyncDevice device);

    /// <summary>
    /// Delete a device
    /// </summary>
    Task DeleteDeviceAsync(string deviceId);

    /// <summary>
    /// Get devices for a specific folder
    /// </summary>
    Task<IReadOnlyList<SyncDevice>> GetDevicesForFolderAsync(string folderId);

    /// <summary>
    /// Update device last seen time (in memory, batched to config.xml periodically)
    /// </summary>
    void UpdateDeviceLastSeen(string deviceId, DateTime lastSeen);

    /// <summary>
    /// Update device runtime statistics (in memory only)
    /// </summary>
    void UpdateDeviceStatistics(string deviceId, long bytesReceived, long bytesSent, DateTime lastActivity);

    /// <summary>
    /// Get device runtime statistics
    /// </summary>
    DeviceStatistics? GetDeviceStatistics(string deviceId);

    #endregion

    #region Configuration Operations

    /// <summary>
    /// Get GUI configuration
    /// </summary>
    ConfigXmlGui GetGuiConfig();

    /// <summary>
    /// Update GUI configuration
    /// </summary>
    Task UpdateGuiConfigAsync(ConfigXmlGui gui);

    /// <summary>
    /// Get options configuration
    /// </summary>
    ConfigXmlOptions GetOptionsConfig();

    /// <summary>
    /// Update options configuration
    /// </summary>
    Task UpdateOptionsConfigAsync(ConfigXmlOptions options);

    /// <summary>
    /// Save current configuration to config.xml
    /// </summary>
    Task SaveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Reload configuration from config.xml
    /// </summary>
    Task ReloadAsync(CancellationToken cancellationToken = default);

    #endregion
}

/// <summary>
/// Event args for configuration changes
/// </summary>
public class ConfigurationChangedEventArgs : EventArgs
{
    public ConfigurationChangeType ChangeType { get; init; }
    public string? FolderId { get; init; }
    public string? DeviceId { get; init; }
}

/// <summary>
/// Type of configuration change
/// </summary>
public enum ConfigurationChangeType
{
    FolderAdded,
    FolderUpdated,
    FolderRemoved,
    DeviceAdded,
    DeviceUpdated,
    DeviceRemoved,
    OptionsUpdated,
    GuiUpdated,
    FullReload
}

/// <summary>
/// Runtime statistics for a folder (not persisted to config.xml)
/// </summary>
public class FolderStatistics
{
    public string FolderId { get; init; } = string.Empty;
    public long FileCount { get; set; }
    public long TotalSize { get; set; }
    public DateTime? LastScan { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Runtime statistics for a device (not persisted to config.xml)
/// </summary>
public class DeviceStatistics
{
    public string DeviceId { get; init; } = string.Empty;
    public long BytesReceived { get; set; }
    public long BytesSent { get; set; }
    public DateTime? LastSeen { get; set; }
    public DateTime? LastActivity { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
}
