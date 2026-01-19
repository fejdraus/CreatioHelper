using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Network;

/// <summary>
/// Service for managing LAN address announcements.
/// Based on Syncthing's announceLANAddresses option.
/// </summary>
public interface IAnnounceLanAddressesService
{
    /// <summary>
    /// Check if LAN address announcement is enabled.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Enable or disable LAN address announcement.
    /// </summary>
    void SetEnabled(bool enabled);

    /// <summary>
    /// Get all LAN addresses to announce.
    /// </summary>
    IReadOnlyList<LanAddress> GetLanAddresses();

    /// <summary>
    /// Get LAN addresses for a specific interface.
    /// </summary>
    IReadOnlyList<LanAddress> GetAddressesForInterface(string interfaceName);

    /// <summary>
    /// Check if an address is a LAN address.
    /// </summary>
    bool IsLanAddress(IPAddress address);

    /// <summary>
    /// Check if an address is a link-local address.
    /// </summary>
    bool IsLinkLocalAddress(IPAddress address);

    /// <summary>
    /// Add a custom LAN address to announce.
    /// </summary>
    void AddCustomAddress(IPAddress address, int port);

    /// <summary>
    /// Remove a custom LAN address.
    /// </summary>
    bool RemoveCustomAddress(IPAddress address);

    /// <summary>
    /// Get network interfaces.
    /// </summary>
    IReadOnlyList<NetworkInterfaceInfo> GetNetworkInterfaces();

    /// <summary>
    /// Filter addresses for announcement.
    /// </summary>
    IReadOnlyList<LanAddress> FilterForAnnouncement(IEnumerable<LanAddress> addresses);

    /// <summary>
    /// Get announcement statistics.
    /// </summary>
    AnnouncementStats GetStats();

    /// <summary>
    /// Refresh detected LAN addresses.
    /// </summary>
    void RefreshAddresses();
}

/// <summary>
/// Represents a LAN address.
/// </summary>
public class LanAddress
{
    public IPAddress Address { get; init; } = IPAddress.None;
    public int Port { get; init; }
    public string InterfaceName { get; init; } = string.Empty;
    public AddressType Type { get; init; }
    public bool IsCustom { get; init; }
    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;

    public string FullAddress => $"{Address}:{Port}";

    public override string ToString() => FullAddress;
}

/// <summary>
/// Type of LAN address.
/// </summary>
public enum AddressType
{
    /// <summary>
    /// Private network address (10.x.x.x, 172.16-31.x.x, 192.168.x.x).
    /// </summary>
    Private,

    /// <summary>
    /// Link-local address (169.254.x.x, fe80::).
    /// </summary>
    LinkLocal,

    /// <summary>
    /// Loopback address (127.x.x.x, ::1).
    /// </summary>
    Loopback,

    /// <summary>
    /// Public/routable address.
    /// </summary>
    Public,

    /// <summary>
    /// Custom user-defined address.
    /// </summary>
    Custom
}

/// <summary>
/// Information about a network interface.
/// </summary>
public class NetworkInterfaceInfo
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public NetworkInterfaceType Type { get; init; }
    public OperationalStatus Status { get; init; }
    public long Speed { get; init; }
    public IReadOnlyList<IPAddress> Addresses { get; init; } = Array.Empty<IPAddress>();
    public bool SupportsMulticast { get; init; }
    public string? MacAddress { get; init; }
}

/// <summary>
/// Announcement statistics.
/// </summary>
public class AnnouncementStats
{
    public int TotalAddresses { get; set; }
    public int PrivateAddresses { get; set; }
    public int LinkLocalAddresses { get; set; }
    public int CustomAddresses { get; set; }
    public int IPv4Addresses { get; set; }
    public int IPv6Addresses { get; set; }
    public int ActiveInterfaces { get; set; }
    public DateTime LastRefresh { get; set; }
    public long RefreshCount { get; set; }
}

/// <summary>
/// Configuration for LAN address announcement.
/// </summary>
public class AnnounceLanAddressesConfiguration
{
    /// <summary>
    /// Enable LAN address announcement.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Default port for announcements.
    /// </summary>
    public int DefaultPort { get; set; } = 22000;

    /// <summary>
    /// Include link-local addresses.
    /// </summary>
    public bool IncludeLinkLocal { get; set; } = true;

