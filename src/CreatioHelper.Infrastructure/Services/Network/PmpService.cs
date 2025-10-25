#pragma warning disable CS1998 // Async method lacks await (for placeholder methods)  
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CreatioHelper.Infrastructure.Services.Network;

/// <summary>
/// Port Mapping Protocol (PMP) implementation for NAT-PMP and PCP
/// Based on RFC 6886 (NAT-PMP) and RFC 6887 (PCP)
/// </summary>
public interface IPmpService : IDisposable
{
    Task<bool> StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<NatMapping?> CreateMappingAsync(string protocol, int internalPort, int externalPort = 0, string description = "CreatioHelper");
    Task<bool> RemoveMappingAsync(NatMapping mapping);
    Task<List<NatMapping>> GetActiveMappingsAsync();
    bool IsEnabled { get; }
}

public class PmpGateway
{
    public IPAddress Gateway { get; set; } = IPAddress.Any;
    public IPAddress ExternalIP { get; set; } = IPAddress.Any;
    public bool SupportsNatPmp { get; set; }
    public bool SupportsPcp { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public uint Epoch { get; set; }

    public bool IsStale => DateTime.UtcNow.Subtract(LastSeen).TotalMinutes > 30;
    
    public override string ToString() => $"PMP Gateway {Gateway} (External: {ExternalIP})";
}

public class PmpService : IPmpService, IDisposable
{
    private readonly ILogger<PmpService> _logger;
    private readonly SyncConfiguration _config;
    private readonly ConcurrentDictionary<string, PmpGateway> _gateways = new();
    private readonly ConcurrentDictionary<string, NatMapping> _mappings = new();
    private readonly Timer _renewalTimer;
    private readonly Timer _discoveryTimer;
    private volatile bool _isEnabled;
    private volatile bool _isStarted;

    private const int NAT_PMP_PORT = 5351;
    private const int PCP_PORT = 5351;
    private const byte NAT_PMP_VERSION = 0;
    private const byte PCP_VERSION = 2;

    // NAT-PMP Operation codes
    private const byte NAT_PMP_OP_EXTERNAL_IP = 0;
    private const byte NAT_PMP_OP_MAP_UDP = 1;
    private const byte NAT_PMP_OP_MAP_TCP = 2;

    // PCP Operation codes  
    private const byte PCP_OP_MAP = 1;

    public bool IsEnabled => _isEnabled && _isStarted;

    public PmpService(ILogger<PmpService> logger, IOptions<SyncConfiguration> config)
    {
        _logger = logger;
        _config = config.Value;
        _isEnabled = _config.NatTraversal?.PmpEnabled ?? false;
        
        _renewalTimer = new Timer(ProcessRenewals, null, Timeout.Infinite, Timeout.Infinite);
        _discoveryTimer = new Timer(DiscoverGateways, null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_isEnabled || _isStarted)
            return _isStarted;

        _logger.LogInformation("Starting PMP service for NAT-PMP/PCP port mapping");

        try
        {
            // Discover gateways
            await DiscoverGatewaysAsync(cancellationToken);
            
            // Start timers
            var discoveryInterval = TimeSpan.FromMinutes(_config.NatTraversal?.DiscoveryIntervalMinutes ?? 15);
            var renewalInterval = TimeSpan.FromMinutes(_config.NatTraversal?.RenewalIntervalMinutes ?? 30);
            
            _discoveryTimer.Change(discoveryInterval, discoveryInterval);
            _renewalTimer.Change(renewalInterval, renewalInterval);
            
            _isStarted = true;
            _logger.LogInformation("PMP service started successfully with {GatewayCount} gateways discovered", _gateways.Count);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start PMP service");
            return false;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isStarted)
            return;

        _logger.LogInformation("Stopping PMP service");

        _renewalTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _discoveryTimer.Change(Timeout.Infinite, Timeout.Infinite);

        // Remove all mappings
        var tasks = _mappings.Values.Select(RemoveMappingAsync).ToList();
        await Task.WhenAll(tasks);

        _mappings.Clear();
        _gateways.Clear();
        _isStarted = false;

        _logger.LogInformation("PMP service stopped");
    }

