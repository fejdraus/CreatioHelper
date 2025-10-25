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
    private readonly object _lock = new();
    private bool _disposed = false;

    // UPnP constants matching Syncthing implementation
    private const string SsdpMulticastAddress = "239.255.255.250";
    private const int SsdpPort = 1900;
    private const string IgdV1DeviceType = "urn:schemas-upnp-org:device:InternetGatewayDevice:1";
    private const string IgdV2DeviceType = "urn:schemas-upnp-org:device:InternetGatewayDevice:2";
    private const string UserAgent = "syncthing/1.0";

    public SyncthingUPnPService(ILogger<SyncthingUPnPService> logger)
    {
        _logger = logger;
        _discoveredDevices = new List<IUPnPDevice>();
        
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

    public void Dispose()
    {
        if (!_disposed)
        {
            lock (_lock)
            {
                _discoveredDevices.Clear();
            }
            _disposed = true;
            _logger.LogDebug("SyncthingUPnPService disposed");
        }
    }
}