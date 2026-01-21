using System.Text.Json.Serialization;

namespace CreatioHelper.WebUI.Models;

/// <summary>
/// System status information
/// </summary>
public class SystemStatus
{
    [JsonPropertyName("myID")]
    public string? MyId { get; set; } = string.Empty;

    [JsonPropertyName("uptime")]
    public long? Uptime { get; set; }

    [JsonPropertyName("startTime")]
    public DateTime? StartTime { get; set; }

    [JsonPropertyName("goroutines")]
    public int? Goroutines { get; set; }

    [JsonPropertyName("cpuPercent")]
    public double? CpuPercent { get; set; }

    [JsonPropertyName("alloc")]
    public long? Alloc { get; set; }

    [JsonPropertyName("sys")]
    public long? Sys { get; set; }

    // Extended memory info
    [JsonPropertyName("appMemory")]
    public long? AppMemory { get; set; }

    [JsonPropertyName("osMemoryUsed")]
    public long? OsMemoryUsed { get; set; }

    [JsonPropertyName("totalPhysicalMemory")]
    public long? TotalPhysicalMemory { get; set; }

    [JsonPropertyName("numCPU")]
    public int? NumCpu { get; set; }

    [JsonPropertyName("discoveryEnabled")]
    public bool? DiscoveryEnabled { get; set; }

    [JsonPropertyName("connectionServiceStatus")]
    public Dictionary<string, object>? ConnectionServiceStatus { get; set; }

    [JsonIgnore]
    public TimeSpan UptimeSpan => TimeSpan.FromSeconds(Uptime ?? 0);
}

/// <summary>
/// System configuration
/// </summary>
public class SystemConfig
{
    [JsonPropertyName("folders")]
    public FolderConfig[] Folders { get; set; } = [];

    [JsonPropertyName("devices")]
    public DeviceConfig[] Devices { get; set; } = [];

    [JsonPropertyName("gui")]
    public GuiConfig? Gui { get; set; }

    [JsonPropertyName("options")]
    public OptionsConfig? Options { get; set; }
}

public class GuiConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("address")]
    public string Address { get; set; } = "127.0.0.1:8384";

    [JsonPropertyName("useTLS")]
    public bool UseTls { get; set; } = true;

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "dark";

    [JsonPropertyName("debugging")]
    public bool Debugging { get; set; }

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; set; }
}

public class OptionsConfig
{
    [JsonPropertyName("listenAddresses")]
    public string[]? ListenAddresses { get; set; } = ["default"];

    [JsonPropertyName("globalAnnounceEnabled")]
    public bool? GlobalAnnounceEnabled { get; set; } = true;

    [JsonPropertyName("localAnnounceEnabled")]
    public bool? LocalAnnounceEnabled { get; set; } = true;

    [JsonPropertyName("natEnabled")]
    public bool? NatEnabled { get; set; } = true;

    [JsonPropertyName("relaysEnabled")]
    public bool? RelaysEnabled { get; set; } = true;

    [JsonPropertyName("maxSendKbps")]
    public int? MaxSendKbps { get; set; }

    [JsonPropertyName("maxRecvKbps")]
    public int? MaxRecvKbps { get; set; }

    [JsonPropertyName("reconnectionIntervalS")]
    public int? ReconnectionIntervalS { get; set; } = 60;

    [JsonPropertyName("startBrowser")]
    public bool? StartBrowser { get; set; } = true;

    [JsonPropertyName("limitBandwidthInLan")]
    public bool? LimitBandwidthInLan { get; set; }

    [JsonPropertyName("globalAnnounceServers")]
    public string[]? GlobalAnnounceServers { get; set; }

    [JsonPropertyName("relayServers")]
    public string[]? RelayServers { get; set; }

    [JsonPropertyName("deviceName")]
    public string? DeviceName { get; set; }

    [JsonPropertyName("defaultFolderPath")]
    public string? DefaultFolderPath { get; set; }

    [JsonPropertyName("ignorePerms")]
    public bool? IgnorePerms { get; set; }

    [JsonPropertyName("autoNormalizeFilenames")]
    public bool? AutoNormalizeFilenames { get; set; }

    [JsonPropertyName("crashReportingEnabled")]
    public bool? CrashReportingEnabled { get; set; }

    [JsonPropertyName("hashers")]
    public int? Hashers { get; set; }

    [JsonPropertyName("maxFolderConcurrency")]
    public int? MaxFolderConcurrency { get; set; }

    [JsonPropertyName("setLowPriority")]
    public bool? SetLowPriority { get; set; }

    [JsonPropertyName("progressUpdateIntervalS")]
    public int? ProgressUpdateIntervalS { get; set; }
}

/// <summary>
/// Discovery status
/// </summary>
public class DiscoveryStatus
{
    [JsonPropertyName("global")]
    public Dictionary<string, DiscoveryServerStatus>? Global { get; set; }

    [JsonPropertyName("local")]
    public DiscoveryLocalStatus? Local { get; set; }

    [JsonPropertyName("localAnnounceEnabled")]
    public bool? LocalAnnounceEnabled { get; set; }

    [JsonPropertyName("globalAnnounceEnabled")]
    public bool? GlobalAnnounceEnabled { get; set; }

    [JsonPropertyName("globalAnnounceServers")]
    public string[]? GlobalAnnounceServers { get; set; }

    [JsonPropertyName("externalAddresses")]
    public string[]? ExternalAddresses { get; set; }
}

public class DiscoveryServerStatus
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("lastSeen")]
    public DateTime? LastSeen { get; set; }
}

public class DiscoveryLocalStatus
{
    [JsonPropertyName("multicastStatus")]
    public string MulticastStatus { get; set; } = string.Empty;
}

/// <summary>
/// Debug information
/// </summary>
public class DebugInfo
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("os")]
    public string Os { get; set; } = string.Empty;

    [JsonPropertyName("arch")]
    public string Arch { get; set; } = string.Empty;

    [JsonPropertyName("cpus")]
    public int Cpus { get; set; }

    [JsonPropertyName("goroutines")]
    public int Goroutines { get; set; }

    [JsonPropertyName("connectionStats")]
    public Dictionary<string, object>? ConnectionStats { get; set; }
}

/// <summary>
/// Log entry for system logs
/// </summary>
public class LogEntry
{
    [JsonPropertyName("when")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("level")]
    public string Level { get; set; } = "info";

    [JsonPropertyName("facility")]
    public string Facility { get; set; } = "app";

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// System version information
/// </summary>
public class SystemVersionInfo
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("os")]
    public string? Os { get; set; }

    [JsonPropertyName("arch")]
    public string? Arch { get; set; }

    [JsonPropertyName("longVersion")]
    public string? LongVersion { get; set; }
}
