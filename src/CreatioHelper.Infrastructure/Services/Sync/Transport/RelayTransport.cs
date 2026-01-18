using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Transport;

/// <summary>
/// Relay transport for NAT traversal using Syncthing relay servers.
/// Implements the Syncthing relay protocol for indirect connections.
/// </summary>
public class RelayTransport : ITransport
{
    private readonly ILogger<RelayTransport> _logger;
    private readonly TransportOptions _options;

    // Syncthing relay protocol magic bytes
    private static readonly byte[] RelayMagic = { 0x9E, 0x79, 0xBC, 0x40 };
    private const int RelayProtocolVersion = 1;

    // Relay message types
    private const int MsgTypePing = 0;
    private const int MsgTypePong = 1;
    private const int MsgTypeJoinRelayRequest = 2;
    private const int MsgTypeJoinSessionRequest = 3;
    private const int MsgTypeResponse = 4;
    private const int MsgTypeConnectRequest = 5;
    private const int MsgTypeSessionInvitation = 6;

    public TransportType Type => TransportType.Relay;
    public int Priority => 100; // Lowest priority, used as fallback
    public bool IsAvailable => true;
    public IReadOnlyList<string> SupportedSchemes => new[] { "relay", "relay4", "relay6" };

    public RelayTransport(ILogger<RelayTransport> logger, TransportOptions? options = null)
    {
        _logger = logger;
        _options = options ?? new TransportOptions();
    }

    public async Task<ITransportConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Connecting via relay: {Host}:{Port}", host, port);

