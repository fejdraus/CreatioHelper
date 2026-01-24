namespace CreatioHelper.Infrastructure.Services.Sync.Relay;

/// <summary>
/// Relay protocol implementation based on Syncthing's relay protocol.
/// Compatible with syncthing-relay-server and syncthing relay protocol v1.
/// Reference: github.com/syncthing/syncthing/lib/relay/protocol/protocol.go
/// </summary>
public static class RelayProtocol
{
    /// <summary>
    /// Protocol magic number (0x9E79BC40).
    /// Used to identify relay protocol messages.
    /// </summary>
    public const uint Magic = 0x9E79BC40;

    /// <summary>
    /// Protocol name for relay connections used in ALPN negotiation.
    /// </summary>
    public const string ProtocolName = "bep-relay";

    /// <summary>
    /// TLS handshake record type byte (0x16 = 22).
    /// Used to detect if an incoming connection is TLS or plaintext.
    /// The first byte of a TLS connection is always ContentType.Handshake (0x16).
    /// </summary>
    public const byte TlsHandshakeRecordType = 0x16;

    /// <summary>
    /// Detects if the first byte indicates a TLS connection.
    /// TLS connections always start with the handshake record type (0x16).
    /// </summary>
    /// <param name="firstByte">The first byte read from the connection</param>
    /// <returns>True if the connection appears to be TLS</returns>
    public static bool IsTlsConnection(byte firstByte) => firstByte == TlsHandshakeRecordType;

    /// <summary>
    /// Reads the first byte from a stream to detect TLS mode without consuming it.
    /// For server-side TLS mode detection when the connection type is unknown.
    /// </summary>
    /// <param name="stream">The network stream to peek</param>
    /// <param name="buffer">A buffer to receive the peeked byte</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the first byte indicates a TLS connection (0x16)</returns>
    public static async Task<bool> DetectTlsModeAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length < 1)
            throw new ArgumentException("Buffer must have at least 1 byte", nameof(buffer));

        // Read first byte to detect TLS mode
        var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, 1), cancellationToken);
        if (bytesRead == 0)
            throw new EndOfStreamException("Connection closed before TLS detection");

        return IsTlsConnection(buffer[0]);
    }

    /// <summary>
    /// Validates that the negotiated ALPN protocol matches the expected relay protocol.
    /// </summary>
    /// <param name="negotiatedProtocol">The negotiated ALPN protocol from the TLS connection</param>
    /// <returns>True if the protocol matches "bep-relay"</returns>
    public static bool ValidateAlpnProtocol(System.Net.Security.SslApplicationProtocol negotiatedProtocol)
    {
        if (negotiatedProtocol == default)
            return false;

        var expectedBytes = System.Text.Encoding.UTF8.GetBytes(ProtocolName);
        return negotiatedProtocol.Protocol.Span.SequenceEqual(expectedBytes);
    }

    /// <summary>
    /// Maximum message length in bytes (1024).
    /// From Syncthing: header.messageLength > 1024 returns "bad length" error.
    /// </summary>
    public const int MaxMessageLength = 1024;

    /// <summary>
    /// Message types matching Syncthing's implementation.
    /// Values correspond to iota-based constants in lib/relay/protocol/packets.go.
    /// </summary>
    public enum MessageType : int
    {
        Ping = 0,
        Pong = 1,
        JoinRelayRequest = 2,
        JoinSessionRequest = 3,
        Response = 4,
        ConnectRequest = 5,
        SessionInvitation = 6,
        RelayFull = 7
    }

    /// <summary>
    /// Response codes matching Syncthing's implementation.
    /// From lib/relay/protocol/protocol.go.
    /// </summary>
    public static class ResponseCode
    {
        public const int Success = 0;
        public const int NotFound = 1;
        public const int AlreadyConnected = 2;
        public const int WrongToken = 3;
        public const int UnexpectedMessage = 100;
    }
}

/// <summary>
/// Relay message header.
/// Wire format (12 bytes, all big-endian):
/// <code>
///  0                   1                   2                   3
///  0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                             magic                             |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                         message Type                          |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// |                        message Length                         |
/// +-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
/// </code>
/// </summary>
public struct RelayHeader
{
    public uint Magic { get; set; }
    public RelayProtocol.MessageType MessageType { get; set; }
    public int MessageLength { get; set; }

    /// <summary>XDR header size in bytes.</summary>
    public const int Size = 12;

    public RelayHeader(RelayProtocol.MessageType messageType, int messageLength)
    {
        Magic = RelayProtocol.Magic;
        MessageType = messageType;
        MessageLength = messageLength;
    }
}

/// <summary>
/// Base interface for all relay messages
/// </summary>
public interface IRelayMessage
{
    RelayProtocol.MessageType Type { get; }
}

/// <summary>
/// Ping message (empty payload).
/// Used to keep connections alive.
/// </summary>
public record Ping : IRelayMessage
{
    public RelayProtocol.MessageType Type => RelayProtocol.MessageType.Ping;
}

/// <summary>
/// Pong message (empty payload).
/// Response to a Ping message.
/// </summary>
public record Pong : IRelayMessage
{
    public RelayProtocol.MessageType Type => RelayProtocol.MessageType.Pong;
}