    /// <summary>
    /// Include IPv6 addresses.
    /// </summary>
    public bool IncludeIPv6 { get; set; } = true;

    /// <summary>
    /// Exclude specific interfaces by name pattern.
    /// </summary>
    public List<string> ExcludedInterfaces { get; } = new()
    {
        "docker",
        "veth",
        "br-",
        "virbr"
    };

    /// <summary>
    /// Only include specific interfaces (empty = all).
    /// </summary>
    public List<string> IncludedInterfaces { get; } = new();

    /// <summary>
    /// Custom addresses to always announce.
    /// </summary>
    public List<string> CustomAddresses { get; } = new();
}

/// <summary>
/// Implementation of LAN address announcement service.
/// </summary>
public class AnnounceLanAddressesService : IAnnounceLanAddressesService
{
    private readonly ILogger<AnnounceLanAddressesService> _logger;
    private readonly AnnounceLanAddressesConfiguration _config;
    private readonly ConcurrentDictionary<string, LanAddress> _customAddresses = new();
    private readonly List<LanAddress> _detectedAddresses = new();
    private readonly object _lock = new();
    private bool _enabled;
    private DateTime _lastRefresh;
    private long _refreshCount;

    public AnnounceLanAddressesService(
        ILogger<AnnounceLanAddressesService> logger,
        AnnounceLanAddressesConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new AnnounceLanAddressesConfiguration();
        _enabled = _config.Enabled;

        // Load custom addresses from config
        foreach (var addr in _config.CustomAddresses)
        {
            if (TryParseAddress(addr, out var ip, out var port))
            {
                AddCustomAddress(ip, port);
            }
        }

        RefreshAddresses();
    }

    /// <inheritdoc />
    public bool IsEnabled => _enabled;

