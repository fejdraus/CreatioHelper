using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Service for reading and writing Syncthing-compatible config.xml files.
/// Provides full compatibility with Syncthing's configuration format.
/// </summary>
public interface IConfigXmlService
{
    /// <summary>
    /// Path to the config.xml file
    /// </summary>
    string ConfigPath { get; }

    /// <summary>
    /// Load configuration from config.xml file.
    /// Creates default configuration if file doesn't exist.
    /// </summary>
    Task<ConfigXml> LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Save configuration to config.xml file.
    /// Creates backup of existing file before saving.
    /// </summary>
    Task SaveAsync(ConfigXml config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if config.xml file exists
    /// </summary>
    bool ConfigExists();

    /// <summary>
    /// Create default configuration with auto-generated device ID
    /// </summary>
    Task<ConfigXml> CreateDefaultConfigAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Convert ConfigXml to SyncConfiguration (for internal use)
    /// </summary>
    SyncConfiguration ToSyncConfiguration(ConfigXml config);

    /// <summary>
    /// Convert SyncConfiguration to ConfigXml (for export)
    /// </summary>
    ConfigXml FromSyncConfiguration(SyncConfiguration config, IEnumerable<SyncDevice> devices, IEnumerable<SyncFolder> folders);

    /// <summary>
    /// Validate configuration
    /// </summary>
    ConfigValidationResult Validate(ConfigXml config);

    /// <summary>
    /// Get configuration directory path
    /// </summary>
    string GetConfigDirectory();
}

/// <summary>
/// Result of configuration validation
/// </summary>
public class ConfigValidationResult
{
    public bool IsValid { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
