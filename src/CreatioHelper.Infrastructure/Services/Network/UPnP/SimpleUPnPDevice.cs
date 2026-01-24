using Microsoft.Extensions.Logging;
using System.Net;
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
    private const string WanIpv6FirewallControl = "urn:schemas-upnp-org:service:WANIPv6FirewallControl:1";

    public string DeviceId { get; }
    public string FriendlyName { get; private set; } = "Unknown Device";
    public string DeviceType { get; }
    public bool SupportsIPv6 { get; private set; } = false;
    public bool SupportsIgdv2 => DeviceType.Contains(":2");
    public string ControlUrl { get; private set; } = string.Empty;
    public string Ipv6ControlUrl { get; private set; } = string.Empty;
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

    public async Task<List<UPnPPortMapping>> GetPortMappingsAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimpleUPnPDevice));

        var mappings = new List<UPnPPortMapping>();

        try
        {
            if (string.IsNullOrEmpty(ControlUrl))
            {
                return mappings;
            }

            // Enumerate all port mappings using GetGenericPortMappingEntry
            // Start from index 0 and increment until we get an error (714 = NoSuchEntryInArray)
            int index = 0;
            const int maxMappings = 1000; // Safety limit

            while (index < maxMappings)
            {
                var mapping = await GetGenericPortMappingEntryAsync(index, cancellationToken);
                if (mapping == null)
                {
                    // No more entries or error occurred
                    break;
                }

                mappings.Add(mapping);
                index++;
            }

            _logger.LogDebug("Retrieved {MappingCount} port mappings from device {DeviceId}", mappings.Count, DeviceId);
            return mappings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting port mappings from device {DeviceId}", DeviceId);
            return mappings;
        }
    }

    /// <summary>
    /// IGDv2: Add any port mapping (router assigns the external port)
    /// </summary>
    public async Task<UPnPAnyPortMappingResult?> AddAnyPortMappingAsync(int internalPort, string localIP,
        string protocol, string description, int leaseDuration, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimpleUPnPDevice));

        // This feature requires IGDv2
        if (!SupportsIgdv2)
        {
            _logger.LogDebug("Device {DeviceId} does not support IGDv2 AddAnyPortMapping", DeviceId);
            return null;
        }

        try
        {
            if (string.IsNullOrEmpty(ControlUrl))
            {
                _logger.LogWarning("Device {DeviceId} has no control URL available", DeviceId);
                return null;
            }

            var soapAction = "AddAnyPortMapping";
            var serviceType = WanIpConnectionV2;

            var soapBody = $@"
                <u:{soapAction} xmlns:u=""{serviceType}"">
                    <NewRemoteHost></NewRemoteHost>
                    <NewExternalPort>0</NewExternalPort>
                    <NewProtocol>{protocol}</NewProtocol>
                    <NewInternalPort>{internalPort}</NewInternalPort>
                    <NewInternalClient>{localIP}</NewInternalClient>
                    <NewEnabled>1</NewEnabled>
                    <NewPortMappingDescription>{description}</NewPortMappingDescription>
                    <NewLeaseDuration>{leaseDuration}</NewLeaseDuration>
                </u:{soapAction}>";

            var response = await SendSoapRequestAsync(soapAction, serviceType, soapBody, cancellationToken);

            if (string.IsNullOrEmpty(response) || response.Contains("soap:Fault"))
            {
                // Check for specific error codes
                var errorCode = ParseSoapErrorCode(response);
                if (errorCode.HasValue)
                {
                    _logger.LogDebug("IGDv2 AddAnyPortMapping failed with error code {ErrorCode}", errorCode);
                }
                return null;
            }

            // Parse response to get assigned external port
            var xDoc = XDocument.Parse(response);
            var assignedPort = ParseIntElement(xDoc, "NewReservedPort");

            if (assignedPort == 0)
            {
                _logger.LogWarning("IGDv2 AddAnyPortMapping returned invalid port");
                return null;
            }

            _logger.LogDebug("IGDv2 AddAnyPortMapping succeeded: internal {InternalPort} -> external {ExternalPort}",
                internalPort, assignedPort);

            return new UPnPAnyPortMappingResult
            {
                AssignedExternalPort = assignedPort,
                InternalPort = internalPort,
                Protocol = protocol,
                LeaseDuration = leaseDuration,
                ExpiresAt = leaseDuration > 0 ? DateTime.UtcNow.AddSeconds(leaseDuration) : DateTime.MaxValue
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in IGDv2 AddAnyPortMapping on device {DeviceId}", DeviceId);
            return null;
        }
    }

    /// <summary>
    /// IPv6: Add a firewall pinhole for incoming connections
    /// </summary>
    public async Task<UPnPPinholeResult?> AddPinholeAsync(IPAddress remoteHost, int remotePort,
        IPAddress internalClient, int internalPort, string protocol, int leaseTime,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimpleUPnPDevice));

        if (!SupportsIPv6 || string.IsNullOrEmpty(Ipv6ControlUrl))
        {
            _logger.LogDebug("Device {DeviceId} does not support IPv6 pinholes", DeviceId);
            return null;
        }

        try
        {
            var soapAction = "AddPinhole";
            var serviceType = WanIpv6FirewallControl;

            // Protocol numbers: 6 = TCP, 17 = UDP, 0 = all
            var protocolNumber = protocol.ToUpperInvariant() switch
            {
                "TCP" => 6,
                "UDP" => 17,
                _ => 0
            };

            var soapBody = $@"
                <u:{soapAction} xmlns:u=""{serviceType}"">
                    <RemoteHost>{(remoteHost.Equals(IPAddress.IPv6Any) ? "" : remoteHost)}</RemoteHost>
                    <RemotePort>{remotePort}</RemotePort>
                    <InternalClient>{internalClient}</InternalClient>
                    <InternalPort>{internalPort}</InternalPort>
                    <Protocol>{protocolNumber}</Protocol>
                    <LeaseTime>{leaseTime}</LeaseTime>
                </u:{soapAction}>";

            var response = await SendSoapRequestWithUrlAsync(Ipv6ControlUrl, soapAction, serviceType, soapBody, cancellationToken);

            if (string.IsNullOrEmpty(response) || response.Contains("soap:Fault"))
            {
                _logger.LogDebug("IPv6 AddPinhole failed on device {DeviceId}", DeviceId);
                return null;
            }

            // Parse response to get UniqueID
            var xDoc = XDocument.Parse(response);
            var uniqueId = ParseIntElement(xDoc, "UniqueID");

            if (uniqueId == 0)
            {
                _logger.LogWarning("IPv6 AddPinhole returned invalid UniqueID");
                return null;
            }

            _logger.LogDebug("IPv6 AddPinhole succeeded: UniqueID={UniqueId}", uniqueId);

            return new UPnPPinholeResult
            {
                UniqueId = uniqueId,
                RemoteHost = remoteHost,
                RemotePort = remotePort,
                InternalClient = internalClient,
                InternalPort = internalPort,
                Protocol = protocol,
                LeaseTime = leaseTime,
                ExpiresAt = leaseTime > 0 ? DateTime.UtcNow.AddSeconds(leaseTime) : DateTime.MaxValue
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in IPv6 AddPinhole on device {DeviceId}", DeviceId);
            return null;
        }
    }

    /// <summary>
    /// IPv6: Delete a firewall pinhole by its unique ID
    /// </summary>
    public async Task<bool> DeletePinholeAsync(int uniqueId, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimpleUPnPDevice));

        if (!SupportsIPv6 || string.IsNullOrEmpty(Ipv6ControlUrl))
        {
            return false;
        }

        try
        {
            var soapAction = "DeletePinhole";
            var serviceType = WanIpv6FirewallControl;

            var soapBody = $@"
                <u:{soapAction} xmlns:u=""{serviceType}"">
                    <UniqueID>{uniqueId}</UniqueID>
                </u:{soapAction}>";

            var response = await SendSoapRequestWithUrlAsync(Ipv6ControlUrl, soapAction, serviceType, soapBody, cancellationToken);

            var success = !string.IsNullOrEmpty(response) && !response.Contains("soap:Fault");

            if (success)
            {
                _logger.LogDebug("IPv6 DeletePinhole succeeded: UniqueID={UniqueId}", uniqueId);
            }

            return success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in IPv6 DeletePinhole on device {DeviceId}", DeviceId);
            return false;
        }
    }

    /// <summary>
    /// Get a specific port mapping entry by index
    /// </summary>
    private async Task<UPnPPortMapping?> GetGenericPortMappingEntryAsync(int index, CancellationToken cancellationToken)
    {
        try
        {
            var soapAction = "GetGenericPortMappingEntry";
            var serviceType = GetServiceType();

            var soapBody = $@"
                <u:{soapAction} xmlns:u=""{serviceType}"">
                    <NewPortMappingIndex>{index}</NewPortMappingIndex>
                </u:{soapAction}>";

            var response = await SendSoapRequestAsync(soapAction, serviceType, soapBody, cancellationToken);

            if (string.IsNullOrEmpty(response) || response.Contains("soap:Fault"))
            {
                // Check if this is the "no such entry" error (714) which is expected at end of list
                if (response?.Contains("714") == true || response?.Contains("NoSuchEntryInArray") == true ||
                    response?.Contains("SpecifiedArrayIndexInvalid") == true)
                {
                    return null;
                }

                _logger.LogDebug("GetGenericPortMappingEntry returned fault for index {Index}", index);
                return null;
            }

            // Parse XML response
            var xDoc = XDocument.Parse(response);

            var mapping = new UPnPPortMapping
            {
                ExternalPort = ParseIntElement(xDoc, "NewExternalPort"),
                InternalPort = ParseIntElement(xDoc, "NewInternalPort"),
                InternalClient = ParseStringElement(xDoc, "NewInternalClient") ?? string.Empty,
                Protocol = ParseStringElement(xDoc, "NewProtocol") ?? "TCP",
                Description = ParseStringElement(xDoc, "NewPortMappingDescription") ?? string.Empty,
                Enabled = ParseIntElement(xDoc, "NewEnabled") == 1,
                LeaseDuration = ParseIntElement(xDoc, "NewLeaseDuration")
            };

            return mapping;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error getting port mapping entry at index {Index}", index);
            return null;
        }
    }

    /// <summary>
    /// Check if a specific port mapping exists
    /// </summary>
    public async Task<UPnPPortMapping?> GetSpecificPortMappingEntryAsync(int externalPort, string protocol, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SimpleUPnPDevice));

        try
        {
            if (string.IsNullOrEmpty(ControlUrl))
            {
                return null;
            }

            var soapAction = "GetSpecificPortMappingEntry";
            var serviceType = GetServiceType();

            var soapBody = $@"
                <u:{soapAction} xmlns:u=""{serviceType}"">
                    <NewRemoteHost></NewRemoteHost>
                    <NewExternalPort>{externalPort}</NewExternalPort>
                    <NewProtocol>{protocol}</NewProtocol>
                </u:{soapAction}>";

            var response = await SendSoapRequestAsync(soapAction, serviceType, soapBody, cancellationToken);

            if (string.IsNullOrEmpty(response) || response.Contains("soap:Fault"))
            {
                return null;
            }

            // Parse XML response
            var xDoc = XDocument.Parse(response);

            var mapping = new UPnPPortMapping
            {
                ExternalPort = externalPort,
                InternalPort = ParseIntElement(xDoc, "NewInternalPort"),
                InternalClient = ParseStringElement(xDoc, "NewInternalClient") ?? string.Empty,
                Protocol = protocol,
                Description = ParseStringElement(xDoc, "NewPortMappingDescription") ?? string.Empty,
                Enabled = ParseIntElement(xDoc, "NewEnabled") == 1,
                LeaseDuration = ParseIntElement(xDoc, "NewLeaseDuration")
            };

            _logger.LogDebug("Found existing mapping for port {ExternalPort}:{Protocol} on device {DeviceId}",
                externalPort, protocol, DeviceId);
            return mapping;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error checking specific port mapping {ExternalPort}:{Protocol} on device {DeviceId}",
                externalPort, protocol, DeviceId);
            return null;
        }
    }

    private static int ParseIntElement(XDocument doc, string elementName)
    {
        var element = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == elementName);
        if (element != null && int.TryParse(element.Value, out var value))
        {
            return value;
        }
        return 0;
    }

    private static string? ParseStringElement(XDocument doc, string elementName)
    {
        var element = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == elementName);
        return element?.Value;
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

            // Find IPv6 firewall control URL if available
            var ipv6ControlUrlElement = FindControlUrl(xDoc, WanIpv6FirewallControl);
            if (ipv6ControlUrlElement != null)
            {
                Ipv6ControlUrl = ResolveControlUrl(ipv6ControlUrlElement.Value, locationUrl);
                SupportsIPv6 = true;
                _logger.LogDebug("Device {DeviceId} supports IPv6 firewall control at {Ipv6ControlUrl}", DeviceId, Ipv6ControlUrl);
            }
            else
            {
                // Check IPv6 support based on device type (IGDv2 can potentially support it)
                SupportsIPv6 = DeviceType.Contains(":2");
            }

            _logger.LogDebug("Device {DeviceId} initialized: {FriendlyName}, ControlUrl: {ControlUrl}, SupportsIPv6: {SupportsIPv6}",
                DeviceId, FriendlyName, ControlUrl, SupportsIPv6);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize device {DeviceId} from {LocationUrl}", DeviceId, locationUrl);
        }
    }

    private XElement? FindControlUrl(XDocument deviceDoc, string? specificServiceType = null)
    {
        // Look for WANIPConnection, WANPPPConnection, or specific services
        var services = deviceDoc.Descendants().Where(e => e.Name.LocalName == "service");

        foreach (var service in services)
        {
            var serviceTypeElement = service.Descendants().FirstOrDefault(e => e.Name.LocalName == "serviceType");
            if (serviceTypeElement != null)
            {
                var serviceType = serviceTypeElement.Value;

                // If looking for a specific service type
                if (specificServiceType != null)
                {
                    if (serviceType == specificServiceType)
                    {
                        return service.Descendants().FirstOrDefault(e => e.Name.LocalName == "controlURL");
                    }
                }
                // Default: look for WAN connection services
                else if (serviceType == WanIpConnectionV1 || serviceType == WanIpConnectionV2 ||
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
        return await SendSoapRequestWithUrlAsync(ControlUrl, action, serviceType, soapBody, cancellationToken);
    }

    private async Task<string> SendSoapRequestWithUrlAsync(string controlUrl, string action, string serviceType, string soapBody, CancellationToken cancellationToken)
    {
        var soapEnvelope = $@"<?xml version=""1.0""?>
<soap:Envelope xmlns:soap=""http://schemas.xmlsoap.org/soap/envelope/"" soap:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
    <soap:Body>
        {soapBody}
    </soap:Body>
</soap:Envelope>";

        var content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml");
        content.Headers.Add("SOAPAction", $"\"{serviceType}#{action}\"");

        var response = await _httpClient.PostAsync(controlUrl, content, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>
    /// Parse SOAP error code from fault response
    /// </summary>
    private static int? ParseSoapErrorCode(string? response)
    {
        if (string.IsNullOrEmpty(response) || !response.Contains("soap:Fault"))
            return null;

        try
        {
            var xDoc = XDocument.Parse(response);

            // Look for UPnP error code in various formats
            var errorCodeElement = xDoc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "errorCode" ||
                                    e.Name.LocalName == "ErrorCode");

            if (errorCodeElement != null && int.TryParse(errorCodeElement.Value, out var errorCode))
            {
                return errorCode;
            }

            // Try to parse from faultcode or faultstring
            var faultCodeElement = xDoc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "faultcode");

            if (faultCodeElement != null)
            {
                // Try to extract numeric error code from format like "s:Client" or "718"
                var faultCode = faultCodeElement.Value;
                if (int.TryParse(faultCode, out var numericCode))
                {
                    return numericCode;
                }
            }
        }
        catch
        {
            // Parsing failed, return null
        }

        return null;
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