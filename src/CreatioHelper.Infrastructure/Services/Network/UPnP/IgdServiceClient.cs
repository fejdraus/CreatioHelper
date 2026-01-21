using System.Net;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Network.UPnP;

/// <summary>
/// IGD (Internet Gateway Device) service client for advanced UPnP operations.
/// Supports both IGDv1 and IGDv2, including IPv6 pinhole management.
/// </summary>
public class IgdServiceClient : IDisposable
{
    private readonly ILogger<IgdServiceClient> _logger;
    private readonly HttpClient _httpClient;
    private bool _disposed;

    // UPnP service URNs
    private const string WanIpConnectionV1 = "urn:schemas-upnp-org:service:WANIPConnection:1";
    private const string WanIpConnectionV2 = "urn:schemas-upnp-org:service:WANIPConnection:2";
    private const string WanIpv6FirewallControl = "urn:schemas-upnp-org:service:WANIPv6FirewallControl:1";

    // UPnP error codes
    public const int ErrorOnlyPermanentLeasesSupported = 725;
    public const int ErrorConflictInMappingEntry = 718;
    public const int ErrorNoPortMapsAvailable = 728;
    public const int ErrorSamePortValuesRequired = 724;
    public const int ErrorWildCardNotPermittedInSrcIp = 715;
    public const int ErrorWildCardNotPermittedInExtPort = 716;
    public const int ErrorNoSuchEntryInArray = 714;