    public async Task<NatMapping?> CreateMappingAsync(string protocol, int internalPort, int externalPort = 0, string description = "CreatioHelper")
    {
        if (!_isStarted)
        {
            _logger.LogWarning("PMP service is not started, cannot create mapping");
            return null;
        }

        var internalIP = await GetLocalIPAsync();
        if (internalIP == null)
        {
            _logger.LogError("Could not determine local IP address");
            return null;
        }

        foreach (var gateway in _gateways.Values.Where(g => !g.IsStale))
        {
            try
            {
                var mapping = await CreateMappingOnGatewayAsync(gateway, protocol, internalIP, internalPort, externalPort, description);
                if (mapping != null)
                {
                    _mappings[mapping.Id] = mapping;
                    _logger.LogInformation("Created PMP mapping: {Mapping}", mapping);
                    return mapping;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create PMP mapping on gateway {Gateway}", gateway.Gateway);
            }
        }

        _logger.LogWarning("Failed to create PMP mapping on any available gateway");
        return null;
    }

    public async Task<bool> RemoveMappingAsync(NatMapping mapping)
    {
        if (!_isStarted || !_mappings.ContainsKey(mapping.Id))
            return false;

        var gateway = _gateways.Values.FirstOrDefault(g => g.Gateway.ToString() == mapping.DeviceId);
        if (gateway == null)
        {
            _logger.LogWarning("Gateway {DeviceId} not found for mapping removal", mapping.DeviceId);
            _mappings.TryRemove(mapping.Id, out _);
            return false;
        }

        try
        {
            var success = await RemoveMappingOnGatewayAsync(gateway, mapping);
            if (success)
            {
                _mappings.TryRemove(mapping.Id, out _);
                _logger.LogInformation("Removed PMP mapping: {Mapping}", mapping);
            }
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove PMP mapping: {Mapping}", mapping);
            return false;
        }
    }

    public Task<List<NatMapping>> GetActiveMappingsAsync()
    {
        var activeMappings = _mappings.Values
            .Where(m => !m.IsExpired)
            .ToList();
        
        return Task.FromResult(activeMappings);
    }

    private async Task DiscoverGatewaysAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Starting PMP gateway discovery");

            // Get default gateways
            var gateways = GetDefaultGateways();
            
            foreach (var gatewayIP in gateways)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var gateway = await TestGatewayAsync(gatewayIP, cancellationToken);
                    if (gateway != null)
                    {
                        gateway.LastSeen = DateTime.UtcNow;
                        _gateways.AddOrUpdate(gatewayIP.ToString(), gateway, (key, old) => gateway);
                        _logger.LogDebug("Discovered PMP gateway: {Gateway}", gateway);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error testing gateway {Gateway}", gatewayIP);
                }
            }

            // Remove stale gateways
            var staleGateways = _gateways.Where(kvp => kvp.Value.IsStale).ToList();
            foreach (var (id, gateway) in staleGateways)
            {
                _gateways.TryRemove(id, out _);
                _logger.LogDebug("Removed stale PMP gateway: {Gateway}", id);
            }

            _logger.LogDebug("PMP gateway discovery completed, found {GatewayCount} gateways", _gateways.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during PMP gateway discovery");
        }
    }

