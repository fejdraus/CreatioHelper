using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Network.Connection;

/// <summary>
/// Connection prioritization implementation.
/// Based on Syncthing's connection prioritization from lib/connections/service.go
///
/// Priority order (lower is better):
/// 1. QUIC LAN (0)
/// 2. QUIC WAN (5)
/// 3. TCP LAN (10)
/// 4. TCP WAN (15)
/// 5. Relay (90)
/// </summary>
public class ConnectionPrioritizer : IConnectionPrioritizer
{
    private readonly ILogger<ConnectionPrioritizer> _logger;
    private readonly ConnectionPriorityConfiguration _config;
    private readonly List<IPNetwork> _alwaysLocalNetworks = new();

    public ConnectionPriorityConfiguration Configuration => _config;

    public ConnectionPrioritizer(ILogger<ConnectionPrioritizer> logger, ConnectionPriorityConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new ConnectionPriorityConfiguration();

        // Parse always-local networks
        foreach (var network in _config.AlwaysLocalNetworks)
        {
            try
            {
                _alwaysLocalNetworks.Add(IPNetwork.Parse(network));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse always-local network: {Network}", network);
            }
        }
    }

    public int CalculatePriority(string address)
    {
        var connectionType = GetConnectionType(address);

        return connectionType switch
        {
            ConnectionType.QuicLan => _config.QuicLanPriority,
            ConnectionType.QuicWan => _config.QuicWanPriority,
            ConnectionType.TcpLan => _config.TcpLanPriority,
            ConnectionType.TcpWan => _config.TcpWanPriority,
            ConnectionType.Relay => _config.RelayPriority,
            _ => _config.UnknownPriority
        };
    }

    public IEnumerable<PrioritizedAddress> PrioritizeAddresses(IEnumerable<string> addresses)
    {
        var prioritized = addresses
            .Select(addr =>
            {
                var type = GetConnectionType(addr);
                var isLan = IsLanAddress(addr);
                var (ip, port) = ParseAddress(addr);

                return new PrioritizedAddress
                {
                    Address = addr,
                    Priority = CalculatePriority(addr),
                    Type = type,
                    IsLan = isLan,
                    IpAddress = ip,
                    Port = port
                };
            })
            .OrderBy(p => p.Priority)
            .ThenBy(p => p.IsLan ? 0 : 1) // Prefer LAN within same priority
            .ToList();

        _logger.LogDebug("Prioritized {Count} addresses", prioritized.Count);

        return prioritized;
    }

    public IEnumerable<IGrouping<int, PrioritizedAddress>> GetPriorityBuckets(IEnumerable<string> addresses)
    {
        var prioritized = PrioritizeAddresses(addresses);

        return prioritized
            .GroupBy(p => p.Priority)
            .OrderBy(g => g.Key);
    }

    public bool IsLanAddress(string address)
    {
        try
        {
            var (ip, _) = ParseAddress(address);
            if (ip == null)
                return false;

            return IsLanIpAddress(ip);
        }
        catch
        {
            return false;
        }
    }

    public bool ShouldUpgrade(int currentPriority, int newPriority, int upgradeThreshold)
    {
        // Upgrade if new connection is better by at least the threshold
        return (currentPriority - newPriority) >= upgradeThreshold;
    }

    public ConnectionType GetConnectionType(string address)
    {
        if (string.IsNullOrEmpty(address))
            return ConnectionType.Unknown;

        // Check for relay first
        if (address.StartsWith("relay://", StringComparison.OrdinalIgnoreCase))
            return ConnectionType.Relay;

        var isLan = IsLanAddress(address);

        // Check protocol
        if (address.StartsWith("quic://", StringComparison.OrdinalIgnoreCase))
            return isLan ? ConnectionType.QuicLan : ConnectionType.QuicWan;

        if (address.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase))
            return isLan ? ConnectionType.TcpLan : ConnectionType.TcpWan;

