using System.Net;
using CreatioHelper.Infrastructure.Services.Network.Stun;

namespace CreatioHelper.Infrastructure.Services.Network.Nat;

/// <summary>
/// Unified NAT traversal manager interface.
/// Coordinates UPnP, NAT-PMP, STUN, and other NAT traversal methods.
/// Based on Syncthing's NAT service from lib/nat/service.go
/// </summary>
public interface INatTraversalManager : IDisposable
{
    /// <summary>
    /// Start NAT traversal services
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stop NAT traversal services
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// Create a port mapping using the best available method
    /// </summary>
    Task<NatMappingResult?> CreateMappingAsync(
        string protocol,
        int internalPort,
        int requestedExternalPort = 0,
        string description = "CreatioHelper",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove a port mapping
    /// </summary>
    Task<bool> RemoveMappingAsync(NatMappingResult mapping, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all active port mappings
    /// </summary>
    Task<List<NatMappingResult>> GetActiveMappingsAsync();

    /// <summary>
    /// Get the detected external (public) IP address
    /// </summary>
    Task<IPAddress?> GetExternalAddressAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Detect NAT type using STUN
    /// </summary>
    Task<NatTypeResult?> DetectNatTypeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get NAT traversal status
    /// </summary>
    NatTraversalManagerStatus GetStatus();

    /// <summary>
    /// Is NAT traversal enabled
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Is NAT traversal running
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Event raised when external address changes
    /// </summary>
    event EventHandler<ExternalAddressChangedEventArgs>? ExternalAddressChanged;

    /// <summary>
    /// Event raised when a mapping is about to expire
    /// </summary>
    event EventHandler<MappingExpiringEventArgs>? MappingExpiring;
}

/// <summary>
/// Result of creating a NAT mapping
/// </summary>
public class NatMappingResult
{
    /// <summary>
    /// Unique mapping ID
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Protocol (TCP or UDP)
    /// </summary>
    public string Protocol { get; set; } = string.Empty;

    /// <summary>
    /// Internal (local) port
    /// </summary>
    public int InternalPort { get; set; }

    /// <summary>
    /// External (public) port
    /// </summary>
    public int ExternalPort { get; set; }

    /// <summary>
    /// Internal (local) IP address
    /// </summary>
    public IPAddress? InternalAddress { get; set; }

    /// <summary>
    /// External (public) IP address
    /// </summary>
    public IPAddress? ExternalAddress { get; set; }

    /// <summary>
    /// Method used to create the mapping
    /// </summary>
    public NatMethod Method { get; set; }

    /// <summary>
    /// Description/service name
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// When the mapping was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the mapping expires
    /// </summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>
    /// Is the mapping expired
    /// </summary>
    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

    /// <summary>
    /// Time until expiration
    /// </summary>
    public TimeSpan TimeToExpire => ExpiresAt - DateTime.UtcNow;

    /// <summary>
    /// Should the mapping be renewed soon
    /// </summary>
    public bool ShouldRenew => TimeToExpire.TotalMinutes < 5;

    /// <summary>
    /// Get the external endpoint
    /// </summary>
    public IPEndPoint? GetExternalEndpoint()
    {
        if (ExternalAddress == null)
            return null;
        return new IPEndPoint(ExternalAddress, ExternalPort);
    }

    /// <summary>
    /// Get the internal endpoint
    /// </summary>
    public IPEndPoint? GetInternalEndpoint()
    {
        if (InternalAddress == null)
            return null;
        return new IPEndPoint(InternalAddress, InternalPort);
    }
}

/// <summary>
/// NAT traversal method
/// </summary>
public enum NatMethod
{
    /// <summary>
    /// No NAT (direct connection)
    /// </summary>
    None,

    /// <summary>
    /// UPnP IGD (Internet Gateway Device)
    /// </summary>
    UPnP,

    /// <summary>
    /// NAT-PMP (NAT Port Mapping Protocol)
    /// </summary>
    NatPmp,

    /// <summary>
    /// PCP (Port Control Protocol, successor to NAT-PMP)
    /// </summary>
    Pcp,

    /// <summary>
    /// Manual port forwarding
    /// </summary>
    Manual,

    /// <summary>
    /// STUN hole punching
    /// </summary>
    Stun
}

/// <summary>
/// NAT traversal manager status
/// </summary>
public class NatTraversalManagerStatus
{
    /// <summary>
    /// Is NAT traversal enabled
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Is NAT traversal running
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// Detected NAT type
    /// </summary>
    public NatType? DetectedNatType { get; set; }

    /// <summary>
    /// NAT type description
    /// </summary>
    public string? NatTypeDescription { get; set; }

    /// <summary>
    /// Available NAT traversal methods
    /// </summary>
    public List<NatMethod> AvailableMethods { get; set; } = new();

    /// <summary>
    /// Preferred NAT traversal method
    /// </summary>
    public NatMethod PreferredMethod { get; set; }

    /// <summary>
    /// UPnP status
    /// </summary>
    public NatMethodStatus UPnP { get; set; } = new();

    /// <summary>
    /// NAT-PMP status
    /// </summary>
    public NatMethodStatus NatPmp { get; set; } = new();

    /// <summary>
    /// STUN status
    /// </summary>
    public StunStatus Stun { get; set; } = new();

    /// <summary>
    /// Currently detected external addresses
    /// </summary>
    public List<IPAddress> ExternalAddresses { get; set; } = new();

    /// <summary>
    /// Number of active port mappings
    /// </summary>
    public int ActiveMappingCount { get; set; }

    /// <summary>
    /// Last status update time
    /// </summary>
    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// Status of a NAT traversal method
/// </summary>
public class NatMethodStatus
{
    public bool Enabled { get; set; }
    public bool Available { get; set; }
    public int DeviceCount { get; set; }
    public int ActiveMappings { get; set; }
    public string? Error { get; set; }
    public DateTime? LastSuccess { get; set; }
}

/// <summary>
/// STUN service status
/// </summary>
public class StunStatus
{
    public bool Enabled { get; set; }
    public List<string> Servers { get; set; } = new();
    public IPAddress? ExternalAddress { get; set; }
    public int? ExternalPort { get; set; }
    public NatType? DetectedNatType { get; set; }
    public DateTime? LastCheck { get; set; }
    public TimeSpan? KeepaliveInterval { get; set; }
}

/// <summary>
/// Event args for external address change
/// </summary>
public class ExternalAddressChangedEventArgs : EventArgs
{
    public IPAddress? OldAddress { get; }
    public IPAddress? NewAddress { get; }
    public NatMethod DetectionMethod { get; }

    public ExternalAddressChangedEventArgs(IPAddress? oldAddress, IPAddress? newAddress, NatMethod method)
    {
        OldAddress = oldAddress;
        NewAddress = newAddress;
        DetectionMethod = method;
    }
}

/// <summary>
/// Event args for mapping expiration warning
/// </summary>
public class MappingExpiringEventArgs : EventArgs
{
    public NatMappingResult Mapping { get; }
    public TimeSpan TimeRemaining { get; }

    public MappingExpiringEventArgs(NatMappingResult mapping, TimeSpan timeRemaining)
    {
        Mapping = mapping;
        TimeRemaining = timeRemaining;
    }
}
