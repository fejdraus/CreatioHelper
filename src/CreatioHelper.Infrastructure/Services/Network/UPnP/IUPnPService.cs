using System.Net;

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
    /// IGDv2: Request any available external port (router assigns the port)
    /// </summary>
    Task<UPnPAnyPortMappingResult?> AddAnyPortMappingAsync(int internalPort, string protocol = "TCP",
        string description = "Syncthing", int leaseDuration = 3600, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a port mapping
    /// </summary>
    Task<bool> DeletePortMappingAsync(int externalPort, string protocol = "TCP", CancellationToken cancellationToken = default);

    /// <summary>
    /// IGDv2: Delete a range of port mappings
    /// </summary>
    Task<bool> DeletePortMappingRangeAsync(int startPort, int endPort, string protocol = "TCP", CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all existing port mappings
    /// </summary>
    Task<List<UPnPPortMapping>> GetPortMappingsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if UPnP is available and working
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// IPv6: Add a firewall pinhole for incoming connections
    /// </summary>
    Task<UPnPPinholeResult?> AddPinholeAsync(IPAddress remoteHost, int remotePort,
        IPAddress internalClient, int internalPort, string protocol, int leaseTime,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// IPv6: Delete a firewall pinhole by its unique ID
    /// </summary>
    Task<bool> DeletePinholeAsync(int uniqueId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the status of the UPnP service
    /// </summary>
    UPnPServiceStatus GetStatus();
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

/// <summary>
/// Result of IGDv2 AddAnyPortMapping operation
/// </summary>
public class UPnPAnyPortMappingResult
{
    public int AssignedExternalPort { get; set; }
    public int InternalPort { get; set; }
    public string Protocol { get; set; } = string.Empty;
    public int LeaseDuration { get; set; }
    public DateTime ExpiresAt { get; set; }
}

/// <summary>
/// Result of IPv6 pinhole creation
/// </summary>
public class UPnPPinholeResult
{
    public int UniqueId { get; set; }
    public IPAddress RemoteHost { get; set; } = IPAddress.Any;
    public int RemotePort { get; set; }
    public IPAddress InternalClient { get; set; } = IPAddress.IPv6Any;
    public int InternalPort { get; set; }
    public string Protocol { get; set; } = string.Empty;
    public int LeaseTime { get; set; }
    public DateTime ExpiresAt { get; set; }
}

/// <summary>
/// UPnP service status information
/// </summary>
public class UPnPServiceStatus
{
    public bool IsAvailable { get; set; }
    public int DeviceCount { get; set; }
    public int Igdv1DeviceCount { get; set; }
    public int Igdv2DeviceCount { get; set; }
    public int Ipv6DeviceCount { get; set; }
    public int ActiveMappingCount { get; set; }
    public int ActivePinholeCount { get; set; }
    public string? ExternalIPv4 { get; set; }
    public string? ExternalIPv6 { get; set; }
    public DateTime LastDiscovery { get; set; }
    public List<UPnPDeviceStatus> Devices { get; set; } = new();
}

/// <summary>
/// Individual UPnP device status
/// </summary>
public class UPnPDeviceStatus
{
    public string DeviceId { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public bool SupportsIgdv2 { get; set; }
    public bool SupportsIpv6 { get; set; }
    public string? ExternalIP { get; set; }
    public int MappingCount { get; set; }
}

/// <summary>
/// UPnP error codes from IGD specification
/// </summary>
public static class UPnPErrorCodes
{
    /// <summary>402 - Invalid arguments</summary>
    public const int InvalidArgs = 402;
    /// <summary>501 - Action failed</summary>
    public const int ActionFailed = 501;
    /// <summary>606 - Action not authorized</summary>
    public const int NotAuthorized = 606;
    /// <summary>714 - No such entry in array (expected at end of mapping list)</summary>
    public const int NoSuchEntryInArray = 714;
    /// <summary>715 - Wildcard not permitted in source IP</summary>
    public const int WildcardNotPermittedInSrc = 715;
    /// <summary>716 - Wildcard not permitted in external port</summary>
    public const int WildcardNotPermittedInExtPort = 716;
    /// <summary>718 - Conflict in mapping entry (port already mapped)</summary>
    public const int ConflictInMappingEntry = 718;
    /// <summary>724 - Same port values required</summary>
    public const int SamePortValuesRequired = 724;
    /// <summary>725 - Only permanent leases supported (use duration=0)</summary>
    public const int OnlyPermanentLeasesSupported = 725;
    /// <summary>726 - Remote host only supports wildcard</summary>
    public const int RemoteHostOnlySupportsWildcard = 726;
    /// <summary>727 - External port only supports wildcard</summary>
    public const int ExternalPortOnlySupportsWildcard = 727;
    /// <summary>728 - No port maps available (router exhausted)</summary>
    public const int NoPortMapsAvailable = 728;
    /// <summary>729 - Conflict with other mechanism</summary>
    public const int ConflictWithOtherMechanism = 729;
}