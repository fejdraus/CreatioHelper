#pragma warning disable CS1998 // Async method lacks await (for placeholder methods)
#pragma warning disable CS8601 // Possible null reference assignment
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml;

namespace CreatioHelper.Infrastructure.Services.Network;

public interface INatService : IDisposable
{
    Task<bool> StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
    Task<NatMapping?> CreateMappingAsync(string protocol, int internalPort, int externalPort = 0, string description = "CreatioHelper");
    Task<bool> RemoveMappingAsync(NatMapping mapping);
    Task<List<NatMapping>> GetActiveMappingsAsync();
    bool IsEnabled { get; }
}

public class NatMapping
{
    public string Id { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public IPAddress InternalIP { get; set; } = IPAddress.Any;
    public int InternalPort { get; set; }
    public IPAddress ExternalIP { get; set; } = IPAddress.Any;
    public int ExternalPort { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    
    public override string ToString() => $"{Protocol}:{InternalPort}->{ExternalIP}:{ExternalPort} (expires: {ExpiresAt})";
}

public class UpnpDevice
{
    public string Id { get; set; } = string.Empty;
    public string FriendlyName { get; set; } = string.Empty;
    public IPAddress LocalIP { get; set; } = IPAddress.Any;
    public IPAddress ExternalIP { get; set; } = IPAddress.Any;
    public Uri ControlUrl { get; set; } = new Uri("http://localhost");
    public string ServiceType { get; set; } = string.Empty;
    public bool SupportsIPv6 { get; set; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public bool IsStale => DateTime.UtcNow.Subtract(LastSeen).TotalMinutes > 30;
}

public class NatService : INatService, IDisposable
{
    private readonly ILogger<NatService> _logger;
    private readonly SyncConfiguration _config;
    private readonly ConcurrentDictionary<string, UpnpDevice> _devices = new();
    private readonly ConcurrentDictionary<string, NatMapping> _mappings = new();
    private readonly Timer _renewalTimer;
    private readonly Timer _discoveryTimer;
    private volatile bool _isEnabled;
    private volatile bool _isStarted;

    private const string UPNP_MULTICAST = "239.255.255.250";
    private const int UPNP_PORT = 1900;
    private const string SSDP_SEARCH = 
        "M-SEARCH * HTTP/1.1\r\n" +
        "HOST: 239.255.255.250:1900\r\n" +
        "ST: upnp:rootdevice\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        "MX: 3\r\n\r\n";

    public bool IsEnabled => _isEnabled && _isStarted;

    public NatService(ILogger<NatService> logger, IOptions<SyncConfiguration> config)
    {
        _logger = logger;
        _config = config.Value;
        _isEnabled = _config.NatTraversal?.Enabled ?? false;
        
        _renewalTimer = new Timer(ProcessRenewals, null, Timeout.Infinite, Timeout.Infinite);
        _discoveryTimer = new Timer(DiscoverDevices, null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_isEnabled || _isStarted)
            return _isStarted;

        _logger.LogInformation("Starting NAT service for automatic port mapping");

        try
        {
            // Start device discovery
            await DiscoverDevicesAsync(cancellationToken);
            
            // Start timers
            var discoveryInterval = TimeSpan.FromMinutes(_config.NatTraversal?.DiscoveryIntervalMinutes ?? 15);
            var renewalInterval = TimeSpan.FromMinutes(_config.NatTraversal?.RenewalIntervalMinutes ?? 30);
            
            _discoveryTimer.Change(discoveryInterval, discoveryInterval);
            _renewalTimer.Change(renewalInterval, renewalInterval);
            
            _isStarted = true;
            _logger.LogInformation("NAT service started successfully with {DeviceCount} UPnP devices discovered", _devices.Count);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start NAT service");
            return false;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isStarted)
            return;

        _logger.LogInformation("Stopping NAT service");

        _renewalTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _discoveryTimer.Change(Timeout.Infinite, Timeout.Infinite);

        // Remove all mappings
        var tasks = _mappings.Values.Select(RemoveMappingAsync).ToList();
        await Task.WhenAll(tasks);

        _mappings.Clear();
        _devices.Clear();
        _isStarted = false;

        _logger.LogInformation("NAT service stopped");
    }

    public async Task<NatMapping?> CreateMappingAsync(string protocol, int internalPort, int externalPort = 0, string description = "CreatioHelper")
    {
        if (!_isStarted)
        {
            _logger.LogWarning("NAT service is not started, cannot create mapping");
            return null;
        }

        var internalIP = await GetLocalIPAsync();
        if (internalIP == null)
        {
            _logger.LogError("Could not determine local IP address");
            return null;
        }

        foreach (var device in _devices.Values.Where(d => !d.IsStale))
        {
            try
            {
                var mapping = await CreateMappingOnDeviceAsync(device, protocol, internalIP, internalPort, externalPort, description);
                if (mapping != null)
                {
                    _mappings[mapping.Id] = mapping;
                    _logger.LogInformation("Created NAT mapping: {Mapping}", mapping);
                    return mapping;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to create mapping on device {DeviceId}", device.Id);
            }
        }

        _logger.LogWarning("Failed to create NAT mapping on any available device");
        return null;
    }

    public async Task<bool> RemoveMappingAsync(NatMapping mapping)
    {
        if (!_isStarted || !_mappings.ContainsKey(mapping.Id))
            return false;

        var device = _devices.Values.FirstOrDefault(d => d.Id == mapping.DeviceId);
        if (device == null)
        {
            _logger.LogWarning("Device {DeviceId} not found for mapping removal", mapping.DeviceId);
            _mappings.TryRemove(mapping.Id, out _);
            return false;
        }

        try
        {
            var success = await RemoveMappingOnDeviceAsync(device, mapping);
            if (success)
            {
                _mappings.TryRemove(mapping.Id, out _);
                _logger.LogInformation("Removed NAT mapping: {Mapping}", mapping);
            }
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove NAT mapping: {Mapping}", mapping);
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

    private async Task DiscoverDevicesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Starting UPnP device discovery");

            using var udpClient = new UdpClient();
            udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            
            var searchBytes = Encoding.UTF8.GetBytes(SSDP_SEARCH);
            var multicastEndpoint = new IPEndPoint(IPAddress.Parse(UPNP_MULTICAST), UPNP_PORT);
            
            await udpClient.SendAsync(searchBytes, searchBytes.Length, multicastEndpoint);
            
            var timeout = TimeSpan.FromSeconds(5);
            var endTime = DateTime.UtcNow.Add(timeout);
            
            while (DateTime.UtcNow < endTime && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var remainingTime = endTime.Subtract(DateTime.UtcNow);
                    if (remainingTime <= TimeSpan.Zero)
                        break;

                    udpClient.Client.ReceiveTimeout = (int)remainingTime.TotalMilliseconds;
                    var result = await udpClient.ReceiveAsync();
                    
                    var response = Encoding.UTF8.GetString(result.Buffer);
                    await ProcessSsdpResponseAsync(response, result.RemoteEndPoint.Address);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error during SSDP discovery");
                }
            }

            // Remove stale devices
            var staleDevices = _devices.Where(kvp => kvp.Value.IsStale).ToList();
            foreach (var (id, device) in staleDevices)
            {
                _devices.TryRemove(id, out _);
                _logger.LogDebug("Removed stale UPnP device: {DeviceId}", id);
            }

            _logger.LogDebug("UPnP discovery completed, found {DeviceCount} devices", _devices.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during UPnP device discovery");
        }
    }

    private async Task ProcessSsdpResponseAsync(string response, IPAddress remoteAddress)
    {
        try
        {
            if (!response.Contains("upnp:rootdevice") && !response.Contains("InternetGatewayDevice"))
                return;

            var lines = response.Split('\n');
            var location = lines.FirstOrDefault(l => l.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1]?.Trim();
            
            if (string.IsNullOrEmpty(location))
                return;

            var deviceInfo = await GetDeviceInfoAsync(location);
            if (deviceInfo != null)
            {
                deviceInfo.LastSeen = DateTime.UtcNow;
                _devices.AddOrUpdate(deviceInfo.Id, deviceInfo, (key, old) => deviceInfo);
                _logger.LogDebug("Discovered UPnP device: {DeviceName} at {DeviceIP}", deviceInfo.FriendlyName, deviceInfo.LocalIP);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error processing SSDP response from {RemoteAddress}", remoteAddress);
        }
    }

    private async Task<UpnpDevice?> GetDeviceInfoAsync(string location)
    {
        try
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var xml = await httpClient.GetStringAsync(location);
            
            var doc = new XmlDocument();
            doc.LoadXml(xml);

            var nsManager = new XmlNamespaceManager(doc.NameTable);
            nsManager.AddNamespace("upnp", "urn:schemas-upnp-org:device-1-0");

            var deviceNode = doc.SelectSingleNode("//upnp:device[upnp:deviceType[contains(text(), 'InternetGatewayDevice')]]", nsManager);
            if (deviceNode == null)
                return null;

            var friendlyName = deviceNode.SelectSingleNode("upnp:friendlyName", nsManager)?.InnerText ?? "Unknown";
            var udn = deviceNode.SelectSingleNode("upnp:UDN", nsManager)?.InnerText ?? Guid.NewGuid().ToString();

            // Find WANIPConnection or WANPPPConnection service
            var serviceNodes = doc.SelectNodes("//upnp:service", nsManager);
            Uri? controlUrl = null;
            string? serviceType = null;

            foreach (XmlNode serviceNode in serviceNodes!)
            {
                var type = serviceNode.SelectSingleNode("upnp:serviceType", nsManager)?.InnerText;
                if (type != null && (type.Contains("WANIPConnection") || type.Contains("WANPPPConnection")))
                {
                    var controlUrlPath = serviceNode.SelectSingleNode("upnp:controlURL", nsManager)?.InnerText;
                    if (!string.IsNullOrEmpty(controlUrlPath))
                    {
                        var baseUri = new Uri(location);
                        controlUrl = new Uri(baseUri, controlUrlPath);
                        serviceType = type;
                        break;
                    }
                }
            }

            if (controlUrl == null)
                return null;

            return new UpnpDevice
            {
                Id = udn,
                FriendlyName = friendlyName,
                LocalIP = new Uri(location).Host == "localhost" ? IPAddress.Loopback : IPAddress.Parse(new Uri(location).Host),
                ControlUrl = controlUrl,
                ServiceType = serviceType
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting device info from {Location}", location);
            return null;
        }
    }

    private async Task<NatMapping?> CreateMappingOnDeviceAsync(UpnpDevice device, string protocol, IPAddress internalIP, int internalPort, int externalPort, string description)
    {
        var random = new Random();
        var attempts = 0;
        
        while (attempts < 10)
        {
            var targetExternalPort = externalPort > 0 ? externalPort : random.Next(1024, 65535);
            
            try
            {
                var soapAction = device.ServiceType!.Contains("WANIPConnection") ? 
                    "urn:schemas-upnp-org:service:WANIPConnection:1#AddPortMapping" :
                    "urn:schemas-upnp-org:service:WANPPPConnection:1#AddPortMapping";

                var soapEnvelope = $@"<?xml version=""1.0""?>
<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"" soap:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
    <soap:Body>
        <m:AddPortMapping xmlns:m=""{device.ServiceType}"">
            <NewRemoteHost></NewRemoteHost>
            <NewExternalPort>{targetExternalPort}</NewExternalPort>
            <NewProtocol>{protocol.ToUpper()}</NewProtocol>
            <NewInternalPort>{internalPort}</NewInternalPort>
            <NewInternalClient>{internalIP}</NewInternalClient>
            <NewEnabled>1</NewEnabled>
            <NewPortMappingDescription>{description}</NewPortMappingDescription>
            <NewLeaseDuration>3600</NewLeaseDuration>
        </m:AddPortMapping>
    </soap:Body>
</soap:Envelope>";

                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml");
                content.Headers.Add("SOAPAction", $"\"{soapAction}\"");

                var response = await httpClient.PostAsync(device.ControlUrl, content);
                
                if (response.IsSuccessStatusCode)
                {
                    // Get external IP
                    var externalIP = await GetExternalIPAsync(device);
                    
                    return new NatMapping
                    {
                        Id = $"{device.Id}-{protocol}-{targetExternalPort}",
                        Protocol = protocol,
                        InternalIP = internalIP,
                        InternalPort = internalPort,
                        ExternalIP = externalIP ?? IPAddress.Any,
                        ExternalPort = targetExternalPort,
                        ExpiresAt = DateTime.UtcNow.AddHours(1),
                        DeviceId = device.Id,
                        Description = description
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to create mapping on attempt {Attempt} for port {Port}", attempts + 1, targetExternalPort);
            }
            
            attempts++;
            if (externalPort > 0) break; // Don't retry if specific port was requested
        }

        return null;
    }

    private async Task<bool> RemoveMappingOnDeviceAsync(UpnpDevice device, NatMapping mapping)
    {
        try
        {
            var soapAction = device.ServiceType!.Contains("WANIPConnection") ? 
                "urn:schemas-upnp-org:service:WANIPConnection:1#DeletePortMapping" :
                "urn:schemas-upnp-org:service:WANPPPConnection:1#DeletePortMapping";

            var soapEnvelope = $@"<?xml version=""1.0""?>
<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"" soap:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
    <soap:Body>
        <m:DeletePortMapping xmlns:m=""{device.ServiceType}"">
            <NewRemoteHost></NewRemoteHost>
            <NewExternalPort>{mapping.ExternalPort}</NewExternalPort>
            <NewProtocol>{mapping.Protocol.ToUpper()}</NewProtocol>
        </m:DeletePortMapping>
    </soap:Body>
</soap:Envelope>";

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPAction", $"\"{soapAction}\"");

            var response = await httpClient.PostAsync(device.ControlUrl, content);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error removing mapping on device {DeviceId}", device.Id);
            return false;
        }
    }

    private async Task<IPAddress?> GetExternalIPAsync(UpnpDevice device)
    {
        try
        {
            var soapAction = device.ServiceType!.Contains("WANIPConnection") ? 
                "urn:schemas-upnp-org:service:WANIPConnection:1#GetExternalIPAddress" :
                "urn:schemas-upnp-org:service:WANPPPConnection:1#GetExternalIPAddress";

            var soapEnvelope = $@"<?xml version=""1.0""?>
<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"" soap:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
    <soap:Body>
        <m:GetExternalIPAddress xmlns:m=""{device.ServiceType}"">
        </m:GetExternalIPAddress>
    </soap:Body>
</soap:Envelope>";

            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPAction", $"\"{soapAction}\"");

            var response = await httpClient.PostAsync(device.ControlUrl, content);
            if (!response.IsSuccessStatusCode)
                return null;

            var responseContent = await response.Content.ReadAsStringAsync();
            var doc = new XmlDocument();
            doc.LoadXml(responseContent);

            var ipNode = doc.SelectSingleNode("//*[local-name()='NewExternalIPAddress']");
            if (ipNode != null && IPAddress.TryParse(ipNode.InnerText, out var ip))
            {
                device.ExternalIP = ip;
                return ip;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting external IP from device {DeviceId}", device.Id);
        }

        return null;
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

    private void DiscoverDevices(object? state)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await DiscoverDevicesAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during scheduled device discovery");
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
                    var device = _devices.Values.FirstOrDefault(d => d.Id == mapping.DeviceId);
                    if (device != null)
                    {
                        var newMapping = await CreateMappingOnDeviceAsync(
                            device, 
                            mapping.Protocol, 
                            mapping.InternalIP, 
                            mapping.InternalPort, 
                            mapping.ExternalPort, 
                            mapping.Description);

                        if (newMapping != null)
                        {
                            _mappings[mapping.Id] = newMapping;
                            _logger.LogDebug("Renewed NAT mapping: {Mapping}", newMapping);
                        }
                        else
                        {
                            _logger.LogWarning("Failed to renew mapping: {Mapping}", mapping);
                            _mappings.TryRemove(mapping.Id, out _);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during mapping renewal");
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