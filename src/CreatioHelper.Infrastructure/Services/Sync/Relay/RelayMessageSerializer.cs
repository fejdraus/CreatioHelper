using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace CreatioHelper.Infrastructure.Services.Sync.Relay;

/// <summary>
/// Relay message serializer/deserializer compatible with Syncthing's XDR-like protocol
/// </summary>
public static class RelayMessageSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Write a relay message to a stream
    /// </summary>
    public static async Task WriteMessageAsync(Stream stream, IRelayMessage message, CancellationToken cancellationToken = default)
    {
        // Serialize message payload
        var payload = SerializeMessage(message);
        
        // Create header
        var header = new RelayHeader(message.Type, payload.Length);
        
        // Write header
        var headerBytes = SerializeHeader(header);
        await stream.WriteAsync(headerBytes, cancellationToken);
        
        // Write payload
        if (payload.Length > 0)
        {
            await stream.WriteAsync(payload, cancellationToken);
        }
        
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Read a relay message from a stream
    /// </summary>
    public static async Task<IRelayMessage> ReadMessageAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        // Read header (12 bytes)
        var headerBuffer = new byte[12];
        await ReadExactAsync(stream, headerBuffer, cancellationToken);
        
        var header = DeserializeHeader(headerBuffer);
        
        // Validate header
        if (header.Magic != RelayProtocol.Magic)
        {
            throw new InvalidDataException($"Invalid magic number: 0x{header.Magic:X8}");
        }
        
        if (header.MessageLength < 0 || header.MessageLength > 1024 * 1024) // 1MB limit
        {
            throw new InvalidDataException($"Invalid message length: {header.MessageLength}");
        }
        
        // Read payload
        byte[] payload = Array.Empty<byte>();
        if (header.MessageLength > 0)
        {
            payload = new byte[header.MessageLength];
            await ReadExactAsync(stream, payload, cancellationToken);
        }
        
        // Deserialize message
        return DeserializeMessage(header.MessageType, payload);
    }

    private static byte[] SerializeHeader(RelayHeader header)
    {
        var buffer = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), header.Magic);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(4, 4), (int)header.MessageType);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(8, 4), header.MessageLength);
        return buffer;
    }

    private static RelayHeader DeserializeHeader(byte[] buffer)
    {
        var magic = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(0, 4));
        var messageType = (RelayProtocol.MessageType)BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(4, 4));
        var messageLength = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(8, 4));
        
        return new RelayHeader(messageType, messageLength) { Magic = magic };
    }

    private static byte[] SerializeMessage(IRelayMessage message)
    {
        return message switch
        {
            Ping => Array.Empty<byte>(),
            Pong => Array.Empty<byte>(),
            RelayFull => Array.Empty<byte>(),
            JoinRelayRequest req => SerializeJoinRelayRequest(req),
            JoinSessionRequest req => SerializeJoinSessionRequest(req),
            Response resp => SerializeResponse(resp),
            ConnectRequest req => SerializeConnectRequest(req),
            SessionInvitation inv => SerializeSessionInvitation(inv),
            _ => throw new ArgumentException($"Unknown message type: {message.GetType()}")
        };
    }

    private static IRelayMessage DeserializeMessage(RelayProtocol.MessageType messageType, byte[] payload)
    {
        return messageType switch
        {
            RelayProtocol.MessageType.Ping => new Ping(),
            RelayProtocol.MessageType.Pong => new Pong(),
            RelayProtocol.MessageType.RelayFull => new RelayFull(),
            RelayProtocol.MessageType.JoinRelayRequest => DeserializeJoinRelayRequest(payload),
            RelayProtocol.MessageType.JoinSessionRequest => DeserializeJoinSessionRequest(payload),
            RelayProtocol.MessageType.Response => DeserializeResponse(payload),
            RelayProtocol.MessageType.ConnectRequest => DeserializeConnectRequest(payload),
            RelayProtocol.MessageType.SessionInvitation => DeserializeSessionInvitation(payload),
            _ => throw new ArgumentException($"Unknown message type: {messageType}")
        };
    }

    private static byte[] SerializeJoinRelayRequest(JoinRelayRequest request)
    {
        if (string.IsNullOrEmpty(request.Token))
            return Array.Empty<byte>();
        
        return Encoding.UTF8.GetBytes(request.Token);
    }

    private static JoinRelayRequest DeserializeJoinRelayRequest(byte[] payload)
    {
        if (payload.Length == 0)
            return new JoinRelayRequest("");
        
        var token = Encoding.UTF8.GetString(payload);
        return new JoinRelayRequest(token);
    }

    private static byte[] SerializeJoinSessionRequest(JoinSessionRequest request)
    {
        if (request.Key.Length > 32)
            throw new ArgumentException("Key cannot be longer than 32 bytes");
        
        var buffer = new byte[4 + request.Key.Length];
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(0, 4), request.Key.Length);
        request.Key.CopyTo(buffer.AsSpan(4));
        return buffer;
    }

    private static JoinSessionRequest DeserializeJoinSessionRequest(byte[] payload)
    {
        if (payload.Length < 4)
            return new JoinSessionRequest();
        
        var keyLength = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, 4));
        if (keyLength < 0 || keyLength > 32 || payload.Length < 4 + keyLength)
            throw new InvalidDataException($"Invalid key length: {keyLength}");
        
        var key = payload.AsSpan(4, keyLength).ToArray();
        return new JoinSessionRequest(key);
    }

    private static byte[] SerializeResponse(Response response)
    {
        var messageBytes = Encoding.UTF8.GetBytes(response.Message);
        var buffer = new byte[8 + messageBytes.Length];
        
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(0, 4), response.Code);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(4, 4), messageBytes.Length);
        messageBytes.CopyTo(buffer.AsSpan(8));
        
        return buffer;
    }

    private static Response DeserializeResponse(byte[] payload)
    {
        if (payload.Length < 8)
            throw new InvalidDataException("Invalid response payload");
        
        var code = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, 4));
        var messageLength = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(4, 4));
        
        if (messageLength < 0 || payload.Length < 8 + messageLength)
            throw new InvalidDataException($"Invalid message length: {messageLength}");
        
        var message = Encoding.UTF8.GetString(payload.AsSpan(8, messageLength));
        return new Response(code, message);
    }

    private static byte[] SerializeConnectRequest(ConnectRequest request)
    {
        if (request.Id.Length > 32)
            throw new ArgumentException("ID cannot be longer than 32 bytes");
        
        var buffer = new byte[4 + request.Id.Length];
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(0, 4), request.Id.Length);
        request.Id.CopyTo(buffer.AsSpan(4));
        return buffer;
    }

    private static ConnectRequest DeserializeConnectRequest(byte[] payload)
    {
        if (payload.Length < 4)
            return new ConnectRequest();
        
        var idLength = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, 4));
        if (idLength < 0 || idLength > 32 || payload.Length < 4 + idLength)
            throw new InvalidDataException($"Invalid ID length: {idLength}");
        
        var id = payload.AsSpan(4, idLength).ToArray();
        return new ConnectRequest(id);
    }

    private static byte[] SerializeSessionInvitation(SessionInvitation invitation)
    {
        if (invitation.From.Length > 32 || invitation.Key.Length > 32 || invitation.Address.Length > 32)
            throw new ArgumentException("Field lengths cannot exceed 32 bytes");
        
        var buffer = new byte[16 + invitation.From.Length + invitation.Key.Length + invitation.Address.Length];
        var offset = 0;
        
        // From length and data
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset, 4), invitation.From.Length);
        offset += 4;
        invitation.From.CopyTo(buffer.AsSpan(offset));
        offset += invitation.From.Length;
        
        // Key length and data
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset, 4), invitation.Key.Length);
        offset += 4;
        invitation.Key.CopyTo(buffer.AsSpan(offset));
        offset += invitation.Key.Length;
        
        // Address length and data
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset, 4), invitation.Address.Length);
        offset += 4;
        invitation.Address.CopyTo(buffer.AsSpan(offset));
        offset += invitation.Address.Length;
        
        // Port and ServerSocket
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset, 2), invitation.Port);
        offset += 2;
        buffer[offset] = invitation.ServerSocket ? (byte)1 : (byte)0;
        offset += 1;
        
        // Padding byte
        buffer[offset] = 0;
        
        return buffer;
    }

    private static SessionInvitation DeserializeSessionInvitation(byte[] payload)
    {
        if (payload.Length < 15) // Minimum size for empty arrays
            throw new InvalidDataException("Invalid session invitation payload");
        
        var offset = 0;
        
        // Read From
        var fromLength = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(offset, 4));
        offset += 4;
        if (fromLength < 0 || fromLength > 32 || payload.Length < offset + fromLength)
            throw new InvalidDataException($"Invalid From length: {fromLength}");
        var from = payload.AsSpan(offset, fromLength).ToArray();
        offset += fromLength;
        
        // Read Key
        if (payload.Length < offset + 4)
            throw new InvalidDataException("Incomplete session invitation payload");
        var keyLength = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(offset, 4));
        offset += 4;
        if (keyLength < 0 || keyLength > 32 || payload.Length < offset + keyLength)
            throw new InvalidDataException($"Invalid Key length: {keyLength}");
        var key = payload.AsSpan(offset, keyLength).ToArray();
        offset += keyLength;
        
        // Read Address
        if (payload.Length < offset + 4)
            throw new InvalidDataException("Incomplete session invitation payload");
        var addressLength = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(offset, 4));
        offset += 4;
        if (addressLength < 0 || addressLength > 32 || payload.Length < offset + addressLength)
            throw new InvalidDataException($"Invalid Address length: {addressLength}");
        var address = payload.AsSpan(offset, addressLength).ToArray();
        offset += addressLength;
        
        // Read Port and ServerSocket
        if (payload.Length < offset + 3)
            throw new InvalidDataException("Incomplete session invitation payload");
        var port = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(offset, 2));
        offset += 2;
        var serverSocket = payload[offset] != 0;
        
        return new SessionInvitation(from, key, address, port, serverSocket);
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead), cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of stream");
            totalRead += read;
        }
    }
}