        var tcpClient = new TcpClient();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_options.ConnectTimeoutMs);

            await tcpClient.ConnectAsync(host, port, cts.Token);

            var stream = tcpClient.GetStream();

            // Perform relay handshake
            await PerformRelayHandshakeAsync(stream, cancellationToken);

            _logger.LogInformation("Connected via relay: {Host}:{Port}", host, port);

            return new RelayConnection(tcpClient, stream);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect via relay: {Host}:{Port}", host, port);
            tcpClient.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Connects to a relay and joins a session with a specific device.
    /// </summary>
    public async Task<ITransportConnection> ConnectToDeviceViaRelayAsync(
        string relayHost,
        int relayPort,
        string targetDeviceId,
        string sessionKey,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Connecting to device {DeviceId} via relay {Host}:{Port}",
            targetDeviceId, relayHost, relayPort);

        var tcpClient = new TcpClient();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_options.ConnectTimeoutMs);

            await tcpClient.ConnectAsync(relayHost, relayPort, cts.Token);

            var stream = tcpClient.GetStream();

            // Perform relay handshake
            await PerformRelayHandshakeAsync(stream, cancellationToken);

            // Join the session
            await SendJoinSessionRequestAsync(stream, targetDeviceId, sessionKey, cancellationToken);

            // Wait for session confirmation
            var response = await ReadRelayMessageAsync(stream, cancellationToken);
            if (response.MessageType != MsgTypeResponse || response.Code != 0)
            {
                throw new InvalidOperationException($"Failed to join relay session: code {response.Code}");
            }

            _logger.LogInformation("Connected to device {DeviceId} via relay", targetDeviceId);

            return new RelayConnection(tcpClient, stream);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to device via relay: {DeviceId}", targetDeviceId);
            tcpClient.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Joins a relay as a passive listener waiting for incoming connections.
    /// </summary>
    public async Task<RelaySession> JoinRelayAsListenerAsync(
        string relayHost,
        int relayPort,
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Joining relay as listener: {Host}:{Port}", relayHost, relayPort);

        var tcpClient = new TcpClient();
        try
        {
            await tcpClient.ConnectAsync(relayHost, relayPort, cancellationToken);

            var stream = tcpClient.GetStream();

            // Perform relay handshake
            await PerformRelayHandshakeAsync(stream, cancellationToken);

            // Send join relay request
            await SendJoinRelayRequestAsync(stream, deviceId, cancellationToken);

            // Wait for response
            var response = await ReadRelayMessageAsync(stream, cancellationToken);
            if (response.MessageType != MsgTypeResponse || response.Code != 0)
            {
                throw new InvalidOperationException($"Failed to join relay: code {response.Code}");
            }

            _logger.LogInformation("Joined relay as listener: {Host}:{Port}", relayHost, relayPort);

            return new RelaySession(tcpClient, stream, relayHost, relayPort, deviceId, this);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to join relay: {Host}:{Port}", relayHost, relayPort);
            tcpClient.Dispose();
            throw;
        }
    }

    public Task<ITransportListener> ListenAsync(int port, CancellationToken cancellationToken = default)
    {
        // Relay transport doesn't support traditional listening
        // Instead, use JoinRelayAsListenerAsync
        throw new NotSupportedException("Relay transport does not support direct listening. Use JoinRelayAsListenerAsync instead.");
    }

    public bool TryParseUri(string uri, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        if (string.IsNullOrEmpty(uri))
            return false;

        // Relay URIs: relay://host:port/?id=DEVICE_ID&key=SESSION_KEY
        // or just relay://host:port
        if (!uri.StartsWith("relay://", StringComparison.OrdinalIgnoreCase) &&
            !uri.StartsWith("relay4://", StringComparison.OrdinalIgnoreCase) &&
            !uri.StartsWith("relay6://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var parsed = new Uri(uri);
            host = parsed.Host;
            port = parsed.Port > 0 ? parsed.Port : 22067; // Default Syncthing relay port
            return !string.IsNullOrEmpty(host);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Parses a relay URI and extracts connection parameters.
    /// </summary>
    public bool TryParseRelayUri(string uri, out string host, out int port, out string? deviceId, out string? sessionKey)
    {
        deviceId = null;
        sessionKey = null;

        if (!TryParseUri(uri, out host, out port))
        {
            return false;
        }

        try
        {
            var parsed = new Uri(uri);
            var queryParams = ParseQueryString(parsed.Query);
            queryParams.TryGetValue("id", out deviceId);
            queryParams.TryGetValue("key", out sessionKey);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query))
            return result;

        // Remove leading '?'
        if (query.StartsWith('?'))
            query = query[1..];

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2)
            {
                var key = Uri.UnescapeDataString(parts[0]);
                var value = Uri.UnescapeDataString(parts[1]);
                result[key] = value;
            }
            else if (parts.Length == 1)
            {
                var key = Uri.UnescapeDataString(parts[0]);
                result[key] = string.Empty;
            }
        }

        return result;
    }

    private async Task PerformRelayHandshakeAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        // Send magic and protocol version
        var handshake = new byte[8];
        RelayMagic.CopyTo(handshake, 0);
        BitConverter.GetBytes(RelayProtocolVersion).CopyTo(handshake, 4);

        await stream.WriteAsync(handshake, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        // Read server handshake
        var serverHandshake = new byte[8];
        var bytesRead = await stream.ReadAsync(serverHandshake, cancellationToken);
        if (bytesRead != 8)
        {
            throw new InvalidOperationException("Invalid relay handshake response");
        }

        // Verify magic
        for (int i = 0; i < 4; i++)
        {
            if (serverHandshake[i] != RelayMagic[i])
            {
                throw new InvalidOperationException("Invalid relay magic");
            }
        }

        var serverVersion = BitConverter.ToInt32(serverHandshake, 4);
        _logger.LogDebug("Relay handshake complete, server version: {Version}", serverVersion);
    }

    private async Task SendJoinRelayRequestAsync(NetworkStream stream, string deviceId, CancellationToken cancellationToken)
    {
        var deviceIdBytes = Encoding.UTF8.GetBytes(deviceId);
        var message = new byte[8 + deviceIdBytes.Length];

        // Message type
        BitConverter.GetBytes(MsgTypeJoinRelayRequest).CopyTo(message, 0);
        // Message length
        BitConverter.GetBytes(deviceIdBytes.Length).CopyTo(message, 4);
        // Device ID
        deviceIdBytes.CopyTo(message, 8);

        await stream.WriteAsync(message, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private async Task SendJoinSessionRequestAsync(
        NetworkStream stream,
        string targetDeviceId,
        string sessionKey,
        CancellationToken cancellationToken)
    {
        var targetIdBytes = Encoding.UTF8.GetBytes(targetDeviceId);
        var sessionKeyBytes = Convert.FromBase64String(sessionKey);

        var payloadLength = 4 + targetIdBytes.Length + 4 + sessionKeyBytes.Length;
        var message = new byte[8 + payloadLength];

        // Message type
        BitConverter.GetBytes(MsgTypeJoinSessionRequest).CopyTo(message, 0);
        // Message length
        BitConverter.GetBytes(payloadLength).CopyTo(message, 4);

        var offset = 8;
        // Target device ID length and data
        BitConverter.GetBytes(targetIdBytes.Length).CopyTo(message, offset);
        offset += 4;
        targetIdBytes.CopyTo(message, offset);
        offset += targetIdBytes.Length;
        // Session key length and data
        BitConverter.GetBytes(sessionKeyBytes.Length).CopyTo(message, offset);
        offset += 4;
        sessionKeyBytes.CopyTo(message, offset);

        await stream.WriteAsync(message, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    internal async Task<RelayMessage> ReadRelayMessageAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        var bytesRead = await stream.ReadAsync(header, cancellationToken);
        if (bytesRead != 8)
        {
            throw new InvalidOperationException("Failed to read relay message header");
        }

        var messageType = BitConverter.ToInt32(header, 0);
        var messageLength = BitConverter.ToInt32(header, 4);

        byte[]? payload = null;
        if (messageLength > 0)
        {
            payload = new byte[messageLength];
            var totalRead = 0;
            while (totalRead < messageLength)
            {
                var read = await stream.ReadAsync(payload.AsMemory(totalRead), cancellationToken);
                if (read == 0) throw new InvalidOperationException("Connection closed while reading relay message");
                totalRead += read;
            }
        }

        var message = new RelayMessage
        {
            MessageType = messageType,
            Payload = payload
        };

        // Parse response code if this is a response message
        if (messageType == MsgTypeResponse && payload?.Length >= 4)
        {
            message.Code = BitConverter.ToInt32(payload, 0);
        }

        return message;
    }

    internal class RelayMessage
    {
        public int MessageType { get; set; }
        public byte[]? Payload { get; set; }
        public int Code { get; set; }
    }
}

/// <summary>
/// Represents a relay session for receiving incoming connections.
/// </summary>
public class RelaySession : IAsyncDisposable
{
    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly string _relayHost;
    private readonly int _relayPort;
    private readonly string _deviceId;
    private readonly RelayTransport _transport;
    private bool _disposed;

    public string RelayHost => _relayHost;
    public int RelayPort => _relayPort;
    public string DeviceId => _deviceId;

    internal RelaySession(
        TcpClient tcpClient,
        NetworkStream stream,
        string relayHost,
        int relayPort,
        string deviceId,
        RelayTransport transport)
    {
        _tcpClient = tcpClient;
        _stream = stream;
        _relayHost = relayHost;
        _relayPort = relayPort;
        _deviceId = deviceId;
        _transport = transport;
    }

    /// <summary>
    /// Waits for an incoming session invitation from a peer.
    /// </summary>
    public async Task<SessionInvitation> WaitForInvitationAsync(CancellationToken cancellationToken)
    {
        while (!_disposed)
        {
            var message = await _transport.ReadRelayMessageAsync(_stream, cancellationToken);

            if (message.MessageType == 6) // SessionInvitation
            {
                if (message.Payload == null || message.Payload.Length < 40)
                {
                    throw new InvalidOperationException("Invalid session invitation");
                }

                // Parse invitation
                var offset = 0;
                var fromLength = BitConverter.ToInt32(message.Payload, offset);
                offset += 4;
                var fromDeviceId = Encoding.UTF8.GetString(message.Payload, offset, fromLength);
                offset += fromLength;

                var keyLength = BitConverter.ToInt32(message.Payload, offset);
                offset += 4;
                var sessionKey = Convert.ToBase64String(message.Payload, offset, keyLength);
                offset += keyLength;

                var addressLength = BitConverter.ToInt32(message.Payload, offset);
                offset += 4;
                var address = Encoding.UTF8.GetString(message.Payload, offset, addressLength);

                return new SessionInvitation
                {
                    FromDeviceId = fromDeviceId,
                    SessionKey = sessionKey,
                    ServerAddress = address
                };
            }

            // Handle ping
            if (message.MessageType == 0) // Ping
            {
                await SendPongAsync(cancellationToken);
            }
        }

        throw new OperationCanceledException();
    }

    private async Task SendPongAsync(CancellationToken cancellationToken)
    {
        var pong = new byte[8];
        BitConverter.GetBytes(1).CopyTo(pong, 0); // Pong message type
        BitConverter.GetBytes(0).CopyTo(pong, 4); // Empty payload

        await _stream.WriteAsync(pong, cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _stream.Close();
            _tcpClient.Close();
        }
        catch
        {
            // Ignore cleanup errors
        }

        await Task.CompletedTask;
    }
}

/// <summary>
/// Session invitation from a peer device.
/// </summary>
public class SessionInvitation
{
    public string FromDeviceId { get; set; } = string.Empty;
    public string SessionKey { get; set; } = string.Empty;
    public string ServerAddress { get; set; } = string.Empty;
}

/// <summary>
/// Connection established via relay.
/// </summary>
public class RelayConnection : ITransportConnection
{
    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private bool _disposed;

    public Stream Stream => _stream;
    public EndPoint? RemoteEndPoint => _tcpClient.Client.RemoteEndPoint;
    public EndPoint? LocalEndPoint => _tcpClient.Client.LocalEndPoint;
    public TransportType TransportType => TransportType.Relay;
    public bool IsConnected => _tcpClient.Connected && !_disposed;

    internal RelayConnection(TcpClient tcpClient, NetworkStream stream)
    {
        _tcpClient = tcpClient;
        _stream = stream;
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _stream.Close();
            _tcpClient.Close();
        }
        catch
        {
            // Ignore cleanup errors
        }

        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
    }
}
