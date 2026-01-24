using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services.Network.Discovery;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Local discovery service for finding Syncthing-like devices on the local network
/// Uses UDP broadcast/multicast for device announcement and discovery
/// Compatible with Syncthing's local discovery protocol (lib/discover/local.go)
///
/// Protocol structure:
/// - Magic bytes: 4 bytes, big endian (0x2EA7D90B for current, 0x7D79BC40 for v0.13)
/// - Protobuf payload (discoproto.Announce):
///   - id: 32 raw bytes (device ID as SHA-256 hash)
///   - addresses: repeated strings (device addresses)
///   - instance_id: int64 (random ID for detecting restarts)
/// </summary>
public class LocalDiscoveryService : ILocalDiscovery, IDeviceDiscovery
{
    private readonly ILogger<LocalDiscoveryService> _logger;
    private readonly SyncConfiguration _syncConfig;
    private readonly string _currentDeviceId;
    private readonly int _currentPort;
    private readonly int _discoveryPort;
    private Timer? _announceTimer;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Dictionary<string, DiscoveredDevice> _discoveredDevices = new();
    
    private UdpClient? _udpClient;
    private UdpClient? _broadcastClient;
    private CancellationTokenSource _cancellationTokenSource = new();
    private bool _isRunning;
    private readonly long _instanceId; // Random instance ID generated on startup

    // Syncthing local discovery magic numbers (4 bytes, big endian)
    private const uint Magic = 0x2EA7D90B; // same as BEP protocol
    private const uint v13Magic = 0x7D79BC40; // previous version for compatibility
    private static readonly byte[] MagicBytes = BitConverter.GetBytes(Magic).Reverse().ToArray(); // Big endian
    private const int DefaultDiscoveryPort = 21027;
    private const int BroadcastInterval = 30; // seconds
    private const int CacheLifeTime = 90; // 3 * BroadcastInterval, seconds

    public event EventHandler<DeviceDiscoveredEventArgs>? DeviceDiscovered;

    /// <summary>
    /// Gets whether the discovery service is currently running.
    /// Required by ILocalDiscovery interface.
    /// </summary>
    public bool IsRunning => _isRunning;

    public LocalDiscoveryService(
        ILogger<LocalDiscoveryService> logger,
        SyncConfiguration syncConfig)
    {
        _logger = logger;
        _syncConfig = syncConfig;

        // Generate random instance ID (Syncthing compatibility)
        _instanceId = Random.Shared.NextInt64();

        // Get device ID from sync configuration
        _currentDeviceId = _syncConfig.DeviceId
            ?? throw new InvalidOperationException("SyncConfiguration.DeviceId is required");

        // Get port from sync configuration
        _currentPort = _syncConfig.Port > 0 ? _syncConfig.Port : 22000;

        // Get discovery port from sync configuration
        _discoveryPort = _syncConfig.DiscoveryPort > 0 ? _syncConfig.DiscoveryPort : DefaultDiscoveryPort;

        _logger.LogInformation("Local discovery service initialized for device {DeviceId} on port {Port}, discovery port {DiscoveryPort}",
            _currentDeviceId, _currentPort, _discoveryPort);
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning) return Task.CompletedTask;
        
        _logger.LogInformation("Starting local discovery service on port {DiscoveryPort}", _discoveryPort);
        
        try
        {
            // Setup UDP listener for incoming announcements
            _udpClient = new UdpClient(_discoveryPort);
            _udpClient.EnableBroadcast = true;
            
            // Setup UDP client for broadcasts
            _broadcastClient = new UdpClient();
            _broadcastClient.EnableBroadcast = true;
            
            _isRunning = true;
            
            // Start listening for announcements
            _ = Task.Run(async () => await ListenForAnnouncementsAsync(_cancellationTokenSource.Token));
            
            // Start periodic announcements every BroadcastInterval seconds (Syncthing compatibility)
            _announceTimer?.Dispose();
            _announceTimer = new Timer(AnnounceDevice, null, TimeSpan.Zero, TimeSpan.FromSeconds(BroadcastInterval));
            
            _logger.LogInformation("Local discovery service started successfully");
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start local discovery service");
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;
        
        _logger.LogInformation("Stopping local discovery service");
        
        _isRunning = false;
        _cancellationTokenSource.Cancel();
        
        _announceTimer?.Dispose();
        _udpClient?.Close();
        _udpClient?.Dispose();
        _broadcastClient?.Close();
        _broadcastClient?.Dispose();
        
        await Task.Delay(100); // Give time for cleanup
        
        _logger.LogInformation("Local discovery service stopped");
    }

