using CreatioHelper.Domain.Common;

namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Global bandwidth limits configuration
/// </summary>
public class GlobalBandwidthLimits
{
    public int MaxUploadKibps { get; set; } = 0; // 0 = unlimited
    public int MaxDownloadKibps { get; set; } = 0; // 0 = unlimited
    public bool Enabled { get; set; } = false;
}

/// <summary>
/// Per-device bandwidth limits configuration
/// </summary>
public class DeviceBandwidthLimits
{
    public int MaxSendKibps { get; set; } = 0; // 0 = unlimited
    public int MaxRecvKibps { get; set; } = 0; // 0 = unlimited
    public int Priority { get; set; } = 5; // 1-10 priority (10 = highest)
}

/// <summary>
/// Bandwidth configuration based on Syncthing settings
/// Supports both global and per-device bandwidth limits
/// </summary>
public class BandwidthConfiguration : ValueObject
{
    /// <summary>
    /// Maximum send speed in KiB/s (1024 bytes/sec), 0 = unlimited
    /// </summary>
    public int MaxSendKibps { get; set; } = 0;
    
    /// <summary>
    /// Maximum receive speed in KiB/s (1024 bytes/sec), 0 = unlimited
    /// </summary>
    public int MaxRecvKibps { get; set; } = 0;
    
    /// <summary>
    /// Whether to limit bandwidth in LAN connections
    /// </summary>
    public bool LimitBandwidthInLan { get; set; } = false;
    
    /// <summary>
    /// Global bandwidth limits that apply to all devices
    /// </summary>
    public GlobalBandwidthLimits GlobalLimits { get; set; } = new();
    
    /// <summary>
    /// Per-device bandwidth limits and priorities
    /// </summary>
    public Dictionary<string, DeviceBandwidthLimits> DeviceLimits { get; set; } = new();
    
    /// <summary>
    /// Syncthing-compatible device configurations
    /// </summary>
    public Dictionary<string, DeviceConfiguration>? DeviceConfigurations { get; set; } = new();

    /// <summary>
    /// Syncthing-compatible device configuration
    /// </summary>
    public class DeviceConfiguration
    {
        public int MaxSendKibps { get; set; } = 0; // 0 = unlimited
        public int MaxRecvKibps { get; set; } = 0; // 0 = unlimited
    }

    /// <summary>
    /// Burst allowance configuration
    /// </summary>
    public BurstConfiguration BurstConfig { get; set; } = new();

    /// <summary>
    /// Get effective send limits for a specific device
    /// </summary>
    public int GetEffectiveSendLimits(string deviceId)
    {
        // Check per-device limits first
        if (DeviceLimits.TryGetValue(deviceId, out var deviceLimits) && deviceLimits.MaxSendKibps > 0)
        {
            return deviceLimits.MaxSendKibps;
        }
        
        // Fall back to global limits
        if (GlobalLimits.Enabled && GlobalLimits.MaxUploadKibps > 0)
        {
            return GlobalLimits.MaxUploadKibps;
        }
        
        // Fall back to instance-level limits
        return MaxSendKibps;
    }
    
    /// <summary>
    /// Get effective receive limits for a specific device
    /// </summary>
    public int GetEffectiveRecvLimits(string deviceId)
    {
        // Check per-device limits first
        if (DeviceLimits.TryGetValue(deviceId, out var deviceLimits) && deviceLimits.MaxRecvKibps > 0)
        {
            return deviceLimits.MaxRecvKibps;
        }
        
        // Fall back to global limits
        if (GlobalLimits.Enabled && GlobalLimits.MaxDownloadKibps > 0)
        {
            return GlobalLimits.MaxDownloadKibps;
        }
        
        // Fall back to instance-level limits
        return MaxRecvKibps;
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return MaxSendKibps;
        yield return MaxRecvKibps;
        yield return LimitBandwidthInLan;
        yield return GlobalLimits;
        yield return DeviceLimits;
        yield return BurstConfig;
    }
}

/// <summary>
/// Configuration for burst allowance in bandwidth limiting
/// </summary>
public class BurstConfiguration : ValueObject
{
    /// <summary>
    /// Whether to allow bursts above the configured limit
    /// </summary>
    public bool AllowBurst { get; set; } = true;
    
    /// <summary>
    /// Maximum burst size in KB
    /// </summary>
    public int BurstSizeKb { get; set; } = 1024; // 1 MB burst
    
    /// <summary>
    /// Burst duration in seconds
    /// </summary>
    public int BurstDurationSeconds { get; set; } = 10;

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return AllowBurst;
        yield return BurstSizeKb;
        yield return BurstDurationSeconds;
    }
}

/// <summary>
/// Traffic shaping configuration for operation priorities
/// </summary>
public class TrafficShapingConfiguration : ValueObject
{
    /// <summary>
    /// Whether traffic shaping is enabled
    /// </summary>
    public bool Enabled { get; set; } = true;
    
    /// <summary>
    /// Priority levels for different operation types (1-10, 10 = highest priority)
    /// </summary>
    public Dictionary<string, int> Priorities { get; set; } = new()
    {
        { "MetadataSync", 9 },     // Index updates, folder info - highest priority
        { "BlockRequests", 7 },    // Block requests - high priority  
        { "FileTransfer", 5 },     // Actual file data transfer - normal priority
        { "Discovery", 6 },        // Device discovery - medium-high priority
        { "Heartbeat", 8 }         // Keep-alive messages - high priority
    };
    
    /// <summary>
    /// Maximum queue size per operation type
    /// </summary>
    public int MaxQueueSize { get; set; } = 100;
    
    /// <summary>
    /// Queue processing interval in milliseconds
    /// </summary>
    public int ProcessingIntervalMs { get; set; } = 100;

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Enabled;
        yield return Priorities;
        yield return MaxQueueSize;
        yield return ProcessingIntervalMs;
    }
}