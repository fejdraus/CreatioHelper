using System.ComponentModel.DataAnnotations;

namespace CreatioHelper.Agent.Configuration;

/// <summary>
/// Settings for automatic service stop/start based on Syncthing synchronization
/// </summary>
public class SyncthingAutoStopSettings
{
    /// <summary>
    /// Enable/disable automatic service management during sync
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Local Syncthing API URL
    /// </summary>
    [Url]
    public string SyncthingApiUrl { get; set; } = "http://127.0.0.1:8384";

    /// <summary>
    /// Syncthing API key
    /// </summary>
    public string? SyncthingApiKey { get; set; }

    /// <summary>
    /// List of folder IDs to monitor for incoming sync
    /// </summary>
    public List<string> MonitoredFolders { get; set; } = new();

    /// <summary>
    /// Timeout in seconds to wait after last file change before starting services
    /// Default: 30 seconds
    /// </summary>
    [Range(1, 3600, ErrorMessage = "IdleTimeoutSeconds must be between 1 and 3600")]
    public int IdleTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Interval in seconds for checking sync completion status
    /// Default: 5 seconds
    /// </summary>
    [Range(1, 300, ErrorMessage = "CompletionCheckIntervalSeconds must be between 1 and 300")]
    public int CompletionCheckIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Number of consecutive stable checks required before starting services
    /// Default: 2
    /// </summary>
    [Range(1, 100, ErrorMessage = "RequiredStableChecks must be between 1 and 100")]
    public int RequiredStableChecks { get; set; } = 2;

    /// <summary>
    /// Timeout in seconds for service stop/start operations
    /// Default: 120 seconds (2 minutes)
    /// Prevents agent from hanging if IIS/systemd/launchd commands don't respond
    /// </summary>
    [Range(10, 1800, ErrorMessage = "ServiceOperationTimeoutSeconds must be between 10 and 1800")]
    public int ServiceOperationTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Maximum time in minutes to wait for sync completion
    /// Default: 30 minutes
    /// Prevents services from staying stopped forever if sync gets stuck
    /// </summary>
    [Range(1, 1440, ErrorMessage = "MaxSyncWaitTimeMinutes must be between 1 and 1440")]
    public int MaxSyncWaitTimeMinutes { get; set; } = 30;

    /// <summary>
    /// Windows-specific settings
    /// </summary>
    public WindowsServiceSettings? Windows { get; set; }

    /// <summary>
    /// Linux-specific settings
    /// </summary>
    public LinuxServiceSettings? Linux { get; set; }

    /// <summary>
    /// MacOS-specific settings
    /// </summary>
    public MacOsServiceSettings? MacOS { get; set; }
}

/// <summary>
/// Windows IIS settings
/// </summary>
public class WindowsServiceSettings
{
    /// <summary>
    /// IIS Application Pool name
    /// </summary>
    public string? AppPoolName { get; set; }

    /// <summary>
    /// IIS Website name
    /// </summary>
    public string? SiteName { get; set; }

    /// <summary>
    /// Windows Service name (alternative to IIS)
    /// </summary>
    public string? ServiceName { get; set; }
}

/// <summary>
/// Linux systemd settings
/// </summary>
public class LinuxServiceSettings
{
    /// <summary>
    /// systemd service name (e.g., "creatio.service")
    /// </summary>
    public string? ServiceName { get; set; }
}

/// <summary>
/// MacOS launchd settings
/// </summary>
public class MacOsServiceSettings
{
    /// <summary>
    /// launchd service name (e.g., "com.creatio.app")
    /// </summary>
    public string? ServiceName { get; set; }
}
