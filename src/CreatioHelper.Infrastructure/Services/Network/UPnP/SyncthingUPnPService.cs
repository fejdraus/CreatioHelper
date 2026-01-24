using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace CreatioHelper.Infrastructure.Services.Network.UPnP;

/// <summary>
/// Syncthing-compatible UPnP service implementation
/// Provides Internet Gateway Device discovery and port mapping functionality
/// </summary>
public class SyncthingUPnPService : IUPnPService, IDisposable
{
    private readonly ILogger<SyncthingUPnPService> _logger;
    private readonly List<IUPnPDevice> _discoveredDevices;
    private readonly List<UPnPPortMapping> _activeMappings;
    private readonly List<UPnPPinholeResult> _activePinholes;
    private readonly object _lock = new();
    private bool _disposed = false;
    private DateTime _lastDiscovery = DateTime.MinValue;
    private string? _cachedExternalIPv4;
    private string? _cachedExternalIPv6;

    // UPnP constants matching Syncthing implementation
    private const string SsdpMulticastAddress = "239.255.255.250";
    private const string SsdpMulticastAddressIPv6 = "ff02::c";
    private const int SsdpPort = 1900;
    private const string IgdV1DeviceType = "urn:schemas-upnp-org:device:InternetGatewayDevice:1";
    private const string IgdV2DeviceType = "urn:schemas-upnp-org:device:InternetGatewayDevice:2";
    private const string WanIpv6FirewallControlService = "urn:schemas-upnp-org:service:WANIPv6FirewallControl:1";
    private const string UserAgent = "syncthing/1.0";

    public SyncthingUPnPService(ILogger<SyncthingUPnPService> logger)
    {
        _logger = logger;
        _discoveredDevices = new List<IUPnPDevice>();
        _activeMappings = new List<UPnPPortMapping>();
        _activePinholes = new List<UPnPPinholeResult>();

        _logger.LogInformation("SyncthingUPnPService initialized");
    }

