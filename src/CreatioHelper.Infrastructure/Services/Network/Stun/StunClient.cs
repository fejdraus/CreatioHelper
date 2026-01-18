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
    private const ushort AttrXorMappedAddress = 0x0020;
    private const ushort AttrErrorCode = 0x0009;
    private const ushort AttrSoftware = 0x8022;
    private const ushort AttrFingerprint = 0x8028;

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
    /// </summary>
    public async Task<NatTypeResult> DetectNatTypeAsync(
        string[] stunServers,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(StunClient));

        timeout ??= TimeSpan.FromSeconds(5);

        var results = new List<StunResult>();

        foreach (var server in stunServers.Take(3)) // Test with up to 3 servers
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
            }

            // Attributes are padded to 4-byte boundaries
            offset += attrLength;
            offset = (offset + 3) & ~3;
        }

        return result.MappedEndPoint != null ? result : null;
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
