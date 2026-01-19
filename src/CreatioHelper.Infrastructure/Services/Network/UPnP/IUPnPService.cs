namespace CreatioHelper.Infrastructure.Services.Network.UPnP;

/// <summary>
/// Interface for UPnP Internet Gateway Device discovery and port mapping
/// Compatible with Syncthing's UPnP implementation
/// </summary>
public interface IUPnPService
{
    /// <summary>
    /// Discovers UPnP Internet Gateway Devices on the network
    /// </summary>
    Task<List<IUPnPDevice>> DiscoverDevicesAsync(TimeSpan timeout = default, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the external IP address from the first available UPnP device
    /// </summary>
    Task<string?> GetExternalIPAddressAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Maps an external port to an internal port on the local machine
    /// </summary>
    Task<bool> AddPortMappingAsync(int externalPort, int internalPort, string protocol = "TCP", 
        string description = "Syncthing", int leaseDuration = 0, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a port mapping
    /// </summary>
    Task<bool> DeletePortMappingAsync(int externalPort, string protocol = "TCP", CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all existing port mappings
    /// </summary>
    Task<List<UPnPPortMapping>> GetPortMappingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if UPnP is available and working
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a UPnP Internet Gateway Device
/// </summary>
public interface IUPnPDevice
{
    /// <summary>
    /// Unique identifier for this device
    /// </summary>
    string DeviceId { get; }

    /// <summary>
    /// Friendly name of the device
    /// </summary>
    string FriendlyName { get; }

    /// <summary>
    /// Device type (IGDv1 or IGDv2)
    /// </summary>
    string DeviceType { get; }

    /// <summary>
    /// Whether this device supports IPv6
    /// </summary>
    bool SupportsIPv6 { get; }

    /// <summary>
    /// Control URL for the device
    /// </summary>
    string ControlUrl { get; }

    /// <summary>
    /// Local IP address used to reach this device
    /// </summary>
    string LocalIPAddress { get; }

    /// <summary>
    /// Maps a port on this device
    /// </summary>
    Task<bool> AddPortMappingAsync(int externalPort, int internalPort, string localIP, 
        string protocol, string description, int leaseDuration, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a port mapping from this device
    /// </summary>
    Task<bool> DeletePortMappingAsync(int externalPort, string protocol, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the external IP address from this device
    /// </summary>
    Task<string?> GetExternalIPAddressAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets port mappings from this device
    /// </summary>
    Task<List<UPnPPortMapping>> GetPortMappingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific port mapping entry by external port and protocol
    /// </summary>
    Task<UPnPPortMapping?> GetSpecificPortMappingEntryAsync(int externalPort, string protocol, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a UPnP port mapping entry
/// </summary>
public class UPnPPortMapping
{
    public int ExternalPort { get; set; }
    public int InternalPort { get; set; }
    public string InternalClient { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int LeaseDuration { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}