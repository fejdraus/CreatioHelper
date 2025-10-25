using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text;
using System.Xml.Linq;

namespace CreatioHelper.Infrastructure.Services.Network.UPnP;

/// <summary>
/// Simple implementation of UPnP Internet Gateway Device
/// Compatible with Syncthing's approach to UPnP port mapping
/// </summary>
public class SimpleUPnPDevice : IUPnPDevice, IDisposable
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private bool _disposed = false;

    // UPnP service types
    private const string WanIpConnectionV1 = "urn:schemas-upnp-org:service:WANIPConnection:1";
    private const string WanIpConnectionV2 = "urn:schemas-upnp-org:service:WANIPConnection:2";
    private const string WanPppConnectionV1 = "urn:schemas-upnp-org:service:WANPPPConnection:1";
    private const string WanPppConnectionV2 = "urn:schemas-upnp-org:service:WANPPPConnection:2";

    public string DeviceId { get; }
    public string FriendlyName { get; private set; } = "Unknown Device";
    public string DeviceType { get; }
    public bool SupportsIPv6 { get; private set; } = false;
    public string ControlUrl { get; private set; } = string.Empty;
    public string LocalIPAddress { get; }

    public SimpleUPnPDevice(string locationUrl, string deviceId, string deviceType, string localIPAddress, ILogger logger)
    {
        DeviceId = deviceId;
        DeviceType = deviceType;
        LocalIPAddress = localIPAddress;
        _logger = logger;

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "syncthing/1.0");
        
        _baseUrl = GetBaseUrl(locationUrl);
        
        // Initialize device info asynchronously (fire and forget)
        _ = Task.Run(async () => await InitializeDeviceAsync(locationUrl));
    }

    public async Task<bool> AddPortMappingAsync(int externalPort, int internalPort, string localIP, 
        string protocol, string description, int leaseDuration, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimpleUPnPDevice));

        try
        {
            if (string.IsNullOrEmpty(ControlUrl))
            {
                _logger.LogWarning("Device {DeviceId} has no control URL available", DeviceId);
                return false;
            }

            var soapAction = "AddPortMapping";
            var serviceType = GetServiceType();
            
            var soapBody = $@"
                <u:{soapAction} xmlns:u=""{serviceType}"">
                    <NewRemoteHost></NewRemoteHost>
                    <NewExternalPort>{externalPort}</NewExternalPort>
                    <NewProtocol>{protocol}</NewProtocol>
                    <NewInternalPort>{internalPort}</NewInternalPort>
                    <NewInternalClient>{localIP}</NewInternalClient>
                    <NewEnabled>1</NewEnabled>
                    <NewPortMappingDescription>{description}</NewPortMappingDescription>
                    <NewLeaseDuration>{leaseDuration}</NewLeaseDuration>
                </u:{soapAction}>";

            var response = await SendSoapRequestAsync(soapAction, serviceType, soapBody, cancellationToken);
            var success = !string.IsNullOrEmpty(response) && !response.Contains("soap:Fault");

            if (success)
            {
                _logger.LogDebug("Successfully added port mapping {ExternalPort}:{Protocol} on device {DeviceId}", 
                    externalPort, protocol, DeviceId);
            }
            else
            {
                _logger.LogWarning("Failed to add port mapping on device {DeviceId}: {Response}", DeviceId, response);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding port mapping on device {DeviceId}", DeviceId);
            return false;
        }
    }

    public async Task<bool> DeletePortMappingAsync(int externalPort, string protocol, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimpleUPnPDevice));

        try
        {
            if (string.IsNullOrEmpty(ControlUrl))
            {
                _logger.LogWarning("Device {DeviceId} has no control URL available", DeviceId);
                return false;
            }

            var soapAction = "DeletePortMapping";
            var serviceType = GetServiceType();
            
            var soapBody = $@"
                <u:{soapAction} xmlns:u=""{serviceType}"">
                    <NewRemoteHost></NewRemoteHost>
                    <NewExternalPort>{externalPort}</NewExternalPort>
                    <NewProtocol>{protocol}</NewProtocol>
                </u:{soapAction}>";

            var response = await SendSoapRequestAsync(soapAction, serviceType, soapBody, cancellationToken);
            var success = !string.IsNullOrEmpty(response) && !response.Contains("soap:Fault");

            if (success)
            {
                _logger.LogDebug("Successfully deleted port mapping {ExternalPort}:{Protocol} on device {DeviceId}", 
                    externalPort, protocol, DeviceId);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting port mapping on device {DeviceId}", DeviceId);
            return false;
        }
    }

    public async Task<string?> GetExternalIPAddressAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimpleUPnPDevice));

        try
        {
            if (string.IsNullOrEmpty(ControlUrl))
            {
                return null;
            }

            var soapAction = "GetExternalIPAddress";
            var serviceType = GetServiceType();
            
            var soapBody = $@"<u:{soapAction} xmlns:u=""{serviceType}""></u:{soapAction}>";

            var response = await SendSoapRequestAsync(soapAction, serviceType, soapBody, cancellationToken);
            
            if (string.IsNullOrEmpty(response) || response.Contains("soap:Fault"))
            {
                return null;
            }

            // Parse XML response to extract IP address
            try
            {
                var xDoc = XDocument.Parse(response);
                var ipElement = xDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "NewExternalIPAddress");
                return ipElement?.Value;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse external IP response from device {DeviceId}", DeviceId);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting external IP from device {DeviceId}", DeviceId);
            return null;
        }
    }

    public Task<List<UPnPPortMapping>> GetPortMappingsAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimpleUPnPDevice));

        var mappings = new List<UPnPPortMapping>();

        try
        {
            if (string.IsNullOrEmpty(ControlUrl))
            {
                return Task.FromResult(mappings);
            }

            // Try to get existing port mappings (this is complex and device-specific)
            // For now, return empty list - full implementation would enumerate existing mappings
            _logger.LogDebug("Port mapping enumeration not fully implemented for device {DeviceId}", DeviceId);
            
            return Task.FromResult(mappings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting port mappings from device {DeviceId}", DeviceId);
            return Task.FromResult(mappings);
        }
    }

    private async Task InitializeDeviceAsync(string locationUrl)
    {
        try
        {
            _logger.LogDebug("Initializing device information from {LocationUrl}", locationUrl);

            var response = await _httpClient.GetStringAsync(locationUrl);
            var xDoc = XDocument.Parse(response);

            // Extract friendly name
            var friendlyNameElement = xDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "friendlyName");
            if (friendlyNameElement != null)
            {
                FriendlyName = friendlyNameElement.Value;
            }

            // Find control URL for WAN connection service
            var controlUrlElement = FindControlUrl(xDoc);
            if (controlUrlElement != null)
            {
                ControlUrl = ResolveControlUrl(controlUrlElement.Value, locationUrl);
            }

            // Check IPv6 support based on device type
            SupportsIPv6 = DeviceType.Contains(":2"); // IGDv2 supports IPv6

            _logger.LogDebug("Device {DeviceId} initialized: {FriendlyName}, ControlUrl: {ControlUrl}", 
                DeviceId, FriendlyName, ControlUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize device {DeviceId} from {LocationUrl}", DeviceId, locationUrl);
        }
    }

    private XElement? FindControlUrl(XDocument deviceDoc)
    {
        // Look for WANIPConnection or WANPPPConnection services
        var services = deviceDoc.Descendants().Where(e => e.Name.LocalName == "service");
        
        foreach (var service in services)
        {
            var serviceTypeElement = service.Descendants().FirstOrDefault(e => e.Name.LocalName == "serviceType");
            if (serviceTypeElement != null)
            {
                var serviceType = serviceTypeElement.Value;
                if (serviceType == WanIpConnectionV1 || serviceType == WanIpConnectionV2 ||
                    serviceType == WanPppConnectionV1 || serviceType == WanPppConnectionV2)
                {
                    return service.Descendants().FirstOrDefault(e => e.Name.LocalName == "controlURL");
                }
            }
        }

        return null;
    }

    private string ResolveControlUrl(string controlUrl, string baseUrl)
    {
        if (Uri.IsWellFormedUriString(controlUrl, UriKind.Absolute))
        {
            return controlUrl;
        }

        var baseUri = new Uri(baseUrl);
        if (controlUrl.StartsWith("/"))
        {
            return $"{baseUri.Scheme}://{baseUri.Authority}{controlUrl}";
        }
        else
        {
            return $"{baseUri.Scheme}://{baseUri.Authority}{baseUri.AbsolutePath.TrimEnd('/')}/{controlUrl}";
        }
    }

    private async Task<string> SendSoapRequestAsync(string action, string serviceType, string soapBody, CancellationToken cancellationToken)
    {
        var soapEnvelope = $@"<?xml version=""1.0""?>
<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"" soap:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
    <soap:Body>
        {soapBody}
    </soap:Body>
</soap:Envelope>";

        var content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPAction", $"\"{serviceType}#{action}\"");

        var response = await _httpClient.PostAsync(ControlUrl, content, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private string GetServiceType()
    {
        // Return appropriate service type based on device version
        return DeviceType.Contains(":2") ? WanIpConnectionV2 : WanIpConnectionV1;
    }

    private string GetBaseUrl(string locationUrl)
    {
        var uri = new Uri(locationUrl);
        return $"{uri.Scheme}://{uri.Authority}";
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient?.Dispose();
            _disposed = true;
        }
    }
}