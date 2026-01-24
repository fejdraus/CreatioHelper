using System.Text.Json.Serialization;

namespace CreatioHelper.Infrastructure.Services.Sync.Relay;

/// <summary>
/// Relay protocol implementation based on Syncthing's relay protocol
/// Compatible with syncthing-relay-server and syncthing relay protocol v1
/// </summary>
public static class RelayProtocol
{
    public const uint Magic = 0x9E79BC40;
    public const string ProtocolName = "bep-relay";
    
    // Message types matching Syncthing's implementation
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
}

/// <summary>
/// Relay message header
/// </summary>
public struct RelayHeader
{
    public uint Magic { get; set; }
    public RelayProtocol.MessageType MessageType { get; set; }
    public int MessageLength { get; set; }
    
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
/// Ping message
/// </summary>
public record Ping : IRelayMessage
{
    public RelayProtocol.MessageType Type => RelayProtocol.MessageType.Ping;
}

/// <summary>
/// Pong message  
/// </summary>
public record Pong : IRelayMessage
{
    public RelayProtocol.MessageType Type => RelayProtocol.MessageType.Pong;
}

/// <summary>
/// RelayFull message
/// </summary>
public record RelayFull : IRelayMessage
{
    public RelayProtocol.MessageType Type => RelayProtocol.MessageType.RelayFull;
}

/// <summary>
/// JoinRelayRequest message
/// </summary>
public record JoinRelayRequest(string Token = "") : IRelayMessage
{
    public RelayProtocol.MessageType Type => RelayProtocol.MessageType.JoinRelayRequest;
}

/// <summary>
/// JoinSessionRequest message
/// </summary>
public record JoinSessionRequest(byte[] Key) : IRelayMessage
{
    public RelayProtocol.MessageType Type => RelayProtocol.MessageType.JoinSessionRequest;
    
    public JoinSessionRequest() : this(Array.Empty<byte>()) { }
}

/// <summary>
/// Response message
/// </summary>
public record Response(int Code, string Message) : IRelayMessage
{
    public RelayProtocol.MessageType Type => RelayProtocol.MessageType.Response;
    
    public static Response Success => new(0, "success");
    public static Response NotFound => new(1, "not found");
    public static Response AlreadyConnected => new(2, "already connected");
    public static Response WrongToken => new(3, "wrong token");
    public static Response UnexpectedMessage => new(100, "unexpected message");
}

/// <summary>
/// ConnectRequest message
/// </summary>
public record ConnectRequest(byte[] Id) : IRelayMessage
{
    public RelayProtocol.MessageType Type => RelayProtocol.MessageType.ConnectRequest;
    
    public ConnectRequest() : this(Array.Empty<byte>()) { }
}

/// <summary>
/// SessionInvitation message
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