    /// <summary>
    /// Announce this device's presence on the local network.
    /// Implementation for ILocalDiscovery interface.
    /// </summary>
    public async Task AnnounceAsync(CancellationToken cancellationToken = default)
    {
        if (!_isRunning) return;

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var addresses = GetLocalAddresses();
            await BroadcastAnnouncementAsync(addresses);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Announce device with specific addresses.
    /// Implementation for IDeviceDiscovery interface.
    /// </summary>
    public async Task AnnounceAsync(SyncDevice localDevice, List<string> addresses)
    {
        if (!_isRunning) return;

        await _semaphore.WaitAsync();
        try
        {
            await BroadcastAnnouncementAsync(addresses);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Discover devices on the local network.
    /// Implementation for ILocalDiscovery interface.
    /// </summary>
    /// <param name="deviceId">Device ID to search for, or null to return all discovered devices.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of discovered devices.</returns>
    async Task<IReadOnlyList<DiscoveredDevice>> ILocalDiscovery.DiscoverAsync(string? deviceId, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var devices = new List<DiscoveredDevice>();
            var now = DateTime.UtcNow;

            if (deviceId == null)
            {
                // Return all fresh devices
                var staleDeviceIds = new List<string>();
                foreach (var kvp in _discoveredDevices)
                {
                    if (now - kvp.Value.LastSeen < TimeSpan.FromSeconds(CacheLifeTime))
                    {
                        devices.Add(kvp.Value);
                    }
                    else
                    {
                        staleDeviceIds.Add(kvp.Key);
                    }
                }
                // Clean up stale entries
                foreach (var staleId in staleDeviceIds)
                {
                    _discoveredDevices.Remove(staleId);
                }
            }
            else if (_discoveredDevices.TryGetValue(deviceId, out var device))
            {
                // Check if device is still fresh (last seen within CacheLifeTime)
                if (now - device.LastSeen < TimeSpan.FromSeconds(CacheLifeTime))
                {
                    devices.Add(device);
                }
                else
                {
                    _discoveredDevices.Remove(deviceId);
                }
            }

            return devices;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Discover specific device on the local network.
    /// Implementation for IDeviceDiscovery interface.
    /// </summary>
    public async Task<List<DiscoveredDevice>> DiscoverAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            var devices = new List<DiscoveredDevice>();

            if (_discoveredDevices.TryGetValue(deviceId, out var device))
            {
                // Check if device is still fresh (last seen within CacheLifeTime)
                if (DateTime.UtcNow - device.LastSeen < TimeSpan.FromSeconds(CacheLifeTime))
                {
                    devices.Add(device);
                }
                else
                {
                    _discoveredDevices.Remove(deviceId);
                }
            }

            return devices;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task SetGlobalDiscoveryServersAsync(List<string> servers)
    {
        // Local discovery doesn't use global servers, but we implement for interface compliance
        _logger.LogDebug("Global discovery servers set (not used by local discovery): {Servers}", string.Join(", ", servers));
        await Task.CompletedTask;
    }

    public async Task SetLocalDiscoveryPortAsync(int port)
    {
        if (port != _discoveryPort)
        {
            _logger.LogWarning("Cannot change discovery port while service is running. Current: {CurrentPort}, Requested: {NewPort}", 
                _discoveryPort, port);
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Listen for incoming UDP announcements from other devices
    /// </summary>
    private async Task ListenForAnnouncementsAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting to listen for local discovery announcements");
        
        try
        {
            while (!cancellationToken.IsCancellationRequested && _isRunning)
            {
                try
                {
                    var result = await _udpClient!.ReceiveAsync();
                    await ProcessAnnouncementAsync(result);
                }
                catch (ObjectDisposedException)
                {
                    // UDP client disposed, normal shutdown
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error receiving UDP announcement");
                    await Task.Delay(1000, cancellationToken); // Brief delay before retrying
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation
        }
        
        _logger.LogDebug("Stopped listening for local discovery announcements");
    }

    /// <summary>
    /// Process received announcement
    /// </summary>
    private async Task ProcessAnnouncementAsync(UdpReceiveResult result)
    {
        try
        {
            var data = result.Buffer;
            
            // Check magic bytes (4 bytes, big endian)
            if (data.Length < 4)
            {
                return; // Too short
            }
            
            var receivedMagic = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
            
            // Check magic numbers
            switch (receivedMagic)
            {
                case Magic:
                    // Current version - all good
                    break;
                
                case v13Magic:
                    // Old version - log warning and skip (Syncthing compatibility)
                    _logger.LogWarning("Incompatible (v0.13) local discovery packet - upgrade that device to connect. Source: {RemoteEndPoint}", result.RemoteEndPoint);
                    return;
                
                default:
                    _logger.LogDebug("Incorrect magic {Magic:X8} from {RemoteEndPoint}", receivedMagic, result.RemoteEndPoint);
                    return; // Not a Syncthing announcement
            }
            
            // Extract Protobuf payload (skip first 4 bytes - magic number)
            var protobufData = data.Skip(4).ToArray();
            var announcement = DiscoveryProtocol.ParseAnnouncePacket(protobufData);
            
            if (announcement == null || string.IsNullOrEmpty(announcement.DeviceId))
            {
                return;
            }
            
            // Don't process our own announcements
            if (announcement.DeviceId == _currentDeviceId)
            {
                return;
            }
            
            _logger.LogDebug("Received local discovery announcement from device {DeviceId} at {Address}", 
                announcement.DeviceId, result.RemoteEndPoint);
            
            // Update discovered devices
            await _semaphore.WaitAsync();
            try
            {
                // Check if this is a new device or if the instance ID changed (stale cache detection)
                // Following Syncthing pattern: lib/discover/local.go registerDevice
                // isNewDevice := !existsAlready || time.Since(ce.when) > CacheLifeTime || ce.instanceID != device.InstanceId
                var existsAlready = _discoveredDevices.TryGetValue(announcement.DeviceId, out var existingDevice);
                var isNewDevice = !existsAlready ||
                    (DateTime.UtcNow - existingDevice!.LastSeen > TimeSpan.FromSeconds(CacheLifeTime)) ||
                    existingDevice.InstanceId != announcement.InstanceId;

                var device = new DiscoveredDevice
                {
                    DeviceId = announcement.DeviceId,
                    Addresses = new List<string>(announcement.Addresses),
                    LastSeen = DateTime.UtcNow,
                    Source = DiscoverySource.Local,
                    InstanceId = announcement.InstanceId // Store instance ID for stale cache detection
                };

                // If addresses are empty or contain unspecified addresses, add sender's address with default port
                if (device.Addresses.Count == 0 || device.Addresses.Any(addr => addr.Contains("0.0.0.0") || addr.Contains("::")))
                {
                    var senderAddress = $"tcp://{result.RemoteEndPoint.Address}:{_currentPort}";
                    if (!device.Addresses.Contains(senderAddress))
                    {
                        device.Addresses.Add(senderAddress);
                    }
                }

                _discoveredDevices[announcement.DeviceId] = device;

                // Fire discovery event only for new devices or when instance ID changed
                // This allows listeners to detect device restarts and refresh their state
                if (isNewDevice)
                {
                    DeviceDiscovered?.Invoke(this, new DeviceDiscoveredEventArgs(device));

                    if (existsAlready && existingDevice!.InstanceId != announcement.InstanceId)
                    {
                        _logger.LogInformation("Device {DeviceId} restarted (instance ID changed: {OldInstanceId} -> {NewInstanceId})",
                            announcement.DeviceId, existingDevice.InstanceId, announcement.InstanceId);
                    }
                    else
                    {
                        _logger.LogInformation("Discovered device {DeviceId} locally with {Count} addresses",
                            announcement.DeviceId, device.Addresses.Count);
                    }
                }
                else
                {
                    _logger.LogDebug("Updated device {DeviceId} locally with {Count} addresses",
                        announcement.DeviceId, device.Addresses.Count);
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error processing local discovery announcement from {RemoteEndPoint}", result.RemoteEndPoint);
        }
    }

    /// <summary>
    /// Broadcast announcement to local network
    /// </summary>
    private async Task BroadcastAnnouncementAsync(List<string> addresses)
    {
        try
        {
            // Create Syncthing-compatible protobuf announcement packet
            var protobufData = DiscoveryProtocol.CreateAnnouncePacket(_currentDeviceId, addresses, _instanceId);
            
            // Combine magic bytes with protobuf data
            var packet = new byte[4 + protobufData.Length];
            
            // Write magic number in big endian format
            packet[0] = (byte)((Magic >> 24) & 0xFF);
            packet[1] = (byte)((Magic >> 16) & 0xFF);
            packet[2] = (byte)((Magic >> 8) & 0xFF);
            packet[3] = (byte)(Magic & 0xFF);
            
            Array.Copy(protobufData, 0, packet, 4, protobufData.Length);
            
            // IPv4 Broadcast to subnet
            var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, _discoveryPort);
            await _broadcastClient!.SendAsync(packet, broadcastEndpoint);
            
            // IPv4 Multicast (Syncthing compatibility)
            var ipv4MulticastEndpoint = new IPEndPoint(IPAddress.Parse("224.0.0.251"), _discoveryPort);
            try
            {
                await _broadcastClient.SendAsync(packet, ipv4MulticastEndpoint);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send IPv4 multicast announcement (this is normal if multicast is disabled)");
            }
            
            // IPv6 Multicast (Syncthing compatibility) - ff12::8384 = Syncthing discovery multicast
            try 
            {
                var ipv6MulticastEndpoint = new IPEndPoint(IPAddress.Parse("ff12::8384"), _discoveryPort);
                await _broadcastClient.SendAsync(packet, ipv6MulticastEndpoint);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to send IPv6 multicast announcement (this is normal if IPv6 is disabled)");
            }
            
            _logger.LogDebug("Broadcasted local discovery announcement for device {DeviceId}", _currentDeviceId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to broadcast local discovery announcement");
        }
    }

    /// <summary>
    /// Periodic device announcement
    /// </summary>
    private async void AnnounceDevice(object? state)
    {
        if (!_isRunning) return;
        
        try
        {
            var addresses = GetLocalAddresses();
            await BroadcastAnnouncementAsync(addresses);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during local device announcement");
        }
    }

    /// <summary>
    /// Get current device's local addresses
    /// </summary>
    private List<string> GetLocalAddresses()
    {
        var addresses = new List<string>();
        
        try
        {
            // Add localhost address
            addresses.Add($"tcp://127.0.0.1:{_currentPort}");
            
            // Add local network addresses
            var hostEntry = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName());
            foreach (var address in hostEntry.AddressList)
            {
                if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    addresses.Add($"tcp://{address}:{_currentPort}");
                }
                // Add IPv6 support (Syncthing compatibility)
                else if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    addresses.Add($"tcp://[{address}]:{_currentPort}");
                }
            }
            
            // Filter undialable addresses (Syncthing compatibility)
            addresses = FilterUndialableAddresses(addresses);
            
            // Sanitize relay addresses (Syncthing compatibility) 
            addresses = SanitizeRelayAddresses(addresses);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting local addresses");
        }
        
        return addresses;
    }
    
    /// <summary>
    /// Filter out undialable addresses (localhost, multicast, broadcast, port-zero)
    /// Compatible with Syncthing's filterUndialableLocal function
    /// </summary>
    private List<string> FilterUndialableAddresses(List<string> addresses)
    {
        var filtered = new List<string>();
        
        foreach (var addr in addresses)
        {
            try
            {
                var uri = new Uri(addr);
                var tcpAddr = new IPEndPoint(IPAddress.Parse(uri.Host.Trim('[', ']')), uri.Port);
                
                // Skip undialable addresses
                if (tcpAddr.Port == 0) continue;
                if (tcpAddr.Address.Equals(IPAddress.Any)) continue;
                if (tcpAddr.Address.Equals(IPAddress.IPv6Any)) continue;
                
                // Include global unicast, link-local unicast, and unspecified addresses
                if (IsDialableAddress(tcpAddr.Address))
                {
                    filtered.Add(addr);
                }
            }
            catch
            {
                // Skip malformed addresses
                continue;
            }
        }
        
        return filtered;
    }
    
    /// <summary>
    /// Check if address is dialable (compatible with Syncthing logic)
    /// </summary>
    private bool IsDialableAddress(IPAddress address)
    {
        // Allow global unicast, link-local unicast, and unspecified addresses
        return IsIPv6GlobalUnicast(address) ||
               address.IsIPv6LinkLocal ||
               address.IsIPv4MappedToIPv6 ||
               IsIPv4GlobalUnicast(address) ||
               IsIPv4LinkLocal(address) ||
               address.Equals(IPAddress.Any) ||
               address.Equals(IPAddress.IPv6Any);
    }
    
    /// <summary>
    /// Check if IPv6 address is global unicast
    /// </summary>
    private bool IsIPv6GlobalUnicast(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            return false;
            
        var bytes = address.GetAddressBytes();
        
        // Global unicast addresses start with 2000::/3 (first 3 bits are 001)
        return (bytes[0] & 0xE0) == 0x20;
    }
    
    private bool IsIPv4GlobalUnicast(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
            
        var bytes = address.GetAddressBytes();
        
        // Exclude private ranges, loopback, multicast, broadcast
        if (bytes[0] == 127) return false; // Loopback
        if (bytes[0] == 10) return false; // Private 10.0.0.0/8
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return false; // Private 172.16.0.0/12
        if (bytes[0] == 192 && bytes[1] == 168) return false; // Private 192.168.0.0/16
        if (bytes[0] >= 224) return false; // Multicast and reserved
        
        return true;
    }
    
    private bool IsIPv4LinkLocal(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;
            
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254; // Link-local 169.254.0.0/16
    }
    
    /// <summary>
    /// Sanitize relay addresses to remove sensitive tokens (Syncthing compatibility)
    /// </summary>
    private List<string> SanitizeRelayAddresses(List<string> addresses)
    {
        var sanitized = new List<string>();
        
        foreach (var addr in addresses)
        {
            try
            {
                var uri = new Uri(addr);
                
                if (uri.Scheme == "relay")
                {
                    // For relay addresses, only keep allowlisted query parameters
                    var builder = new UriBuilder(uri);
                    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    
                    // Only keep "id" parameter (Syncthing compatibility)
                    var cleanQuery = System.Web.HttpUtility.ParseQueryString("");
                    if (query["id"] != null)
                    {
                        cleanQuery["id"] = query["id"];
                    }
                    
                    builder.Query = cleanQuery.ToString();
                    sanitized.Add(builder.ToString());
                }
                else
                {
                    sanitized.Add(addr);
                }
            }
            catch
            {
                // Skip malformed addresses
                continue;
            }
        }
        
        return sanitized;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _cancellationTokenSource.Dispose();
        _semaphore.Dispose();
        _announceTimer?.Dispose();
    }
}

