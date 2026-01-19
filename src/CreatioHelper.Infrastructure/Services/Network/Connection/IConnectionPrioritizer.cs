using System.Net;

namespace CreatioHelper.Infrastructure.Services.Network.Connection;

/// <summary>
/// Interface for connection prioritization.
/// Determines the order in which connections should be attempted.
/// Based on Syncthing's connection prioritization from lib/connections/service.go
/// </summary>
public interface IConnectionPrioritizer
{
    /// <summary>
    /// Calculate priority for an address (lower is better)
    /// </summary>
    int CalculatePriority(string address);

    /// <summary>
    /// Sort addresses by priority (lower priority first)
    /// </summary>
    IEnumerable<PrioritizedAddress> PrioritizeAddresses(IEnumerable<string> addresses);

    /// <summary>
    /// Group addresses into priority buckets for parallel dialing
    /// </summary>
    IEnumerable<IGrouping<int, PrioritizedAddress>> GetPriorityBuckets(IEnumerable<string> addresses);

    /// <summary>
    /// Check if an address is on a LAN
    /// </summary>
    bool IsLanAddress(string address);

    /// <summary>
    /// Check if a connection should be upgraded based on priority
    /// </summary>
    bool ShouldUpgrade(int currentPriority, int newPriority, int upgradeThreshold);

    /// <summary>
    /// Get the connection type from an address
    /// </summary>
    ConnectionType GetConnectionType(string address);

    /// <summary>
    /// Get priority configuration
    /// </summary>
    ConnectionPriorityConfiguration Configuration { get; }
}

/// <summary>
/// An address with its calculated priority
/// </summary>
public class PrioritizedAddress
{
    /// <summary>
    /// The address URI (tcp://host:port, quic://host:port, relay://...)
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Priority value (lower is better)
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Connection type
    /// </summary>
    public ConnectionType Type { get; set; }

    /// <summary>
    /// Is this a LAN address
    /// </summary>
    public bool IsLan { get; set; }

    /// <summary>
    /// Parsed IP address (if applicable)
    /// </summary>
    public IPAddress? IpAddress { get; set; }

    /// <summary>
    /// Port number
    /// </summary>
    public int Port { get; set; }
}

/// <summary>
/// Types of connections
/// </summary>
public enum ConnectionType
{
    /// <summary>
    /// Unknown connection type
    /// </summary>
    Unknown,

    /// <summary>
    /// TCP connection on LAN
    /// </summary>
    TcpLan,

    /// <summary>
    /// TCP connection on WAN
    /// </summary>
    TcpWan,

    /// <summary>
    /// QUIC connection on LAN
    /// </summary>
    QuicLan,

    /// <summary>
    /// QUIC connection on WAN
    /// </summary>
    QuicWan,

    /// <summary>
    /// Relay connection
    /// </summary>
    Relay
}

/// <summary>
/// Configuration for connection priorities
/// </summary>
public class ConnectionPriorityConfiguration
{
    /// <summary>
    /// Priority for QUIC connections on LAN (default: 0, highest)
    /// </summary>
    public int QuicLanPriority { get; set; } = 0;

    /// <summary>
    /// Priority for QUIC connections on WAN (default: 5)
    /// </summary>
    public int QuicWanPriority { get; set; } = 5;

    /// <summary>
    /// Priority for TCP connections on LAN (default: 10)
    /// </summary>
    public int TcpLanPriority { get; set; } = 10;

    /// <summary>
    /// Priority for TCP connections on WAN (default: 15)
    /// </summary>
    public int TcpWanPriority { get; set; } = 15;

    /// <summary>
    /// Priority for relay connections (default: 90, lowest)
    /// </summary>
    public int RelayPriority { get; set; } = 90;

    /// <summary>
    /// Priority for unknown connection types (default: 50)
    /// </summary>
    public int UnknownPriority { get; set; } = 50;

    /// <summary>
    /// Threshold for upgrading connections.
    /// A new connection must be this much better than the worst existing connection.
    /// (default: 5)
    /// </summary>
    public int UpgradeThreshold { get; set; } = 5;

    /// <summary>
    /// Custom LAN networks (CIDR notation)
    /// </summary>
    public List<string> AlwaysLocalNetworks { get; set; } = new();

    /// <summary>
    /// Maximum parallel dials total (default: 64)
    /// </summary>
    public int MaxParallelDials { get; set; } = 64;

    /// <summary>
    /// Maximum parallel dials per device (default: 8)
    /// </summary>
    public int MaxParallelDialsPerDevice { get; set; } = 8;
}
