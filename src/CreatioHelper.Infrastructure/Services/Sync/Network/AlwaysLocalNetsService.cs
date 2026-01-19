using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Network;

/// <summary>
/// Service for managing networks that are always considered local.
/// Based on Syncthing's alwaysLocalNets configuration.
/// </summary>
public interface IAlwaysLocalNetsService
{
    /// <summary>
    /// Check if an IP address is considered local.
    /// </summary>
    bool IsLocalAddress(IPAddress address);

    /// <summary>
    /// Check if a hostname/address string is considered local.
    /// </summary>
    bool IsLocalAddress(string address);

    /// <summary>
    /// Add a network to the always-local list.
    /// </summary>
    void AddLocalNetwork(string cidr);

    /// <summary>
    /// Remove a network from the always-local list.
    /// </summary>
    bool RemoveLocalNetwork(string cidr);

    /// <summary>
    /// Get all configured always-local networks.
    /// </summary>
    IReadOnlyList<string> GetLocalNetworks();

    /// <summary>
    /// Clear all always-local networks.
    /// </summary>
    void ClearLocalNetworks();

    /// <summary>
    /// Check if an address is in a private/RFC1918 range.
    /// </summary>
    bool IsPrivateAddress(IPAddress address);

    /// <summary>
    /// Get the network type for an address.
    /// </summary>
    NetworkType GetNetworkType(IPAddress address);
}

/// <summary>
/// Type of network.
/// </summary>
public enum NetworkType
{
    Unknown,
    Loopback,
    LinkLocal,
    Private,
    AlwaysLocal,
    Public
}

/// <summary>
/// Represents a network CIDR range.
/// </summary>
public class NetworkRange
{
    public IPAddress Network { get; }
    public int PrefixLength { get; }
    public string Cidr { get; }

    public NetworkRange(string cidr)
    {
        Cidr = cidr;
        var parts = cidr.Split('/');
        Network = IPAddress.Parse(parts[0]);
        PrefixLength = parts.Length > 1 ? int.Parse(parts[1]) : (Network.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
    }

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != Network.AddressFamily)
        {
            return false;
        }

        var networkBytes = Network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();

        var fullBytes = PrefixLength / 8;
        var remainingBits = PrefixLength % 8;

        for (int i = 0; i < fullBytes; i++)
        {
            if (networkBytes[i] != addressBytes[i])
            {
                return false;
            }
        }

        if (remainingBits > 0 && fullBytes < networkBytes.Length)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            if ((networkBytes[fullBytes] & mask) != (addressBytes[fullBytes] & mask))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Configuration for always local nets.
/// </summary>
public class AlwaysLocalNetsConfiguration
{
    /// <summary>
    /// List of CIDR ranges always considered local.
    /// </summary>
    public List<string> AlwaysLocalNets { get; } = new();

    /// <summary>
    /// Whether to treat RFC1918 private addresses as local.
    /// </summary>
    public bool TreatPrivateAsLocal { get; set; } = true;

    /// <summary>
    /// Whether to treat link-local addresses as local.
    /// </summary>
    public bool TreatLinkLocalAsLocal { get; set; } = true;
}

/// <summary>
/// Implementation of always local nets service.
/// </summary>
public class AlwaysLocalNetsService : IAlwaysLocalNetsService
{
    private readonly ILogger<AlwaysLocalNetsService> _logger;
    private readonly AlwaysLocalNetsConfiguration _config;
    private readonly List<NetworkRange> _localRanges = new();
    private readonly object _lock = new();

    // Standard private network ranges (RFC1918)
    private static readonly NetworkRange[] PrivateRanges =
    {
        new("10.0.0.0/8"),
        new("172.16.0.0/12"),
        new("192.168.0.0/16"),
        new("fc00::/7"),  // IPv6 Unique Local Addresses
    };

    // Link-local ranges
    private static readonly NetworkRange[] LinkLocalRanges =
    {
        new("169.254.0.0/16"),  // IPv4 link-local
        new("fe80::/10"),       // IPv6 link-local
    };

