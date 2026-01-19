using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Network.Stun;

/// <summary>
/// STUN (Session Traversal Utilities for NAT) client implementation.
/// Based on RFC 5389/8489 for NAT type detection and external IP discovery.
/// Compatible with Syncthing's STUN implementation.
/// </summary>
public class StunClient : IDisposable
{
    private readonly ILogger<StunClient> _logger;
    private bool _disposed;

    // STUN Constants (RFC 5389/8489)
    private const uint MagicCookie = 0x2112A442;
    private const int HeaderSize = 20;

    // Message Types
    private const ushort BindingRequest = 0x0001;
    private const ushort BindingResponse = 0x0101;
    private const ushort BindingErrorResponse = 0x0111;

    // Attribute Types
    private const ushort AttrMappedAddress = 0x0001;
    private const ushort AttrChangeRequest = 0x0003;  // RFC 5780
    private const ushort AttrSourceAddress = 0x0004;
    private const ushort AttrChangedAddress = 0x0005;
    private const ushort AttrErrorCode = 0x0009;
    private const ushort AttrOtherAddress = 0x802C;   // RFC 5780
    private const ushort AttrXorMappedAddress = 0x0020;
    private const ushort AttrResponseOrigin = 0x802B; // RFC 5780
    private const ushort AttrSoftware = 0x8022;
    private const ushort AttrFingerprint = 0x8028;

    // Change-Request flags (RFC 5780)
    private const uint ChangeIpFlag = 0x04;
    private const uint ChangePortFlag = 0x02;

    // Address Families
    private const byte AddressFamilyIPv4 = 0x01;
    private const byte AddressFamilyIPv6 = 0x02;

    public StunClient(ILogger<StunClient> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Performs a STUN binding request to discover the external IP address and port.
    /// </summary>
    /// <param name="stunServer">STUN server hostname or IP.</param>
    /// <param name="port">STUN server port (default 3478).</param>
    /// <param name="timeout">Request timeout.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The discovered external endpoint, or null if discovery failed.</returns>
    public async Task<StunResult?> BindingRequestAsync(
        string stunServer,
        int port = 3478,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StunClient));

        timeout ??= TimeSpan.FromSeconds(5);

        try
        {
            _logger.LogDebug("STUN binding request to {Server}:{Port}", stunServer, port);

            // Resolve server address
            var addresses = await Dns.GetHostAddressesAsync(stunServer, cancellationToken);
            var serverAddress = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

            if (serverAddress == null)
            {
                _logger.LogWarning("Could not resolve STUN server {Server}", stunServer);
                return null;
            }

            var serverEndpoint = new IPEndPoint(serverAddress, port);

            using var udpClient = new UdpClient();
            udpClient.Client.ReceiveTimeout = (int)timeout.Value.TotalMilliseconds;

            // Create and send binding request
            var transactionId = GenerateTransactionId();
            var request = CreateBindingRequest(transactionId);

            await udpClient.SendAsync(request, serverEndpoint, cancellationToken);

            // Wait for response
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout.Value);

            var result = await udpClient.ReceiveAsync(cts.Token);

            // Parse response
            var stunResult = ParseBindingResponse(result.Buffer, transactionId);

            if (stunResult != null)
            {
                stunResult.LocalEndPoint = (IPEndPoint?)udpClient.Client.LocalEndPoint;
                stunResult.ServerEndPoint = serverEndpoint;

                _logger.LogDebug("STUN discovered external address: {ExternalEndPoint}", stunResult.MappedEndPoint);
            }

