namespace CreatioHelper.Infrastructure.Services.Network.Discovery;

/// <summary>
/// Unified device discovery manager interface.
/// Coordinates local discovery, global discovery, and static addresses.
/// Based on Syncthing's discovery coordination from lib/discover/manager.go
/// </summary>
public interface IDiscoveryManager : IDisposable
{
    /// <summary>
    /// Start all configured discovery services
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop all discovery services
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Lookup addresses for a device using all available sources
    /// </summary>
    /// <param name="deviceId">Device ID to lookup</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of discovered addresses</returns>
    Task<DiscoveryResult> LookupAsync(string deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Add static addresses for a device
    /// </summary>
    void AddStaticAddresses(string deviceId, IEnumerable<string> addresses);

    /// <summary>
    /// Remove static addresses for a device
    /// </summary>
    void RemoveStaticAddresses(string deviceId);

    /// <summary>
    /// Get current discovery status
    /// </summary>
    DiscoveryStatus GetStatus();

    /// <summary>
    /// Get discovery cache statistics
    /// </summary>
    CacheStatistics GetCacheStatistics();

    /// <summary>
    /// Event raised when a device is discovered
    /// </summary>
    event EventHandler<DeviceDiscoveredArgs>? DeviceDiscovered;

    /// <summary>
    /// Is local discovery enabled
    /// </summary>
    bool LocalDiscoveryEnabled { get; }

    /// <summary>
    /// Is global discovery enabled
    /// </summary>
    bool GlobalDiscoveryEnabled { get; }
}

/// <summary>
/// Result of a device discovery lookup
/// </summary>
public class DiscoveryResult
{
    /// <summary>
    /// Device ID that was looked up
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// All discovered addresses, ordered by priority (LAN first)
    /// </summary>
    public List<DiscoveredAddress> Addresses { get; set; } = new();

    /// <summary>
    /// Whether the lookup was served from cache
    /// </summary>
    public bool FromCache { get; set; }

    /// <summary>
    /// Sources that returned results
    /// </summary>
    public HashSet<DiscoveryCacheSource> Sources { get; set; } = new();

    /// <summary>
    /// Lookup duration
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Returns true if any addresses were found
    /// </summary>
    public bool Found => Addresses.Count > 0;

    /// <summary>
    /// Get addresses as URI strings
    /// </summary>
    public IEnumerable<string> GetAddressStrings() => Addresses.Select(a => a.Address);
}

/// <summary>
/// A discovered address with metadata
/// </summary>
public class DiscoveredAddress
{
    /// <summary>
    /// Address URI (tcp://host:port, quic://host:port, relay://...)
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Source of this address
    /// </summary>
    public DiscoveryCacheSource Source { get; set; }

    /// <summary>
    /// Priority (lower is better)
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Whether this is a LAN address
    /// </summary>
    public bool IsLan { get; set; }

    /// <summary>
    /// When this address was discovered
    /// </summary>
    public DateTime DiscoveredAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When this address expires from cache
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// Discovery service status
/// </summary>
public class DiscoveryStatus
{
    /// <summary>
    /// Is the discovery manager running
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// Local discovery status
    /// </summary>
    public LocalDiscoveryStatus LocalDiscovery { get; set; } = new();

    /// <summary>
    /// Global discovery status
    /// </summary>
    public GlobalDiscoveryStatus GlobalDiscovery { get; set; } = new();

    /// <summary>
    /// Static address configuration status
    /// </summary>
    public StaticAddressStatus StaticAddresses { get; set; } = new();
}

/// <summary>
/// Local discovery status
/// </summary>
public class LocalDiscoveryStatus
{
    public bool Enabled { get; set; }
    public bool Running { get; set; }
    public int Port { get; set; }
    public int DiscoveredDeviceCount { get; set; }
    public DateTime LastAnnouncement { get; set; }
    public DateTime LastReceived { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Global discovery status
/// </summary>
public class GlobalDiscoveryStatus
{
    public bool Enabled { get; set; }
    public bool Running { get; set; }
    public List<string> Servers { get; set; } = new();
    public Dictionary<string, GlobalDiscoveryServerStatus> ServerStatuses { get; set; } = new();
    public DateTime LastAnnouncement { get; set; }
    public DateTime? NextReannouncement { get; set; }
}

/// <summary>
/// Status of a single global discovery server
/// </summary>
public class GlobalDiscoveryServerStatus
{
    public string Server { get; set; } = string.Empty;
    public bool Available { get; set; }
    public string? Error { get; set; }
    public DateTime? LastSuccess { get; set; }
    public DateTime? RetryAfter { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
}

/// <summary>
/// Static address configuration status
/// </summary>
public class StaticAddressStatus
{
    public int DeviceCount { get; set; }
    public int TotalAddressCount { get; set; }
}

/// <summary>
/// Event args for device discovered event
/// </summary>
public class DeviceDiscoveredArgs : EventArgs
{
    public string DeviceId { get; }
    public List<DiscoveredAddress> Addresses { get; }
    public DiscoveryCacheSource Source { get; }

    public DeviceDiscoveredArgs(string deviceId, List<DiscoveredAddress> addresses, DiscoveryCacheSource source)
    {
        DeviceId = deviceId;
        Addresses = addresses;
        Source = source;
    }
}