    public AlwaysLocalNetsService(
        ILogger<AlwaysLocalNetsService> logger,
        AlwaysLocalNetsConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new AlwaysLocalNetsConfiguration();

        // Initialize from config
        foreach (var cidr in _config.AlwaysLocalNets)
        {
            try
            {
                _localRanges.Add(new NetworkRange(cidr));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invalid CIDR in config: {Cidr}", cidr);
            }
        }
    }

    /// <inheritdoc />
    public bool IsLocalAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        // Loopback is always local
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        // Check link-local
        if (_config.TreatLinkLocalAsLocal && IsLinkLocal(address))
        {
            return true;
        }

        // Check private ranges
        if (_config.TreatPrivateAsLocal && IsPrivateAddress(address))
        {
            return true;
        }

        // Check always-local ranges
        lock (_lock)
        {
            foreach (var range in _localRanges)
            {
                if (range.Contains(address))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <inheritdoc />
    public bool IsLocalAddress(string address)
    {
        if (string.IsNullOrEmpty(address))
        {
            return false;
        }

        // Handle hostname:port format
        var hostPart = address;
        if (address.Contains(':') && !address.Contains('['))
        {
            // IPv4 with port
            var lastColon = address.LastIndexOf(':');
            hostPart = address.Substring(0, lastColon);
        }
        else if (address.StartsWith('[') && address.Contains(']'))
        {
            // IPv6 with brackets
            var endBracket = address.IndexOf(']');
            hostPart = address.Substring(1, endBracket - 1);
        }

        if (IPAddress.TryParse(hostPart, out var ipAddress))
        {
            return IsLocalAddress(ipAddress);
        }

        // Try to resolve hostname
        try
        {
            var addresses = Dns.GetHostAddresses(hostPart);
            return addresses.Any(IsLocalAddress);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public void AddLocalNetwork(string cidr)
    {
        ArgumentNullException.ThrowIfNull(cidr);

        try
        {
            var range = new NetworkRange(cidr);

            lock (_lock)
            {
                if (!_localRanges.Any(r => r.Cidr == cidr))
                {
                    _localRanges.Add(range);
                    _config.AlwaysLocalNets.Add(cidr);
                    _logger.LogInformation("Added always-local network: {Cidr}", cidr);
                }
            }
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid CIDR format: {cidr}", nameof(cidr), ex);
        }
    }

    /// <inheritdoc />
    public bool RemoveLocalNetwork(string cidr)
    {
        ArgumentNullException.ThrowIfNull(cidr);

        lock (_lock)
        {
            var index = _localRanges.FindIndex(r => r.Cidr == cidr);
            if (index >= 0)
            {
                _localRanges.RemoveAt(index);
                _config.AlwaysLocalNets.Remove(cidr);
                _logger.LogInformation("Removed always-local network: {Cidr}", cidr);
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetLocalNetworks()
    {
        lock (_lock)
        {
            return _localRanges.Select(r => r.Cidr).ToList();
        }
    }

    /// <inheritdoc />
    public void ClearLocalNetworks()
    {
        lock (_lock)
        {
            _localRanges.Clear();
            _config.AlwaysLocalNets.Clear();
            _logger.LogInformation("Cleared all always-local networks");
        }
    }

    /// <inheritdoc />
    public bool IsPrivateAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        foreach (var range in PrivateRanges)
        {
            if (range.Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public NetworkType GetNetworkType(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (IPAddress.IsLoopback(address))
        {
            return NetworkType.Loopback;
        }

        if (IsLinkLocal(address))
        {
            return NetworkType.LinkLocal;
        }

        lock (_lock)
        {
            foreach (var range in _localRanges)
            {
                if (range.Contains(address))
                {
                    return NetworkType.AlwaysLocal;
                }
            }
        }

        if (IsPrivateAddress(address))
        {
            return NetworkType.Private;
        }

        return NetworkType.Public;
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        foreach (var range in LinkLocalRanges)
        {
            if (range.Contains(address))
            {
                return true;
            }
        }

        return address.IsIPv6LinkLocal;
    }
}