        // If no protocol specified, assume TCP
        return isLan ? ConnectionType.TcpLan : ConnectionType.TcpWan;
    }

    /// <summary>
    /// Check if IP address is on a LAN
    /// Based on Syncthing's IsLAN function
    /// </summary>
    private bool IsLanIpAddress(IPAddress address)
    {
        // Check always-local networks first
        foreach (var network in _alwaysLocalNetworks)
        {
            if (network.Contains(address))
                return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();

            // 10.0.0.0/8
            if (bytes[0] == 10) return true;

            // 172.16.0.0/12
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;

            // 192.168.0.0/16
            if (bytes[0] == 192 && bytes[1] == 168) return true;

            // 169.254.0.0/16 (link-local)
            if (bytes[0] == 169 && bytes[1] == 254) return true;

            // 127.0.0.0/8 (loopback)
            if (bytes[0] == 127) return true;
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // IPv6 link-local (fe80::/10)
            if (address.IsIPv6LinkLocal) return true;

            // IPv6 ULA (fc00::/7)
            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;

            // IPv6 loopback (::1)
            if (IPAddress.IsLoopback(address)) return true;
        }

        return false;
    }

    private static (IPAddress? ip, int port) ParseAddress(string address)
    {
        try
        {
            if (string.IsNullOrEmpty(address))
                return (null, 0);

            // Handle relay:// differently (may have query params)
            if (address.StartsWith("relay://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(address);
                if (IPAddress.TryParse(uri.Host, out var relayIp))
                    return (relayIp, uri.Port > 0 ? uri.Port : 443);
                return (null, 0);
            }

            // Parse tcp:// or quic:// addresses
            if (address.StartsWith("tcp://", StringComparison.OrdinalIgnoreCase) ||
                address.StartsWith("quic://", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(address);
                var host = uri.Host.Trim('[', ']');

                if (IPAddress.TryParse(host, out var ip))
                    return (ip, uri.Port > 0 ? uri.Port : 22000);
                return (null, 0);
            }

            // Try parsing as host:port
            var parts = address.Split(':');
            if (parts.Length >= 2)
            {
                // Handle IPv6 addresses [::1]:port
                if (address.StartsWith("["))
                {
                    var closeBracket = address.IndexOf(']');
                    if (closeBracket > 0)
                    {
                        var host = address.Substring(1, closeBracket - 1);
                        var portStr = address.Substring(closeBracket + 2);

                        if (IPAddress.TryParse(host, out var ipv6) && int.TryParse(portStr, out var p))
                            return (ipv6, p);
                    }
                }
                else
                {
                    // IPv4 address
                    var lastColon = address.LastIndexOf(':');
                    if (lastColon > 0)
                    {
                        var host = address.Substring(0, lastColon);
                        var portStr = address.Substring(lastColon + 1);

                        if (IPAddress.TryParse(host, out var ipv4) && int.TryParse(portStr, out var p))
                            return (ipv4, p);
                    }
                }
            }

            // Try parsing just the IP
            if (IPAddress.TryParse(address, out var plainIp))
                return (plainIp, 0);

            return (null, 0);
        }
        catch
        {
            return (null, 0);
        }
    }
}

/// <summary>
/// Simple IP network representation for CIDR matching
/// </summary>
public class IPNetwork
{
    public IPAddress NetworkAddress { get; }
    public int PrefixLength { get; }
    private readonly byte[] _networkBytes;
    private readonly byte[] _maskBytes;

    public IPNetwork(IPAddress networkAddress, int prefixLength)
    {
        NetworkAddress = networkAddress;
        PrefixLength = prefixLength;
        _networkBytes = networkAddress.GetAddressBytes();
        _maskBytes = CreateMask(networkAddress.AddressFamily, prefixLength);
    }

    public static IPNetwork Parse(string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2)
            throw new FormatException($"Invalid CIDR notation: {cidr}");

        var address = IPAddress.Parse(parts[0]);
        var prefixLength = int.Parse(parts[1]);

        return new IPNetwork(address, prefixLength);
    }

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != NetworkAddress.AddressFamily)
            return false;

        var addressBytes = address.GetAddressBytes();

        for (int i = 0; i < _maskBytes.Length; i++)
        {
            if ((addressBytes[i] & _maskBytes[i]) != (_networkBytes[i] & _maskBytes[i]))
                return false;
        }

        return true;
    }

    private static byte[] CreateMask(AddressFamily family, int prefixLength)
    {
        var length = family == AddressFamily.InterNetwork ? 4 : 16;
        var mask = new byte[length];

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (int i = 0; i < fullBytes && i < length; i++)
        {
            mask[i] = 0xFF;
        }

        if (fullBytes < length && remainingBits > 0)
        {
            mask[fullBytes] = (byte)(0xFF << (8 - remainingBits));
        }

        return mask;
    }
}