            return stunResult;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("STUN request to {Server}:{Port} timed out", stunServer, port);
            return null;
        }
        catch (SocketException ex)
        {
            _logger.LogDebug(ex, "STUN socket error for {Server}:{Port}", stunServer, port);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "STUN request to {Server}:{Port} failed", stunServer, port);
            return null;
        }
    }

    /// <summary>
    /// Performs STUN requests to multiple servers to detect NAT type.
    /// Uses RFC 5780 CHANGE-REQUEST when supported for accurate detection.
    /// </summary>
    public async Task<NatTypeResult> DetectNatTypeAsync(
        string[] stunServers,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StunClient));

        timeout ??= TimeSpan.FromSeconds(5);

        var results = new List<StunResult>();
        var testResults = new NatTestResults();

        // Use the first available server for detailed tests
        foreach (var server in stunServers.Take(3))
        {
            var parts = server.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 ? int.Parse(parts[1]) : 3478;

            // Test 1: Basic binding request
            var result = await BindingRequestAsync(host, port, timeout, cancellationToken);
            if (result != null)
            {
                results.Add(result);
                testResults.BasicResult = result;

                // Test 2: Request with change-ip and change-port (full change)
                // This tests if we can receive from a different server IP and port
                var changeResult = await BindingRequestWithChangeAsync(host, port, changeIp: true, changePort: true, timeout, cancellationToken);
                testResults.FullChangeResult = changeResult;

                // Test 3: Request with change-port only
                // This tests filtering behavior
                var changePortResult = await BindingRequestWithChangeAsync(host, port, changeIp: false, changePort: true, timeout, cancellationToken);
                testResults.ChangePortResult = changePortResult;

                // If server has alternate address, test to it
                if (result.OtherAddress != null)
                {
                    var altResult = await BindingRequestAsync(result.OtherAddress.Address.ToString(), result.OtherAddress.Port, timeout, cancellationToken);
                    if (altResult != null)
                    {
                        testResults.AlternateServerResult = altResult;
                    }
                }

                break; // Found a working server
            }

            if (cancellationToken.IsCancellationRequested)
                break;
        }

        // Also do basic tests with multiple servers to detect symmetric NAT
        foreach (var server in stunServers.Skip(1).Take(2))
        {
            var parts = server.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 ? int.Parse(parts[1]) : 3478;

            var result = await BindingRequestAsync(host, port, timeout, cancellationToken);
            if (result != null)
            {
                results.Add(result);
            }

            if (cancellationToken.IsCancellationRequested)
                break;
        }

        return AnalyzeNatTypeAdvanced(testResults, results);
    }

    /// <summary>
    /// Performs basic STUN requests to multiple servers (simpler, less accurate).
    /// </summary>
    public async Task<NatTypeResult> DetectNatTypeBasicAsync(
        string[] stunServers,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StunClient));

        timeout ??= TimeSpan.FromSeconds(5);

        var results = new List<StunResult>();

        foreach (var server in stunServers.Take(3))
        {
            var parts = server.Split(':');
            var host = parts[0];
            var port = parts.Length > 1 ? int.Parse(parts[1]) : 3478;

            var result = await BindingRequestAsync(host, port, timeout, cancellationToken);
            if (result != null)
            {
                results.Add(result);
            }

            if (cancellationToken.IsCancellationRequested)
                break;
        }

        return AnalyzeNatType(results);
    }

    private byte[] CreateBindingRequest(byte[] transactionId)
    {
        var message = new byte[HeaderSize];

        // Message Type: Binding Request (0x0001)
        message[0] = (byte)(BindingRequest >> 8);
        message[1] = (byte)(BindingRequest & 0xFF);

        // Message Length: 0 (no attributes in basic request)
        message[2] = 0;
        message[3] = 0;

        // Magic Cookie: 0x2112A442
        message[4] = unchecked((byte)(MagicCookie >> 24));
        message[5] = unchecked((byte)(MagicCookie >> 16));
        message[6] = unchecked((byte)(MagicCookie >> 8));
        message[7] = unchecked((byte)(MagicCookie & 0xFF));

        // Transaction ID (12 bytes)
        Array.Copy(transactionId, 0, message, 8, 12);

        return message;
    }

    private StunResult? ParseBindingResponse(byte[] response, byte[] expectedTransactionId)
    {
        if (response.Length < HeaderSize)
        {
            _logger.LogDebug("STUN response too short: {Length} bytes", response.Length);
            return null;
        }

        // Parse header
        var messageType = (ushort)((response[0] << 8) | response[1]);
        var messageLength = (ushort)((response[2] << 8) | response[3]);
        var magicCookie = (uint)((response[4] << 24) | (response[5] << 16) | (response[6] << 8) | response[7]);

        // Validate magic cookie
        if (magicCookie != MagicCookie)
        {
            _logger.LogDebug("STUN response has invalid magic cookie: 0x{Cookie:X8}", magicCookie);
            return null;
        }

        // Validate transaction ID
        for (int i = 0; i < 12; i++)
        {
            if (response[8 + i] != expectedTransactionId[i])
            {
                _logger.LogDebug("STUN response has mismatched transaction ID");
                return null;
            }
        }

        // Check message type
        if (messageType == BindingErrorResponse)
        {
            _logger.LogDebug("STUN binding error response received");
            return null;
        }

        if (messageType != BindingResponse)
        {
            _logger.LogDebug("STUN unexpected message type: 0x{Type:X4}", messageType);
            return null;
        }

        // Parse attributes
        var result = new StunResult();
        var offset = HeaderSize;
        var endOffset = HeaderSize + messageLength;

        while (offset + 4 <= endOffset && offset + 4 <= response.Length)
        {
            var attrType = (ushort)((response[offset] << 8) | response[offset + 1]);
            var attrLength = (ushort)((response[offset + 2] << 8) | response[offset + 3]);
            offset += 4;

            if (offset + attrLength > response.Length)
                break;

            switch (attrType)
            {
                case AttrXorMappedAddress:
                    result.MappedEndPoint = ParseXorMappedAddress(response, offset, attrLength, expectedTransactionId);
                    break;

                case AttrMappedAddress:
                    // Fallback to MAPPED-ADDRESS if XOR-MAPPED-ADDRESS not present
                    if (result.MappedEndPoint == null)
                    {
                        result.MappedEndPoint = ParseMappedAddress(response, offset, attrLength);
                    }
                    break;

                case AttrSoftware:
                    result.ServerSoftware = System.Text.Encoding.UTF8.GetString(response, offset, attrLength).TrimEnd('\0');
                    break;

                case AttrOtherAddress:
                case AttrChangedAddress:
                    result.OtherAddress = ParseMappedAddress(response, offset, attrLength);
                    break;

                case AttrResponseOrigin:
                case AttrSourceAddress:
                    result.ResponseOrigin = ParseMappedAddress(response, offset, attrLength);
                    break;
            }

            // Attributes are padded to 4-byte boundaries
            offset += attrLength;
            offset = (offset + 3) & ~3;
        }

        return result.MappedEndPoint != null ? result : null;
    }

    /// <summary>
    /// Performs a STUN binding request with CHANGE-REQUEST attribute (RFC 5780).
    /// </summary>
    public async Task<StunResult?> BindingRequestWithChangeAsync(
        string stunServer,
        int port = 3478,
        bool changeIp = false,
        bool changePort = false,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StunClient));

        timeout ??= TimeSpan.FromSeconds(5);

        try
        {
            _logger.LogDebug("STUN binding request with change (IP={ChangeIp}, Port={ChangePort}) to {Server}:{Port}",
                changeIp, changePort, stunServer, port);

            var addresses = await Dns.GetHostAddressesAsync(stunServer, cancellationToken);
            var serverAddress = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

            if (serverAddress == null)
            {
                _logger.LogWarning("Could not resolve STUN server {Server}", stunServer);
                return null;
            }

            var serverEndpoint = new IPEndPoint(serverAddress, port);

            using var udpClient = new UdpClient();
            udpClient.Client.ReceiveTimeout = (int)timeout.Value.TotalMilliseconds;

            var transactionId = GenerateTransactionId();
            var request = CreateBindingRequestWithChange(transactionId, changeIp, changePort);

            await udpClient.SendAsync(request, serverEndpoint, cancellationToken);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(timeout.Value);

            var result = await udpClient.ReceiveAsync(cts.Token);
            var stunResult = ParseBindingResponse(result.Buffer, transactionId);

            if (stunResult != null)
            {
                stunResult.LocalEndPoint = (IPEndPoint?)udpClient.Client.LocalEndPoint;
                stunResult.ServerEndPoint = serverEndpoint;
                stunResult.ChangedIp = changeIp;
                stunResult.ChangedPort = changePort;
            }

            return stunResult;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("STUN change request to {Server}:{Port} timed out", stunServer, port);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "STUN change request to {Server}:{Port} failed", stunServer, port);
            return null;
        }
    }

    private byte[] CreateBindingRequestWithChange(byte[] transactionId, bool changeIp, bool changePort)
    {
        // Calculate change flags
        uint changeFlags = 0;
        if (changeIp) changeFlags |= ChangeIpFlag;
        if (changePort) changeFlags |= ChangePortFlag;

        // If no change requested, return basic request
        if (changeFlags == 0)
        {
            return CreateBindingRequest(transactionId);
        }

        // Message with CHANGE-REQUEST attribute (4 bytes)
        var message = new byte[HeaderSize + 8]; // Header + attribute header (4) + value (4)

        // Message Type: Binding Request (0x0001)
        message[0] = (byte)(BindingRequest >> 8);
        message[1] = (byte)(BindingRequest & 0xFF);

        // Message Length: 8 (CHANGE-REQUEST attribute)
        message[2] = 0;
        message[3] = 8;

        // Magic Cookie: 0x2112A442
        message[4] = unchecked((byte)(MagicCookie >> 24));
        message[5] = unchecked((byte)(MagicCookie >> 16));
        message[6] = unchecked((byte)(MagicCookie >> 8));
        message[7] = unchecked((byte)(MagicCookie & 0xFF));

        // Transaction ID (12 bytes)
        Array.Copy(transactionId, 0, message, 8, 12);

        // CHANGE-REQUEST attribute
        // Attribute Type (2 bytes)
        message[20] = (byte)(AttrChangeRequest >> 8);
        message[21] = (byte)(AttrChangeRequest & 0xFF);
        // Attribute Length (2 bytes) - 4 bytes for the value
        message[22] = 0;
        message[23] = 4;
        // Change flags (4 bytes)
        message[24] = (byte)(changeFlags >> 24);
        message[25] = (byte)(changeFlags >> 16);
        message[26] = (byte)(changeFlags >> 8);
        message[27] = (byte)(changeFlags & 0xFF);

        return message;
    }

    private IPEndPoint? ParseXorMappedAddress(byte[] response, int offset, int length, byte[] transactionId)
    {
        if (length < 8)
            return null;

        // Skip first byte (reserved)
        var family = response[offset + 1];
        var xorPort = (ushort)((response[offset + 2] << 8) | response[offset + 3]);

        // XOR port with magic cookie high bits
        var port = (ushort)(xorPort ^ (MagicCookie >> 16));

        if (family == AddressFamilyIPv4)
        {
            if (length < 8)
                return null;

            // XOR with magic cookie
            var xorAddress = new byte[4];
            xorAddress[0] = (byte)(response[offset + 4] ^ (MagicCookie >> 24));
            xorAddress[1] = (byte)(response[offset + 5] ^ (MagicCookie >> 16));
            xorAddress[2] = (byte)(response[offset + 6] ^ (MagicCookie >> 8));
            xorAddress[3] = (byte)(response[offset + 7] ^ (MagicCookie & 0xFF));

            return new IPEndPoint(new IPAddress(xorAddress), port);
        }
        else if (family == AddressFamilyIPv6)
        {
            if (length < 20)
                return null;

            // XOR with magic cookie + transaction ID
            var xorAddress = new byte[16];
            var xorKey = new byte[16];
            xorKey[0] = unchecked((byte)(MagicCookie >> 24));
            xorKey[1] = unchecked((byte)(MagicCookie >> 16));
            xorKey[2] = unchecked((byte)(MagicCookie >> 8));
            xorKey[3] = unchecked((byte)(MagicCookie & 0xFF));
            Array.Copy(transactionId, 0, xorKey, 4, 12);

            for (int i = 0; i < 16; i++)
            {
                xorAddress[i] = (byte)(response[offset + 4 + i] ^ xorKey[i]);
            }

            return new IPEndPoint(new IPAddress(xorAddress), port);
        }

        return null;
    }

    private IPEndPoint? ParseMappedAddress(byte[] response, int offset, int length)
    {
        if (length < 8)
            return null;

        // Skip first byte (reserved)
        var family = response[offset + 1];
        var port = (ushort)((response[offset + 2] << 8) | response[offset + 3]);

        if (family == AddressFamilyIPv4)
        {
            if (length < 8)
                return null;

            var address = new byte[4];
            Array.Copy(response, offset + 4, address, 0, 4);
            return new IPEndPoint(new IPAddress(address), port);
        }
        else if (family == AddressFamilyIPv6)
        {
            if (length < 20)
                return null;

            var address = new byte[16];
            Array.Copy(response, offset + 4, address, 0, 16);
            return new IPEndPoint(new IPAddress(address), port);
        }

        return null;
    }

    private NatTypeResult AnalyzeNatType(List<StunResult> results)
    {
        if (results.Count == 0)
        {
            return new NatTypeResult
            {
                Type = NatType.Unknown,
                Description = "No STUN response received - may be blocked by firewall"
            };
        }

        var externalAddresses = results
            .Where(r => r.MappedEndPoint != null)
            .Select(r => r.MappedEndPoint!.Address.ToString())
            .Distinct()
            .ToList();

        var externalPorts = results
            .Where(r => r.MappedEndPoint != null)
            .Select(r => r.MappedEndPoint!.Port)
            .Distinct()
            .ToList();

        // Check if external IP matches local IP (no NAT)
        var firstResult = results.First();
        if (firstResult.LocalEndPoint != null && firstResult.MappedEndPoint != null)
        {
            if (firstResult.LocalEndPoint.Address.Equals(firstResult.MappedEndPoint.Address))
            {
                return new NatTypeResult
                {
                    Type = NatType.OpenInternet,
                    ExternalAddress = firstResult.MappedEndPoint.Address,
                    Description = "No NAT detected - direct internet connection"
                };
            }
        }

        // Analyze NAT behavior
        if (externalAddresses.Count == 1 && externalPorts.Count == 1)
        {
            return new NatTypeResult
            {
                Type = NatType.FullCone,
                ExternalAddress = results.First().MappedEndPoint?.Address,
                Description = "Full cone NAT (Endpoint-Independent Mapping)"
            };
        }

        if (externalAddresses.Count == 1 && externalPorts.Count > 1)
        {
            return new NatTypeResult
            {
                Type = NatType.SymmetricNat,
                ExternalAddress = results.First().MappedEndPoint?.Address,
                Description = "Symmetric NAT (Endpoint-Dependent Mapping)"
            };
        }

        return new NatTypeResult
        {
            Type = NatType.RestrictedCone,
            ExternalAddress = results.First().MappedEndPoint?.Address,
            Description = "Restricted NAT (likely port-restricted cone NAT)"
        };
    }

    /// <summary>
    /// Advanced NAT type analysis using RFC 5780 test results.
    /// </summary>
    private NatTypeResult AnalyzeNatTypeAdvanced(NatTestResults testResults, List<StunResult> allResults)
    {
        // No response at all - blocked or UDP filtered
        if (testResults.BasicResult == null)
        {
            return new NatTypeResult
            {
                Type = NatType.Unknown,
                Description = "No STUN response received - UDP may be blocked by firewall"
            };
        }

        var mappedEndpoint = testResults.BasicResult.MappedEndPoint;
        var localEndpoint = testResults.BasicResult.LocalEndPoint;

        // Check if external IP matches local IP (no NAT)
        if (localEndpoint != null && mappedEndpoint != null &&
            localEndpoint.Address.Equals(mappedEndpoint.Address))
        {
            // No NAT, check filtering
            if (testResults.FullChangeResult != null)
            {
                return new NatTypeResult
                {
                    Type = NatType.OpenInternet,
                    ExternalAddress = mappedEndpoint.Address,
                    Description = "No NAT detected - open internet connection"
                };
            }
            else
            {
                return new NatTypeResult
                {
                    Type = NatType.OpenInternet,
                    ExternalAddress = mappedEndpoint.Address,
                    Description = "No NAT detected - firewall may be blocking unsolicited inbound"
                };
            }
        }

        // We have NAT - determine the type

        // Check for Symmetric NAT by comparing external ports across servers
        var externalPorts = allResults
            .Where(r => r.MappedEndPoint != null)
            .Select(r => r.MappedEndPoint!.Port)
            .Distinct()
            .ToList();

        if (externalPorts.Count > 1)
        {
            // Different external ports for different destinations = Symmetric NAT
            return new NatTypeResult
            {
                Type = NatType.SymmetricNat,
                ExternalAddress = mappedEndpoint?.Address,
                Description = "Symmetric NAT (Endpoint-Dependent Mapping) - hardest for P2P connections"
            };
        }

        // Also check if alternate server test returned different port
        if (testResults.AlternateServerResult?.MappedEndPoint != null &&
            mappedEndpoint != null &&
            testResults.AlternateServerResult.MappedEndPoint.Port != mappedEndpoint.Port)
        {
            return new NatTypeResult
            {
                Type = NatType.SymmetricNat,
                ExternalAddress = mappedEndpoint.Address,
                Description = "Symmetric NAT (Endpoint-Dependent Mapping) - hardest for P2P connections"
            };
        }

        // Endpoint-Independent Mapping confirmed, check filtering type
        if (testResults.FullChangeResult != null)
        {
            // Received response from different IP and port = Full Cone (Endpoint-Independent Filtering)
            return new NatTypeResult
            {
                Type = NatType.FullCone,
                ExternalAddress = mappedEndpoint?.Address,
                Description = "Full Cone NAT (Endpoint-Independent Mapping and Filtering) - best for P2P"
            };
        }
        else if (testResults.ChangePortResult != null)
        {
            // Received response from different port but same IP = Restricted Cone (Address-Dependent Filtering)
            return new NatTypeResult
            {
                Type = NatType.RestrictedCone,
                ExternalAddress = mappedEndpoint?.Address,
                Description = "Restricted Cone NAT (Endpoint-Independent Mapping, Address-Dependent Filtering)"
            };
        }
        else
        {
            // No response from changed port = Port Restricted Cone (Address+Port-Dependent Filtering)
            return new NatTypeResult
            {
                Type = NatType.PortRestrictedCone,
                ExternalAddress = mappedEndpoint?.Address,
                Description = "Port Restricted Cone NAT (Endpoint-Independent Mapping, Address+Port-Dependent Filtering)"
            };
        }
    }

    private byte[] GenerateTransactionId()
    {
        var transactionId = new byte[12];
        RandomNumberGenerator.Fill(transactionId);
        return transactionId;
    }

    public void Dispose()
    {
        _disposed = true;
    }
}