    /// <inheritdoc />
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _logger.LogInformation("LAN address announcement {State}", enabled ? "enabled" : "disabled");
    }

    /// <inheritdoc />
    public IReadOnlyList<LanAddress> GetLanAddresses()
    {
        if (!_enabled)
        {
            return Array.Empty<LanAddress>();
        }

        lock (_lock)
        {
            var addresses = new List<LanAddress>(_detectedAddresses);
            addresses.AddRange(_customAddresses.Values);
            return addresses;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<LanAddress> GetAddressesForInterface(string interfaceName)
    {
        ArgumentNullException.ThrowIfNull(interfaceName);

        lock (_lock)
        {
            return _detectedAddresses
                .Where(a => a.InterfaceName.Equals(interfaceName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
    }

    /// <inheritdoc />
    public bool IsLanAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

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
        }
        else if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            // Link-local fe80::/10
            if (address.IsIPv6LinkLocal) return true;

            // Unique local fc00::/7
            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;
        }

        return false;
    }

    /// <inheritdoc />
    public bool IsLinkLocalAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] == 169 && bytes[1] == 254;
        }

        return address.IsIPv6LinkLocal;
    }

    /// <inheritdoc />
    public void AddCustomAddress(IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (port < 1 || port > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535");
        }

        var key = $"{address}:{port}";
        var lanAddress = new LanAddress
        {
            Address = address,
            Port = port,
            InterfaceName = "custom",
            Type = AddressType.Custom,
            IsCustom = true
        };

        _customAddresses[key] = lanAddress;
        _logger.LogInformation("Added custom LAN address: {Address}", key);
    }

    /// <inheritdoc />
    public bool RemoveCustomAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var keysToRemove = _customAddresses.Keys
            .Where(k => k.StartsWith(address.ToString()))
            .ToList();

        var removed = false;
        foreach (var key in keysToRemove)
        {
            if (_customAddresses.TryRemove(key, out _))
            {
                removed = true;
                _logger.LogInformation("Removed custom LAN address: {Address}", key);
            }
        }

        return removed;
    }

    /// <inheritdoc />
    public IReadOnlyList<NetworkInterfaceInfo> GetNetworkInterfaces()
    {
        var result = new List<NetworkInterfaceInfo>();

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }

                if (ShouldExcludeInterface(ni.Name))
                {
                    continue;
                }

                var addresses = ni.GetIPProperties().UnicastAddresses
                    .Select(a => a.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork ||
                               (_config.IncludeIPv6 && a.AddressFamily == AddressFamily.InterNetworkV6))
                    .ToList();

                if (addresses.Count == 0)
                {
                    continue;
                }

                string? macAddress = null;
                try
                {
                    var mac = ni.GetPhysicalAddress();
                    if (mac != null && mac.GetAddressBytes().Length > 0)
                    {
                        macAddress = string.Join(":", mac.GetAddressBytes().Select(b => b.ToString("X2")));
                    }
                }
                catch
                {
                    // MAC address not available
                }

                result.Add(new NetworkInterfaceInfo
                {
                    Name = ni.Name,
                    Description = ni.Description,
                    Type = ni.NetworkInterfaceType,
                    Status = ni.OperationalStatus,
                    Speed = ni.Speed,
                    Addresses = addresses,
                    SupportsMulticast = ni.SupportsMulticast,
                    MacAddress = macAddress
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to enumerate network interfaces");
        }

        return result;
    }

    /// <inheritdoc />
    public IReadOnlyList<LanAddress> FilterForAnnouncement(IEnumerable<LanAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);

        return addresses
            .Where(a => a.Type != AddressType.Loopback)
            .Where(a => _config.IncludeLinkLocal || a.Type != AddressType.LinkLocal)
            .Where(a => _config.IncludeIPv6 || a.Address.AddressFamily == AddressFamily.InterNetwork)
            .ToList();
    }

    /// <inheritdoc />
    public AnnouncementStats GetStats()
    {
        var addresses = GetLanAddresses();

        return new AnnouncementStats
        {
            TotalAddresses = addresses.Count,
            PrivateAddresses = addresses.Count(a => a.Type == AddressType.Private),
            LinkLocalAddresses = addresses.Count(a => a.Type == AddressType.LinkLocal),
            CustomAddresses = addresses.Count(a => a.IsCustom),
            IPv4Addresses = addresses.Count(a => a.Address.AddressFamily == AddressFamily.InterNetwork),
            IPv6Addresses = addresses.Count(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6),
            ActiveInterfaces = GetNetworkInterfaces().Count,
            LastRefresh = _lastRefresh,
            RefreshCount = _refreshCount
        };
    }

    /// <inheritdoc />
    public void RefreshAddresses()
    {
        lock (_lock)
        {
            _detectedAddresses.Clear();

            foreach (var ni in GetNetworkInterfaces())
            {
                foreach (var address in ni.Addresses)
                {
                    if (IPAddress.IsLoopback(address))
                    {
                        continue;
                    }

                    var type = DetermineAddressType(address);

                    _detectedAddresses.Add(new LanAddress
                    {
                        Address = address,
                        Port = _config.DefaultPort,
                        InterfaceName = ni.Name,
                        Type = type,
                        IsCustom = false
                    });
                }
            }

            _lastRefresh = DateTime.UtcNow;
            _refreshCount++;

            _logger.LogDebug("Refreshed LAN addresses: {Count} addresses detected", _detectedAddresses.Count);
        }
    }

    private bool ShouldExcludeInterface(string name)
    {
        // Check included interfaces first
        if (_config.IncludedInterfaces.Count > 0)
        {
            return !_config.IncludedInterfaces.Any(i =>
                name.Contains(i, StringComparison.OrdinalIgnoreCase));
        }

        // Check excluded interfaces
        return _config.ExcludedInterfaces.Any(e =>
            name.Contains(e, StringComparison.OrdinalIgnoreCase));
    }

    private AddressType DetermineAddressType(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return AddressType.Loopback;
        }

        if (IsLinkLocalAddress(address))
        {
            return AddressType.LinkLocal;
        }

        if (IsLanAddress(address))
        {
            return AddressType.Private;
        }

        return AddressType.Public;
    }

    private bool TryParseAddress(string addressString, out IPAddress ip, out int port)
    {
        ip = IPAddress.None;
        port = _config.DefaultPort;

        if (string.IsNullOrWhiteSpace(addressString))
        {
            return false;
        }

        var lastColon = addressString.LastIndexOf(':');
        if (lastColon > 0 && int.TryParse(addressString[(lastColon + 1)..], out var parsedPort))
        {
            port = parsedPort;
            addressString = addressString[..lastColon];
        }

        return IPAddress.TryParse(addressString.Trim('[', ']'), out ip!);
    }
}