    public IgdServiceClient(ILogger<IgdServiceClient> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "CreatioHelper/1.0 UPnP/1.1");
    }

    /// <summary>
    /// Adds a port mapping using IGDv2 AddAnyPortMapping action.
    /// This allows the gateway to assign any available external port.
    /// </summary>
    /// <param name="controlUrl">The UPnP control URL</param>
    /// <param name="internalClient">Internal IP address</param>
    /// <param name="internalPort">Internal port</param>
    /// <param name="protocol">Protocol (TCP or UDP)</param>
    /// <param name="description">Description for the mapping</param>
    /// <param name="leaseDuration">Lease duration in seconds (0 for indefinite)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The assigned external port, or null if failed</returns>
    public async Task<AddAnyPortMappingResult?> AddAnyPortMappingAsync(
        string controlUrl,
        string internalClient,
        int internalPort,
        string protocol,
        string description,
        int leaseDuration = 0,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("AddAnyPortMapping: {InternalClient}:{InternalPort} {Protocol}",
                internalClient, internalPort, protocol);

            var soapBody = $@"
                <u:AddAnyPortMapping xmlns:u=""{WanIpConnectionV2}"">
                    <NewRemoteHost></NewRemoteHost>
                    <NewExternalPort>{internalPort}</NewExternalPort>
                    <NewProtocol>{protocol}</NewProtocol>
                    <NewInternalPort>{internalPort}</NewInternalPort>
                    <NewInternalClient>{internalClient}</NewInternalClient>
                    <NewEnabled>1</NewEnabled>
                    <NewPortMappingDescription>{description}</NewPortMappingDescription>
                    <NewLeaseDuration>{leaseDuration}</NewLeaseDuration>
                </u:AddAnyPortMapping>";

            var response = await SendSoapRequestAsync(controlUrl, "AddAnyPortMapping", WanIpConnectionV2, soapBody, cancellationToken);

            if (response.Success && response.ResponseDocument != null)
            {
                var reservedPort = ParseIntElement(response.ResponseDocument, "NewReservedPort");
                if (reservedPort > 0)
                {
                    _logger.LogInformation("AddAnyPortMapping succeeded: assigned external port {ExternalPort}", reservedPort);
                    return new AddAnyPortMappingResult
                    {
                        ExternalPort = reservedPort,
                        InternalPort = internalPort,
                        Protocol = protocol,
                        Description = description,
                        LeaseDuration = leaseDuration
                    };
                }
            }

            // Handle specific error codes
            if (response.ErrorCode == ErrorOnlyPermanentLeasesSupported)
            {
                _logger.LogDebug("Gateway only supports permanent leases, retrying with duration=0");
                if (leaseDuration != 0)
                {
                    return await AddAnyPortMappingAsync(controlUrl, internalClient, internalPort, protocol, description, 0, cancellationToken);
                }
            }
            else if (response.ErrorCode == ErrorConflictInMappingEntry)
            {
                _logger.LogWarning("Port mapping conflict for port {Port}", internalPort);
            }

            _logger.LogWarning("AddAnyPortMapping failed: {ErrorCode} - {ErrorDescription}",
                response.ErrorCode, response.ErrorDescription);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddAnyPortMapping exception");
            return null;
        }
    }

    /// <summary>
    /// Deletes a range of port mappings (IGDv2 feature).
    /// </summary>
    public async Task<bool> DeletePortMappingRangeAsync(
        string controlUrl,
        int startPort,
        int endPort,
        string protocol,
        bool manage = true,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("DeletePortMappingRange: {StartPort}-{EndPort} {Protocol}", startPort, endPort, protocol);

            var soapBody = $@"
                <u:DeletePortMappingRange xmlns:u=""{WanIpConnectionV2}"">
                    <NewStartPort>{startPort}</NewStartPort>
                    <NewEndPort>{endPort}</NewEndPort>
                    <NewProtocol>{protocol}</NewProtocol>
                    <NewManage>{(manage ? 1 : 0)}</NewManage>
                </u:DeletePortMappingRange>";

            var response = await SendSoapRequestAsync(controlUrl, "DeletePortMappingRange", WanIpConnectionV2, soapBody, cancellationToken);
            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeletePortMappingRange exception");
            return false;
        }
    }

    /// <summary>
    /// Gets port mapping entries within a range (IGDv2 feature).
    /// </summary>
    public async Task<List<PortMappingEntry>> GetListOfPortMappingsAsync(
        string controlUrl,
        int startPort,
        int endPort,
        string protocol,
        int numberOfPorts,
        CancellationToken cancellationToken = default)
    {
        var mappings = new List<PortMappingEntry>();

        try
        {
            _logger.LogDebug("GetListOfPortMappings: {StartPort}-{EndPort} {Protocol}", startPort, endPort, protocol);

            var soapBody = $@"
                <u:GetListOfPortMappings xmlns:u=""{WanIpConnectionV2}"">
                    <NewStartPort>{startPort}</NewStartPort>
                    <NewEndPort>{endPort}</NewEndPort>
                    <NewProtocol>{protocol}</NewProtocol>
                    <NewManage>1</NewManage>
                    <NewNumberOfPorts>{numberOfPorts}</NewNumberOfPorts>
                </u:GetListOfPortMappings>";

            var response = await SendSoapRequestAsync(controlUrl, "GetListOfPortMappings", WanIpConnectionV2, soapBody, cancellationToken);

            if (response.Success && response.ResponseDocument != null)
            {
                var portListElement = response.ResponseDocument.Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "NewPortListing");

                if (portListElement != null && !string.IsNullOrEmpty(portListElement.Value))
                {
                    mappings = ParsePortMappingList(portListElement.Value);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetListOfPortMappings exception");
        }

        return mappings;
    }

    #region IPv6 Pinhole Management (WANIPv6FirewallControl)

    /// <summary>
    /// Adds an IPv6 firewall pinhole for inbound connections.
    /// </summary>
    /// <param name="controlUrl">The WANIPv6FirewallControl URL</param>
    /// <param name="remoteHost">Remote IPv6 address (empty for any)</param>
    /// <param name="remotePort">Remote port (0 for any)</param>
    /// <param name="internalClient">Internal IPv6 address</param>
    /// <param name="internalPort">Internal port</param>
    /// <param name="protocol">Protocol number (6=TCP, 17=UDP)</param>
    /// <param name="leaseTime">Lease time in seconds (0 for indefinite)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The unique pinhole ID, or null if failed</returns>
    public async Task<int?> AddPinholeAsync(
        string controlUrl,
        string remoteHost,
        int remotePort,
        string internalClient,
        int internalPort,
        int protocol,
        int leaseTime = 3600,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("AddPinhole: {RemoteHost}:{RemotePort} -> {InternalClient}:{InternalPort} proto={Protocol}",
                string.IsNullOrEmpty(remoteHost) ? "*" : remoteHost, remotePort, internalClient, internalPort, protocol);

            var soapBody = $@"
                <u:AddPinhole xmlns:u=""{WanIpv6FirewallControl}"">
                    <RemoteHost>{remoteHost}</RemoteHost>
                    <RemotePort>{remotePort}</RemotePort>
                    <InternalClient>{internalClient}</InternalClient>
                    <InternalPort>{internalPort}</InternalPort>
                    <Protocol>{protocol}</Protocol>
                    <LeaseTime>{leaseTime}</LeaseTime>
                </u:AddPinhole>";

            var response = await SendSoapRequestAsync(controlUrl, "AddPinhole", WanIpv6FirewallControl, soapBody, cancellationToken);

            if (response.Success && response.ResponseDocument != null)
            {
                var uniqueId = ParseIntElement(response.ResponseDocument, "UniqueID");
                if (uniqueId > 0)
                {
                    _logger.LogInformation("AddPinhole succeeded: UniqueID={UniqueId}", uniqueId);
                    return uniqueId;
                }
            }

            _logger.LogWarning("AddPinhole failed: {ErrorCode} - {ErrorDescription}",
                response.ErrorCode, response.ErrorDescription);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AddPinhole exception");
            return null;
        }
    }

    /// <summary>
    /// Updates an existing IPv6 pinhole's lease time.
    /// </summary>
    public async Task<bool> UpdatePinholeAsync(
        string controlUrl,
        int uniqueId,
        int newLeaseTime,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("UpdatePinhole: UniqueID={UniqueId} LeaseTime={LeaseTime}", uniqueId, newLeaseTime);

            var soapBody = $@"
                <u:UpdatePinhole xmlns:u=""{WanIpv6FirewallControl}"">
                    <UniqueID>{uniqueId}</UniqueID>
                    <NewLeaseTime>{newLeaseTime}</NewLeaseTime>
                </u:UpdatePinhole>";

            var response = await SendSoapRequestAsync(controlUrl, "UpdatePinhole", WanIpv6FirewallControl, soapBody, cancellationToken);
            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "UpdatePinhole exception");
            return false;
        }
    }

    /// <summary>
    /// Deletes an IPv6 pinhole.
    /// </summary>
    public async Task<bool> DeletePinholeAsync(
        string controlUrl,
        int uniqueId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("DeletePinhole: UniqueID={UniqueId}", uniqueId);

            var soapBody = $@"
                <u:DeletePinhole xmlns:u=""{WanIpv6FirewallControl}"">
                    <UniqueID>{uniqueId}</UniqueID>
                </u:DeletePinhole>";

            var response = await SendSoapRequestAsync(controlUrl, "DeletePinhole", WanIpv6FirewallControl, soapBody, cancellationToken);
            return response.Success;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DeletePinhole exception");
            return false;
        }
    }

    /// <summary>
    /// Gets packet count through a pinhole for determining activity.
    /// </summary>
    public async Task<long?> GetPinholePacketsAsync(
        string controlUrl,
        int uniqueId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var soapBody = $@"
                <u:GetPinholePackets xmlns:u=""{WanIpv6FirewallControl}"">
                    <UniqueID>{uniqueId}</UniqueID>
                </u:GetPinholePackets>";

            var response = await SendSoapRequestAsync(controlUrl, "GetPinholePackets", WanIpv6FirewallControl, soapBody, cancellationToken);

            if (response.Success && response.ResponseDocument != null)
            {
                var packets = ParseLongElement(response.ResponseDocument, "PinholePackets");
                return packets;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetPinholePackets exception");
            return null;
        }
    }

    /// <summary>
    /// Checks if outbound pinhole timeout is available.
    /// </summary>
    public async Task<int?> GetOutboundPinholeTimeoutAsync(
        string controlUrl,
        string remoteHost,
        int remotePort,
        string internalClient,
        int internalPort,
        int protocol,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var soapBody = $@"
                <u:GetOutboundPinholeTimeout xmlns:u=""{WanIpv6FirewallControl}"">
                    <RemoteHost>{remoteHost}</RemoteHost>
                    <RemotePort>{remotePort}</RemotePort>
                    <InternalClient>{internalClient}</InternalClient>
                    <InternalPort>{internalPort}</InternalPort>
                    <Protocol>{protocol}</Protocol>
                </u:GetOutboundPinholeTimeout>";

            var response = await SendSoapRequestAsync(controlUrl, "GetOutboundPinholeTimeout", WanIpv6FirewallControl, soapBody, cancellationToken);

            if (response.Success && response.ResponseDocument != null)
            {
                return ParseIntElement(response.ResponseDocument, "OutboundPinholeTimeout");
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetOutboundPinholeTimeout exception");
            return null;
        }
    }

    /// <summary>
    /// Checks if the firewall is enabled.
    /// </summary>
    public async Task<bool?> GetFirewallStatusAsync(
        string controlUrl,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var soapBody = $@"<u:GetFirewallStatus xmlns:u=""{WanIpv6FirewallControl}""></u:GetFirewallStatus>";

            var response = await SendSoapRequestAsync(controlUrl, "GetFirewallStatus", WanIpv6FirewallControl, soapBody, cancellationToken);

            if (response.Success && response.ResponseDocument != null)
            {
                var enabled = ParseIntElement(response.ResponseDocument, "FirewallEnabled");
                return enabled == 1;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetFirewallStatus exception");
            return null;
        }
    }

    #endregion

    #region Helper Methods

    private async Task<SoapResponse> SendSoapRequestAsync(
        string controlUrl,
        string action,
        string serviceType,
        string soapBody,
        CancellationToken cancellationToken)
    {
        var response = new SoapResponse();

        try
        {
            var soapEnvelope = $@"<?xml version=""1.0""?>
<s:Envelope xmlns:s=""http://schemas.xmlsoap.org/soap/envelope/"" s:encodingStyle=""http://schemas.xmlsoap.org/soap/encoding/"">
    <s:Body>
        {soapBody}
    </s:Body>
</s:Envelope>";

            var content = new StringContent(soapEnvelope, Encoding.UTF8, "text/xml");
            content.Headers.Add("SOAPAction", $"\"{serviceType}#{action}\"");

            var httpResponse = await _httpClient.PostAsync(controlUrl, content, cancellationToken);
            var responseContent = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

            if (!string.IsNullOrEmpty(responseContent))
            {
                response.ResponseDocument = XDocument.Parse(responseContent);

                if (responseContent.Contains("s:Fault") || responseContent.Contains("soap:Fault"))
                {
                    response.Success = false;
                    response.ErrorCode = ParseIntElement(response.ResponseDocument, "errorCode");
                    response.ErrorDescription = ParseStringElement(response.ResponseDocument, "errorDescription") ?? "Unknown error";
                }
                else
                {
                    response.Success = httpResponse.IsSuccessStatusCode;
                }
            }
        }
        catch (Exception ex)
        {
            response.Success = false;
            response.ErrorDescription = ex.Message;
            _logger.LogDebug(ex, "SOAP request failed for {Action}", action);
        }

        return response;
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

    private static long ParseLongElement(XDocument doc, string elementName)
    {
        var element = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == elementName);
        if (element != null && long.TryParse(element.Value, out var value))
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

    private List<PortMappingEntry> ParsePortMappingList(string xmlList)
    {
        var mappings = new List<PortMappingEntry>();

        try
        {
            var doc = XDocument.Parse($"<root>{xmlList}</root>");
            var entries = doc.Descendants().Where(e => e.Name.LocalName == "PortMappingEntry");

            foreach (var entry in entries)
            {
                var mapping = new PortMappingEntry
                {
                    RemoteHost = entry.Element("RemoteHost")?.Value ?? string.Empty,
                    ExternalPort = int.TryParse(entry.Element("ExternalPort")?.Value, out var ext) ? ext : 0,
                    Protocol = entry.Element("Protocol")?.Value ?? "TCP",
                    InternalPort = int.TryParse(entry.Element("InternalPort")?.Value, out var intPort) ? intPort : 0,
                    InternalClient = entry.Element("InternalClient")?.Value ?? string.Empty,
                    Enabled = entry.Element("Enabled")?.Value == "1",
                    Description = entry.Element("Description")?.Value ?? string.Empty,
                    LeaseDuration = int.TryParse(entry.Element("LeaseDuration")?.Value, out var lease) ? lease : 0
                };
                mappings.Add(mapping);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse port mapping list");
        }

        return mappings;
    }

    #endregion

    public void Dispose()
    {
        if (!_disposed)
        {
            _httpClient.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Result of AddAnyPortMapping operation
/// </summary>
public class AddAnyPortMappingResult
{
    public int ExternalPort { get; set; }
    public int InternalPort { get; set; }
    public string Protocol { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int LeaseDuration { get; set; }
}

/// <summary>
/// Port mapping entry from GetListOfPortMappings
/// </summary>
public class PortMappingEntry
{
    public string RemoteHost { get; set; } = string.Empty;
    public int ExternalPort { get; set; }
    public string Protocol { get; set; } = string.Empty;
    public int InternalPort { get; set; }
    public string InternalClient { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public string Description { get; set; } = string.Empty;
    public int LeaseDuration { get; set; }
}

/// <summary>
/// IPv6 pinhole entry
/// </summary>
public class Ipv6PinholeEntry
{
    public int UniqueId { get; set; }
    public string RemoteHost { get; set; } = string.Empty;
    public int RemotePort { get; set; }
    public string InternalClient { get; set; } = string.Empty;
    public int InternalPort { get; set; }
    public int Protocol { get; set; }
    public int LeaseTime { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
}

/// <summary>
/// Internal SOAP response wrapper
/// </summary>
internal class SoapResponse
{
    public bool Success { get; set; }
    public XDocument? ResponseDocument { get; set; }
    public int ErrorCode { get; set; }
    public string ErrorDescription { get; set; } = string.Empty;
}