/// <summary>
/// RelayFull message (empty payload).
/// Indicates the relay server has reached its capacity limit.
/// </summary>
public record RelayFull : IRelayMessage
{
    public RelayProtocol.MessageType Type => RelayProtocol.MessageType.RelayFull;
}

/// <summary>
/// JoinRelayRequest message.
/// Wire format: [4B token_length][token_data][padding]
/// Note: In prior versions of the protocol, Token did not exist (empty payload was valid).
/// </summary>
public record JoinRelayRequest(string Token = "") : IRelayMessage
{
    public RelayProtocol.MessageType Type => RelayProtocol.MessageType.JoinRelayRequest;
}

/// <summary>
/// JoinSessionRequest message.
/// Wire format: [4B key_length][key_data (max 32B)][padding]
/// </summary>
public record JoinSessionRequest(byte[] Key) : IRelayMessage
{
    public RelayProtocol.MessageType Type => RelayProtocol.MessageType.JoinSessionRequest;

    public JoinSessionRequest() : this(Array.Empty<byte>()) { }
}

/// <summary>
/// Response message.
/// Wire format: [4B code][4B message_length][message_data][padding]
/// </summary>
public record Response(int Code, string Message) : IRelayMessage
{
    public RelayProtocol.MessageType Type => RelayProtocol.MessageType.Response;

    /// <summary>Standard response for success (code 0).</summary>
    public static Response Success => new(RelayProtocol.ResponseCode.Success, "success");

    /// <summary>Standard response for not found (code 1).</summary>
    public static Response NotFound => new(RelayProtocol.ResponseCode.NotFound, "not found");

    /// <summary>Standard response for already connected (code 2).</summary>
    public static Response AlreadyConnected => new(RelayProtocol.ResponseCode.AlreadyConnected, "already connected");

    /// <summary>Standard response for wrong token (code 3).</summary>
    public static Response WrongToken => new(RelayProtocol.ResponseCode.WrongToken, "wrong token");

    /// <summary>Standard response for unexpected message (code 100).</summary>
    public static Response UnexpectedMessage => new(RelayProtocol.ResponseCode.UnexpectedMessage, "unexpected message");

    /// <summary>
    /// Checks if this response indicates success.
    /// </summary>
    public bool IsSuccess => Code == RelayProtocol.ResponseCode.Success;
}

/// <summary>
/// ConnectRequest message.
/// Wire format: [4B id_length][id_data (max 32B)][padding]
/// ID is the device ID to connect to (32 bytes for SHA-256 device ID).
/// </summary>
public record ConnectRequest(byte[] Id) : IRelayMessage
{
    public RelayProtocol.MessageType Type => RelayProtocol.MessageType.ConnectRequest;

    public ConnectRequest() : this(Array.Empty<byte>()) { }
}

/// <summary>
/// SessionInvitation message.
/// Wire format:
/// <code>
/// [4B from_length][from_data (max 32B)][padding]
/// [4B key_length][key_data (max 32B)][padding]
/// [4B address_length][address_data (max 32B)][padding]
/// [2B zero_padding][2B port]
/// [4B server_socket (0 or 1)]
/// </code>
/// </summary>
public record SessionInvitation(
    byte[] From,
    byte[] Key,
    byte[] Address,
    ushort Port,
    bool ServerSocket
) : IRelayMessage
{
    public RelayProtocol.MessageType Type => RelayProtocol.MessageType.SessionInvitation;

    public SessionInvitation() : this(Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>(), 0, false) { }

    public override string ToString()
    {
        var deviceId = From.Length == 32 ? Convert.ToHexString(From) : "<invalid>";
        var ip = Address.Length >= 4 ? new System.Net.IPAddress(Address).ToString() : "<invalid>";
        return $"{deviceId}@{ip}:{Port}";
    }
}

/// <summary>
/// Exception thrown when a relay server is full (RelayFull message received)
/// Following Syncthing pattern from lib/relay/client/static.go
/// </summary>
public class RelayFullException : Exception
{
    public Uri? RelayUri { get; }

    public RelayFullException() : base("relay full")
    {
    }

    public RelayFullException(Uri relayUri) : base($"relay full: {relayUri}")
    {
        RelayUri = relayUri;
    }

    public RelayFullException(string message) : base(message)
    {
    }

    public RelayFullException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Exception thrown when the response code from relay server is not success
/// Following Syncthing pattern from lib/relay/client/static.go incorrectResponseCodeErr
/// </summary>
public class RelayIncorrectResponseCodeException : Exception
{
    public int Code { get; }
    public string ResponseMessage { get; }

    public RelayIncorrectResponseCodeException(int code, string message)
        : base($"incorrect response code {code}: {message}")
    {
        Code = code;
        ResponseMessage = message;
    }
}

/// <summary>
/// Event arguments for RelayFull event
/// </summary>
public class RelayFullEventArgs : EventArgs
{
    public Uri RelayUri { get; }
    public DateTime Timestamp { get; }

    public RelayFullEventArgs(Uri relayUri)
    {
        RelayUri = relayUri;
        Timestamp = DateTime.UtcNow;
    }
}