    private List<IPAddress> GetDefaultGateways()
    {
        var gateways = new List<IPAddress>();

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            foreach (var networkInterface in interfaces)
            {
                var ipProperties = networkInterface.GetIPProperties();
                foreach (var gateway in ipProperties.GatewayAddresses)
                {
                    if (gateway.Address.AddressFamily == AddressFamily.InterNetwork && 
                        !IPAddress.IsLoopback(gateway.Address))
                    {
                        gateways.Add(gateway.Address);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting default gateways");
        }

        return gateways.Distinct().ToList();
    }

    private async Task<PmpGateway?> TestGatewayAsync(IPAddress gatewayIP, CancellationToken cancellationToken)
    {
        try
        {
            using var udpClient = new UdpClient();
            udpClient.Client.ReceiveTimeout = 5000;
            
            var gateway = new PmpGateway { Gateway = gatewayIP };

            // Test NAT-PMP first
            var natPmpResult = await TestNatPmpAsync(udpClient, gatewayIP, cancellationToken);
            if (natPmpResult != null)
            {
                gateway.SupportsNatPmp = true;
                gateway.ExternalIP = natPmpResult.Value.externalIP;
                gateway.Epoch = natPmpResult.Value.epoch;
            }

            // Test PCP if NAT-PMP failed
            if (!gateway.SupportsNatPmp)
            {
                gateway.SupportsPcp = await TestPcpAsync(udpClient, gatewayIP, cancellationToken);
            }

            return (gateway.SupportsNatPmp || gateway.SupportsPcp) ? gateway : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error testing gateway {Gateway}", gatewayIP);
            return null;
        }
    }

    private async Task<(IPAddress externalIP, uint epoch)?> TestNatPmpAsync(UdpClient udpClient, IPAddress gatewayIP, CancellationToken cancellationToken)
    {
        try
        {
            // NAT-PMP External IP request: Version (1) + Opcode (1) = 2 bytes
            var request = new byte[] { NAT_PMP_VERSION, NAT_PMP_OP_EXTERNAL_IP };
            
            var endpoint = new IPEndPoint(gatewayIP, NAT_PMP_PORT);
            await udpClient.SendAsync(request, request.Length, endpoint);

            var response = await udpClient.ReceiveAsync();
            if (response.Buffer.Length >= 12) // Minimum response size
            {
                var version = response.Buffer[0];
                var opcode = response.Buffer[1];
                var resultCode = (ushort)((response.Buffer[2] << 8) | response.Buffer[3]);
                var epoch = (uint)((response.Buffer[4] << 24) | (response.Buffer[5] << 16) | (response.Buffer[6] << 8) | response.Buffer[7]);
                
                if (version == NAT_PMP_VERSION && (opcode & 0x7F) == NAT_PMP_OP_EXTERNAL_IP && resultCode == 0)
                {
                    var externalIP = new IPAddress(response.Buffer[8..12]);
                    return (externalIP, epoch);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "NAT-PMP test failed for {Gateway}", gatewayIP);
        }

        return null;
    }

    private async Task<bool> TestPcpAsync(UdpClient udpClient, IPAddress gatewayIP, CancellationToken cancellationToken)
    {
        try
        {
            // Simple PCP ANNOUNCE request to test if PCP is supported
            var request = new byte[24]; // Minimum PCP request size
            request[0] = PCP_VERSION;
            request[1] = 0; // ANNOUNCE opcode
            // Rest filled with zeros (lifetime = 0 means query)
            
            var endpoint = new IPEndPoint(gatewayIP, PCP_PORT);
            await udpClient.SendAsync(request, request.Length, endpoint);

            var response = await udpClient.ReceiveAsync();
            if (response.Buffer.Length >= 24) // Minimum PCP response size
            {
                var version = response.Buffer[0];
                var opcode = response.Buffer[1];
                
                return version == PCP_VERSION && (opcode & 0x80) != 0; // Response bit set
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PCP test failed for {Gateway}", gatewayIP);
        }

        return false;
    }

    private async Task<NatMapping?> CreateMappingOnGatewayAsync(PmpGateway gateway, string protocol, IPAddress internalIP, int internalPort, int externalPort, string description)
    {
        if (gateway.SupportsNatPmp)
        {
            return await CreateNatPmpMappingAsync(gateway, protocol, internalIP, internalPort, externalPort, description);
        }
        else if (gateway.SupportsPcp)
        {
            return await CreatePcpMappingAsync(gateway, protocol, internalIP, internalPort, externalPort, description);
        }

        return null;
    }

    private async Task<NatMapping?> CreateNatPmpMappingAsync(PmpGateway gateway, string protocol, IPAddress internalIP, int internalPort, int externalPort, string description)
    {
        try
        {
            using var udpClient = new UdpClient();
            udpClient.Client.ReceiveTimeout = 5000;

            var opcode = protocol.ToLower() == "tcp" ? NAT_PMP_OP_MAP_TCP : NAT_PMP_OP_MAP_UDP;
            var lifetime = (uint)(_config.NatTraversal?.LeaseTimeMinutes ?? 60) * 60; // Convert to seconds

            var random = new Random();
            var attempts = 0;

            while (attempts < 10)
            {
                var targetExternalPort = externalPort > 0 ? externalPort : random.Next(1024, 65535);

                // NAT-PMP mapping request: Version(1) + Opcode(1) + Reserved(2) + InternalPort(2) + ExternalPort(2) + Lifetime(4) = 12 bytes
                var request = new byte[12];
                request[0] = NAT_PMP_VERSION;
                request[1] = opcode;
                // Reserved: request[2], request[3] = 0
                request[4] = (byte)(internalPort >> 8);
                request[5] = (byte)(internalPort & 0xFF);
                request[6] = (byte)(targetExternalPort >> 8);
                request[7] = (byte)(targetExternalPort & 0xFF);
                request[8] = (byte)(lifetime >> 24);
                request[9] = (byte)(lifetime >> 16);
                request[10] = (byte)(lifetime >> 8);
                request[11] = (byte)(lifetime & 0xFF);

                var endpoint = new IPEndPoint(gateway.Gateway, NAT_PMP_PORT);
                await udpClient.SendAsync(request, request.Length, endpoint);

                var response = await udpClient.ReceiveAsync();
                if (response.Buffer.Length >= 16) // NAT-PMP mapping response size
                {
                    var version = response.Buffer[0];
                    var responseOpcode = response.Buffer[1];
                    var resultCode = (ushort)((response.Buffer[2] << 8) | response.Buffer[3]);

                    if (version == NAT_PMP_VERSION && (responseOpcode & 0x7F) == opcode && resultCode == 0)
                    {
                        var mappedInternalPort = (ushort)((response.Buffer[8] << 8) | response.Buffer[9]);
                        var mappedExternalPort = (ushort)((response.Buffer[10] << 8) | response.Buffer[11]);
                        var mappedLifetime = (uint)((response.Buffer[12] << 24) | (response.Buffer[13] << 16) | (response.Buffer[14] << 8) | response.Buffer[15]);

                        return new NatMapping
                        {
                            Id = $"{gateway.Gateway}-natpmp-{protocol}-{mappedExternalPort}",
                            Protocol = protocol,
                            InternalIP = internalIP,
                            InternalPort = internalPort,
                            ExternalIP = gateway.ExternalIP,
                            ExternalPort = mappedExternalPort,
                            ExpiresAt = DateTime.UtcNow.AddSeconds(mappedLifetime),
                            DeviceId = gateway.Gateway.ToString(),
                            Description = description
                        };
                    }
                }

                attempts++;
                if (externalPort > 0) break; // Don't retry if specific port was requested
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create NAT-PMP mapping on gateway {Gateway}", gateway.Gateway);
        }

        return null;
    }

    private async Task<NatMapping?> CreatePcpMappingAsync(PmpGateway gateway, string protocol, IPAddress internalIP, int internalPort, int externalPort, string description)
    {
        // PCP MAP request implementation
        // This is a simplified implementation - full PCP support would be more complex
        try
        {
            using var udpClient = new UdpClient();
            udpClient.Client.ReceiveTimeout = 5000;

            var protocolNum = protocol.ToLower() == "tcp" ? (byte)6 : (byte)17; // TCP=6, UDP=17
            var lifetime = (uint)(_config.NatTraversal?.LeaseTimeMinutes ?? 60) * 60;

            var random = new Random();
            var attempts = 0;

            while (attempts < 10)
            {
                var targetExternalPort = externalPort > 0 ? externalPort : random.Next(1024, 65535);

                // Basic PCP MAP request (simplified)
                var request = new byte[60]; // PCP MAP request size
                request[0] = PCP_VERSION;
                request[1] = PCP_OP_MAP;
                // Lifetime
                request[4] = (byte)(lifetime >> 24);
                request[5] = (byte)(lifetime >> 16);
                request[6] = (byte)(lifetime >> 8);
                request[7] = (byte)(lifetime & 0xFF);
                // Client IP (last 4 bytes for IPv4)
                var clientIPBytes = internalIP.GetAddressBytes();
                Array.Copy(clientIPBytes, 0, request, 20, 4);
                // Protocol
                request[32] = protocolNum;
                // Internal port
                request[34] = (byte)(internalPort >> 8);
                request[35] = (byte)(internalPort & 0xFF);
                // External port
                request[36] = (byte)(targetExternalPort >> 8);
                request[37] = (byte)(targetExternalPort & 0xFF);

                var endpoint = new IPEndPoint(gateway.Gateway, PCP_PORT);
                await udpClient.SendAsync(request, request.Length, endpoint);

                var response = await udpClient.ReceiveAsync();
                if (response.Buffer.Length >= 60) // PCP MAP response size
                {
                    var version = response.Buffer[0];
                    var responseOpcode = response.Buffer[1];
                    var resultCode = response.Buffer[3];

                    if (version == PCP_VERSION && (responseOpcode & 0x80) != 0 && resultCode == 0)
                    {
                        var mappedLifetime = (uint)((response.Buffer[4] << 24) | (response.Buffer[5] << 16) | (response.Buffer[6] << 8) | response.Buffer[7]);
                        var mappedExternalPort = (ushort)((response.Buffer[42] << 8) | response.Buffer[43]);

                        return new NatMapping
                        {
                            Id = $"{gateway.Gateway}-pcp-{protocol}-{mappedExternalPort}",
                            Protocol = protocol,
                            InternalIP = internalIP,
                            InternalPort = internalPort,
                            ExternalIP = gateway.ExternalIP,
                            ExternalPort = mappedExternalPort,
                            ExpiresAt = DateTime.UtcNow.AddSeconds(mappedLifetime),
                            DeviceId = gateway.Gateway.ToString(),
                            Description = description
                        };
                    }
                }

                attempts++;
                if (externalPort > 0) break; // Don't retry if specific port was requested
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create PCP mapping on gateway {Gateway}", gateway.Gateway);
        }

        return null;
    }

    private async Task<bool> RemoveMappingOnGatewayAsync(PmpGateway gateway, NatMapping mapping)
    {
        try
        {
            if (gateway.SupportsNatPmp)
            {
                return await RemoveNatPmpMappingAsync(gateway, mapping);
            }
            else if (gateway.SupportsPcp)
            {
                return await RemovePcpMappingAsync(gateway, mapping);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove mapping on gateway {Gateway}", gateway.Gateway);
        }

        return false;
    }

    private async Task<bool> RemoveNatPmpMappingAsync(PmpGateway gateway, NatMapping mapping)
    {
        try
        {
            using var udpClient = new UdpClient();
            udpClient.Client.ReceiveTimeout = 5000;

            var opcode = mapping.Protocol.ToLower() == "tcp" ? NAT_PMP_OP_MAP_TCP : NAT_PMP_OP_MAP_UDP;

            // NAT-PMP removal: same as creation but with lifetime = 0
            var request = new byte[12];
            request[0] = NAT_PMP_VERSION;
            request[1] = opcode;
            request[4] = (byte)(mapping.InternalPort >> 8);
            request[5] = (byte)(mapping.InternalPort & 0xFF);
            request[6] = (byte)(mapping.ExternalPort >> 8);
            request[7] = (byte)(mapping.ExternalPort & 0xFF);
            // Lifetime = 0 to remove mapping
            // request[8-11] already 0

            var endpoint = new IPEndPoint(gateway.Gateway, NAT_PMP_PORT);
            await udpClient.SendAsync(request, request.Length, endpoint);

            var response = await udpClient.ReceiveAsync();
            if (response.Buffer.Length >= 16)
            {
                var version = response.Buffer[0];
                var responseOpcode = response.Buffer[1];
                var resultCode = (ushort)((response.Buffer[2] << 8) | response.Buffer[3]);

                return version == NAT_PMP_VERSION && (responseOpcode & 0x7F) == opcode && resultCode == 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error removing NAT-PMP mapping on gateway {Gateway}", gateway.Gateway);
        }

        return false;
    }

    private async Task<bool> RemovePcpMappingAsync(PmpGateway gateway, NatMapping mapping)
    {
        try
        {
            using var udpClient = new UdpClient();
            udpClient.Client.ReceiveTimeout = 5000;

            var protocolNum = mapping.Protocol.ToLower() == "tcp" ? (byte)6 : (byte)17;

            // PCP MAP request with lifetime = 0 to remove mapping
            var request = new byte[60];
            request[0] = PCP_VERSION;
            request[1] = PCP_OP_MAP;
            // Lifetime = 0 to remove mapping
            // request[4-7] already 0
            // Client IP
            var clientIPBytes = mapping.InternalIP.GetAddressBytes();
            Array.Copy(clientIPBytes, 0, request, 20, 4);
            // Protocol
            request[32] = protocolNum;
            // Internal port
            request[34] = (byte)(mapping.InternalPort >> 8);
            request[35] = (byte)(mapping.InternalPort & 0xFF);
            // External port
            request[36] = (byte)(mapping.ExternalPort >> 8);
            request[37] = (byte)(mapping.ExternalPort & 0xFF);

            var endpoint = new IPEndPoint(gateway.Gateway, PCP_PORT);
            await udpClient.SendAsync(request, request.Length, endpoint);

            var response = await udpClient.ReceiveAsync();
            if (response.Buffer.Length >= 60)
            {
                var version = response.Buffer[0];
                var responseOpcode = response.Buffer[1];
                var resultCode = response.Buffer[3];

                return version == PCP_VERSION && (responseOpcode & 0x80) != 0 && resultCode == 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error removing PCP mapping on gateway {Gateway}", gateway.Gateway);
        }

        return false;
    }

    private async Task<IPAddress?> GetLocalIPAsync()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Where(ua => ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                .Select(ua => ua.Address)
                .FirstOrDefault();

            return interfaces;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting local IP address");
            return null;
        }
    }

    private void DiscoverGateways(object? state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await DiscoverGatewaysAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during scheduled gateway discovery");
            }
        });
    }

    private void ProcessRenewals(object? state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var expiringSoon = _mappings.Values
                    .Where(m => m.ExpiresAt.Subtract(DateTime.UtcNow).TotalMinutes < 10)
                    .ToList();

                foreach (var mapping in expiringSoon)
                {
                    var gateway = _gateways.Values.FirstOrDefault(g => g.Gateway.ToString() == mapping.DeviceId);
                    if (gateway != null)
                    {
                        var newMapping = await CreateMappingOnGatewayAsync(
                            gateway, 
                            mapping.Protocol, 
                            mapping.InternalIP, 
                            mapping.InternalPort, 
                            mapping.ExternalPort, 
                            mapping.Description);

                        if (newMapping != null)
                        {
                            _mappings[mapping.Id] = newMapping;
                            _logger.LogDebug("Renewed PMP mapping: {Mapping}", newMapping);
                        }
                        else
                        {
                            _logger.LogWarning("Failed to renew PMP mapping: {Mapping}", mapping);
                            _mappings.TryRemove(mapping.Id, out _);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during PMP mapping renewal");
            }
        });
    }

    public void Dispose()
    {
        _renewalTimer?.Dispose();
        _discoveryTimer?.Dispose();
        
        if (_isStarted)
        {
            _ = StopAsync();
        }
    }
}