/// <summary>
/// Result of a STUN binding request.
/// </summary>
public class StunResult
{
    /// <summary>
    /// The external (mapped) endpoint as seen by the STUN server.
    /// </summary>
    public IPEndPoint? MappedEndPoint { get; set; }

    /// <summary>
    /// The local endpoint used for the request.
    /// </summary>
    public IPEndPoint? LocalEndPoint { get; set; }

    /// <summary>
    /// The STUN server endpoint.
    /// </summary>
    public IPEndPoint? ServerEndPoint { get; set; }

    /// <summary>
    /// Software string from the STUN server.
    /// </summary>
    public string? ServerSoftware { get; set; }

    /// <summary>
    /// Alternate server address (OTHER-ADDRESS attribute from RFC 5780).
    /// Used for NAT behavior detection.
    /// </summary>
    public IPEndPoint? OtherAddress { get; set; }

    /// <summary>
    /// Response origin address (RESPONSE-ORIGIN attribute from RFC 5780).
    /// </summary>
    public IPEndPoint? ResponseOrigin { get; set; }

    /// <summary>
    /// Whether this test used CHANGE-REQUEST with change-ip flag.
    /// </summary>
    public bool ChangedIp { get; set; }

    /// <summary>
    /// Whether this test used CHANGE-REQUEST with change-port flag.
    /// </summary>
    public bool ChangedPort { get; set; }
}