    public async Task<List<IUPnPDevice>> DiscoverDevicesAsync(TimeSpan timeout = default, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncthingUPnPService));
        
        if (timeout == default) timeout = TimeSpan.FromSeconds(3);

        try
        {
            _logger.LogDebug("Starting UPnP device discovery with timeout {Timeout}", timeout);

            var devices = new List<IUPnPDevice>();
            
            // Discover IGDv2 devices first (preferred)
            var igdv2Devices = await DiscoverDevicesByTypeAsync(IgdV2DeviceType, timeout, cancellationToken);
            devices.AddRange(igdv2Devices);
            
            // Then discover IGDv1 devices
            var igdv1Devices = await DiscoverDevicesByTypeAsync(IgdV1DeviceType, timeout, cancellationToken);
            devices.AddRange(igdv1Devices);

            lock (_lock)
            {
                _discoveredDevices.Clear();
                _discoveredDevices.AddRange(devices);
            }

            _logger.LogInformation("Discovered {DeviceCount} UPnP devices", devices.Count);
            return devices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover UPnP devices");
            return new List<IUPnPDevice>();
        }
    }

    public async Task<string?> GetExternalIPAddressAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncthingUPnPService));

        try
        {
            var devices = await GetAvailableDevicesAsync(cancellationToken);
            
            foreach (var device in devices)
            {
                var ip = await device.GetExternalIPAddressAsync(cancellationToken);
                if (!string.IsNullOrEmpty(ip))
                {
                    _logger.LogDebug("Got external IP {IP} from device {DeviceId}", ip, device.DeviceId);
                    return ip;
                }
            }
            
            _logger.LogWarning("No UPnP devices could provide external IP address");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get external IP address");
            return null;
        }
    }

    public async Task<bool> AddPortMappingAsync(int externalPort, int internalPort, string protocol = "TCP", 
        string description = "Syncthing", int leaseDuration = 0, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncthingUPnPService));

        try
        {
            var devices = await GetAvailableDevicesAsync(cancellationToken);
            var localIP = GetLocalIPAddress();
            
            if (string.IsNullOrEmpty(localIP))
            {
                _logger.LogError("Could not determine local IP address for port mapping");
                return false;
            }

            bool success = false;
            foreach (var device in devices)
            {
                var result = await device.AddPortMappingAsync(externalPort, internalPort, localIP, 
                    protocol, description, leaseDuration, cancellationToken);
                
                if (result)
                {
                    _logger.LogInformation("Successfully mapped port {ExternalPort}:{Protocol} -> {InternalIP}:{InternalPort} on device {DeviceId}",
                        externalPort, protocol, localIP, internalPort, device.DeviceId);
                    success = true;
                }
                else
                {
                    _logger.LogWarning("Failed to map port on device {DeviceId}", device.DeviceId);
                }
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add port mapping {ExternalPort}:{Protocol}", externalPort, protocol);
            return false;
        }
    }

    public async Task<bool> DeletePortMappingAsync(int externalPort, string protocol = "TCP", CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncthingUPnPService));

        try
        {
            var devices = await GetAvailableDevicesAsync(cancellationToken);
            
            bool success = false;
            foreach (var device in devices)
            {
                var result = await device.DeletePortMappingAsync(externalPort, protocol, cancellationToken);
                
                if (result)
                {
                    _logger.LogInformation("Successfully deleted port mapping {ExternalPort}:{Protocol} on device {DeviceId}",
                        externalPort, protocol, device.DeviceId);
                    success = true;
                }
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete port mapping {ExternalPort}:{Protocol}", externalPort, protocol);
            return false;
        }
    }

    public async Task<List<UPnPPortMapping>> GetPortMappingsAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncthingUPnPService));

        try
        {
            var allMappings = new List<UPnPPortMapping>();
            var devices = await GetAvailableDevicesAsync(cancellationToken);
            
            foreach (var device in devices)
            {
                var mappings = await device.GetPortMappingsAsync(cancellationToken);
                allMappings.AddRange(mappings);
            }

            _logger.LogDebug("Retrieved {MappingCount} total port mappings", allMappings.Count);
            return allMappings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get port mappings");
            return new List<UPnPPortMapping>();
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncthingUPnPService));

        try
        {
            var devices = await DiscoverDevicesAsync(TimeSpan.FromSeconds(2), cancellationToken);
            var isAvailable = devices.Count > 0;
            
            _logger.LogDebug("UPnP availability check: {IsAvailable} ({DeviceCount} devices)", isAvailable, devices.Count);
            return isAvailable;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to check UPnP availability");
            return false;
        }
    }

    private async Task<List<IUPnPDevice>> DiscoverDevicesByTypeAsync(string deviceType, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var devices = new List<IUPnPDevice>();
        
        try
        {
            // Create M-SEARCH message
            var searchMessage = CreateMSearchMessage(deviceType);
            var searchBytes = Encoding.UTF8.GetBytes(searchMessage);

            using var udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            
            var multicastEndpoint = new IPEndPoint(IPAddress.Parse(SsdpMulticastAddress), SsdpPort);
            
            // Send discovery message
            await udpClient.SendAsync(searchBytes, multicastEndpoint, cancellationToken);
            _logger.LogDebug("Sent M-SEARCH for device type {DeviceType}", deviceType);

            // Listen for responses
            var startTime = DateTime.UtcNow;
            udpClient.Client.ReceiveTimeout = 250; // 250ms timeout per receive
            
            while (DateTime.UtcNow - startTime < timeout && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var result = await udpClient.ReceiveAsync().WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken);
                    var response = Encoding.UTF8.GetString(result.Buffer);
                    
                    _logger.LogDebug("Received UPnP response from {RemoteEndPoint}", result.RemoteEndPoint);
                    
                    var device = await ParseDiscoveryResponseAsync(response, result.RemoteEndPoint, deviceType, cancellationToken);
                    if (device != null)
                    {
                        devices.Add(device);
                    }
                }
                catch (TimeoutException)
                {
                    // Timeout is expected, continue listening
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during device discovery for type {DeviceType}", deviceType);
        }

        return devices;
    }

    private Task<IUPnPDevice?> ParseDiscoveryResponseAsync(string response, IPEndPoint remoteEndPoint, string expectedDeviceType, CancellationToken cancellationToken)
    {
        try
        {
            // Parse HTTP response headers
            var lines = response.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            
            foreach (var line in lines.Skip(1)) // Skip status line
            {
                var colonIndex = line.IndexOf(':');
                if (colonIndex > 0)
                {
                    var key = line.Substring(0, colonIndex).Trim();
                    var value = line.Substring(colonIndex + 1).Trim();
                    headers[key] = value;
                }
            }

            // Verify device type matches
            if (!headers.TryGetValue("ST", out var deviceType) || deviceType != expectedDeviceType)
            {
                return Task.FromResult<IUPnPDevice?>(null);
            }

            // Get device description URL
            if (!headers.TryGetValue("LOCATION", out var location) || string.IsNullOrEmpty(location))
            {
                _logger.LogWarning("UPnP device response missing LOCATION header");
                return Task.FromResult<IUPnPDevice?>(null);
            }

            // Get USN (Unique Service Name)
            headers.TryGetValue("USN", out var usn);

            // Create a simple UPnP device implementation
            return Task.FromResult<IUPnPDevice?>(new SimpleUPnPDevice(location, usn ?? "unknown", deviceType, remoteEndPoint.Address.ToString(), _logger));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse UPnP discovery response");
            return Task.FromResult<IUPnPDevice?>(null);
        }
    }

    private string CreateMSearchMessage(string deviceType)
    {
        return $"M-SEARCH * HTTP/1.1\r\n" +
               $"HOST: {SsdpMulticastAddress}:{SsdpPort}\r\n" +
               $"ST: {deviceType}\r\n" +
               $"MAN: \"ssdp:discover\"\r\n" +
               $"MX: 3\r\n" +
               $"USER-AGENT: {UserAgent}\r\n" +
               $"\r\n";
    }

    private async Task<List<IUPnPDevice>> GetAvailableDevicesAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_discoveredDevices.Count > 0)
            {
                return new List<IUPnPDevice>(_discoveredDevices);
            }
        }

        // If no devices cached, try to discover them
        return await DiscoverDevicesAsync(TimeSpan.FromSeconds(3), cancellationToken);
    }

    private string? GetLocalIPAddress()
    {
        try
        {
            var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up && 
                            ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            foreach (var ni in networkInterfaces)
            {
                var ipProps = ni.GetIPProperties();
                var ipv4Addresses = ipProps.UnicastAddresses
                    .Where(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                                  !IPAddress.IsLoopback(addr.Address))
                    .Select(addr => addr.Address);

                var localAddr = ipv4Addresses.FirstOrDefault();
                if (localAddr != null)
                {
                    return localAddr.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to determine local IP address");
        }

        return null;
    }

    public async Task<UPnPAnyPortMappingResult?> AddAnyPortMappingAsync(int internalPort, string protocol = "TCP",
        string description = "Syncthing", int leaseDuration = 3600, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncthingUPnPService));

        try
        {
            var devices = await GetAvailableDevicesAsync(cancellationToken);
            var localIP = GetLocalIPAddress();

            if (string.IsNullOrEmpty(localIP))
            {
                _logger.LogError("Could not determine local IP address for AddAnyPortMapping");
                return null;
            }

            // Try IGDv2 devices first (they support AddAnyPortMapping)
            var igdv2Devices = devices.Where(d => d.DeviceType.Contains(":2")).ToList();

            foreach (var device in igdv2Devices)
            {
                var result = await TryAddAnyPortMappingOnDeviceAsync(device, internalPort, localIP, protocol, description, leaseDuration, cancellationToken);
                if (result != null)
                {
                    lock (_lock)
                    {
                        _activeMappings.Add(new UPnPPortMapping
                        {
                            ExternalPort = result.AssignedExternalPort,
                            InternalPort = internalPort,
                            InternalClient = localIP,
                            Protocol = protocol,
                            Description = description,
                            LeaseDuration = leaseDuration,
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                    return result;
                }
            }

            // Fallback: Try standard AddPortMapping with requested port = internal port
            _logger.LogDebug("No IGDv2 device available for AddAnyPortMapping, falling back to standard mapping");
            var standardSuccess = await AddPortMappingAsync(internalPort, internalPort, protocol, description, leaseDuration, cancellationToken);
            if (standardSuccess)
            {
                return new UPnPAnyPortMappingResult
                {
                    AssignedExternalPort = internalPort,
                    InternalPort = internalPort,
                    Protocol = protocol,
                    LeaseDuration = leaseDuration,
                    ExpiresAt = leaseDuration > 0 ? DateTime.UtcNow.AddSeconds(leaseDuration) : DateTime.MaxValue
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add any port mapping for internal port {InternalPort}", internalPort);
            return null;
        }
    }

    private async Task<UPnPAnyPortMappingResult?> TryAddAnyPortMappingOnDeviceAsync(IUPnPDevice device, int internalPort,
        string localIP, string protocol, string description, int leaseDuration, CancellationToken cancellationToken)
    {
        try
        {
            // IGDv2 AddAnyPortMapping action - router assigns the external port
            if (device is SimpleUPnPDevice simpleDevice)
            {
                var result = await simpleDevice.AddAnyPortMappingAsync(internalPort, localIP, protocol, description, leaseDuration, cancellationToken);
                if (result != null)
                {
                    _logger.LogInformation("IGDv2 AddAnyPortMapping succeeded: internal {InternalPort} -> external {ExternalPort}",
                        internalPort, result.AssignedExternalPort);
                    return result;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "IGDv2 AddAnyPortMapping failed on device {DeviceId}", device.DeviceId);
        }
        return null;
    }

    public async Task<bool> DeletePortMappingRangeAsync(int startPort, int endPort, string protocol = "TCP", CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncthingUPnPService));

        try
        {
            var devices = await GetAvailableDevicesAsync(cancellationToken);
            var deletedCount = 0;

            for (int port = startPort; port <= endPort; port++)
            {
                if (await DeletePortMappingAsync(port, protocol, cancellationToken))
                {
                    deletedCount++;
                }
            }

            _logger.LogInformation("Deleted {Count} port mappings in range {Start}-{End}:{Protocol}",
                deletedCount, startPort, endPort, protocol);

            return deletedCount > 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete port mapping range {Start}-{End}:{Protocol}", startPort, endPort, protocol);
            return false;
        }
    }

    public async Task<UPnPPinholeResult?> AddPinholeAsync(IPAddress remoteHost, int remotePort,
        IPAddress internalClient, int internalPort, string protocol, int leaseTime,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncthingUPnPService));

        try
        {
            var devices = await GetAvailableDevicesAsync(cancellationToken);
            var ipv6Devices = devices.Where(d => d.SupportsIPv6).ToList();

            if (!ipv6Devices.Any())
            {
                _logger.LogDebug("No IPv6-capable devices found for pinhole creation");
                return null;
            }

            foreach (var device in ipv6Devices)
            {
                if (device is SimpleUPnPDevice simpleDevice)
                {
                    var result = await simpleDevice.AddPinholeAsync(remoteHost, remotePort, internalClient, internalPort, protocol, leaseTime, cancellationToken);
                    if (result != null)
                    {
                        lock (_lock)
                        {
                            _activePinholes.Add(result);
                        }
                        _logger.LogInformation("Created IPv6 pinhole: UniqueId={UniqueId}, {RemoteHost}:{RemotePort} -> {InternalClient}:{InternalPort}",
                            result.UniqueId, remoteHost, remotePort, internalClient, internalPort);
                        return result;
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add IPv6 pinhole");
            return null;
        }
    }

    public async Task<bool> DeletePinholeAsync(int uniqueId, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SyncthingUPnPService));

        try
        {
            var devices = await GetAvailableDevicesAsync(cancellationToken);
            var ipv6Devices = devices.Where(d => d.SupportsIPv6).ToList();

            foreach (var device in ipv6Devices)
            {
                if (device is SimpleUPnPDevice simpleDevice)
                {
                    var success = await simpleDevice.DeletePinholeAsync(uniqueId, cancellationToken);
                    if (success)
                    {
                        lock (_lock)
                        {
                            _activePinholes.RemoveAll(p => p.UniqueId == uniqueId);
                        }
                        _logger.LogInformation("Deleted IPv6 pinhole: UniqueId={UniqueId}", uniqueId);
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete IPv6 pinhole {UniqueId}", uniqueId);
            return false;
        }
    }

    public UPnPServiceStatus GetStatus()
    {
        lock (_lock)
        {
            var igdv1Count = _discoveredDevices.Count(d => !d.DeviceType.Contains(":2"));
            var igdv2Count = _discoveredDevices.Count(d => d.DeviceType.Contains(":2"));
            var ipv6Count = _discoveredDevices.Count(d => d.SupportsIPv6);

            return new UPnPServiceStatus
            {
                IsAvailable = _discoveredDevices.Count > 0,
                DeviceCount = _discoveredDevices.Count,
                Igdv1DeviceCount = igdv1Count,
                Igdv2DeviceCount = igdv2Count,
                Ipv6DeviceCount = ipv6Count,
                ActiveMappingCount = _activeMappings.Count,
                ActivePinholeCount = _activePinholes.Count,
                ExternalIPv4 = _cachedExternalIPv4,
                ExternalIPv6 = _cachedExternalIPv6,
                LastDiscovery = _lastDiscovery,
                Devices = _discoveredDevices.Select(d => new UPnPDeviceStatus
                {
                    DeviceId = d.DeviceId,
                    FriendlyName = d.FriendlyName,
                    DeviceType = d.DeviceType,
                    SupportsIgdv2 = d.DeviceType.Contains(":2"),
                    SupportsIpv6 = d.SupportsIPv6,
                    ExternalIP = null // Would need async call
                }).ToList()
            };
        }
    }

    /// <summary>
    /// Enhanced port mapping with UPnP error code handling
    /// </summary>
    private async Task<(bool Success, int? ErrorCode)> AddPortMappingWithErrorHandlingAsync(
        IUPnPDevice device, int externalPort, int internalPort, string localIP,
        string protocol, string description, int leaseDuration, CancellationToken cancellationToken)
    {
        try
        {
            var success = await device.AddPortMappingAsync(externalPort, internalPort, localIP, protocol, description, leaseDuration, cancellationToken);

            if (success)
            {
                return (true, null);
            }

            // The SimpleUPnPDevice doesn't return error codes directly, but we can infer from the result
            // In a full implementation, we would parse the SOAP fault response
            return (false, null);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Port mapping failed with exception");
            return (false, null);
        }
    }

    /// <summary>
    /// Try to add port mapping with automatic error handling and retry logic
    /// </summary>
    public async Task<bool> AddPortMappingWithRetryAsync(int externalPort, int internalPort, string protocol = "TCP",
        string description = "Syncthing", int leaseDuration = 3600, CancellationToken cancellationToken = default)
    {
        // First attempt
        var success = await AddPortMappingAsync(externalPort, internalPort, protocol, description, leaseDuration, cancellationToken);
        if (success) return true;

        // If failed with lease duration, try with permanent lease (duration=0)
        _logger.LogDebug("Port mapping failed, retrying with permanent lease (duration=0)");
        success = await AddPortMappingAsync(externalPort, internalPort, protocol, description, 0, cancellationToken);
        if (success) return true;

        // If port conflict, try a different port
        _logger.LogDebug("Port mapping failed, trying alternative port");
        for (int offset = 1; offset <= 10; offset++)
        {
            var altPort = externalPort + offset;
            success = await AddPortMappingAsync(altPort, internalPort, protocol, description, 0, cancellationToken);
            if (success)
            {
                _logger.LogInformation("Port mapping succeeded with alternative external port {AltPort}", altPort);
                return true;
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            lock (_lock)
            {
                _discoveredDevices.Clear();
                _activeMappings.Clear();
                _activePinholes.Clear();
            }
            _disposed = true;
            _logger.LogDebug("SyncthingUPnPService disposed");
        }
    }
}