/// <summary>
/// NAT type classification.
/// </summary>
public enum NatType
{
    /// <summary>
    /// NAT type could not be determined.
    /// </summary>
    Unknown,

    /// <summary>
    /// No NAT - direct internet connection.
    /// </summary>
    OpenInternet,

    /// <summary>
    /// Full cone NAT (Endpoint-Independent Mapping and Filtering).
    /// Best for P2P - any external host can reach the mapped port.
    /// </summary>
    FullCone,

    /// <summary>
    /// Restricted cone NAT (Endpoint-Independent Mapping, Address-Dependent Filtering).
    /// External host must have received a packet from internal host first.
    /// </summary>
    RestrictedCone,

    /// <summary>
    /// Port-restricted cone NAT (Endpoint-Independent Mapping, Address+Port-Dependent Filtering).
    /// External host:port must have received a packet from internal host first.
    /// </summary>
    PortRestrictedCone,

    /// <summary>
    /// Symmetric NAT (Endpoint-Dependent Mapping).
    /// Different external mappings for different destinations - hardest for P2P.
    /// </summary>
    SymmetricNat
}

/// <summary>
/// Result of NAT type detection.
/// </summary>
public class NatTypeResult
{
    public NatType Type { get; set; }
    public IPAddress? ExternalAddress { get; set; }
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Internal helper class to hold NAT detection test results.
/// </summary>
internal class NatTestResults
{
    /// <summary>
    /// Result from basic binding request.
    /// </summary>
    public StunResult? BasicResult { get; set; }

    /// <summary>
    /// Result from request with CHANGE-REQUEST (change-ip and change-port).
    /// If received, indicates Endpoint-Independent Filtering.
    /// </summary>
    public StunResult? FullChangeResult { get; set; }

    /// <summary>
    /// Result from request with CHANGE-REQUEST (change-port only).
    /// If received but FullChangeResult was not, indicates Address-Dependent Filtering.
    /// </summary>
    public StunResult? ChangePortResult { get; set; }

    /// <summary>
    /// Result from request to alternate server (if OTHER-ADDRESS was provided).
    /// Used to detect Symmetric NAT by comparing external ports.
    /// </summary>
    public StunResult? AlternateServerResult { get; set; }
}
