using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace CreatioHelper.Infrastructure.Services.Sync.Relay;

/// <summary>
/// Relay message serializer/deserializer 100% compatible with Syncthing's XDR protocol
/// Implements exact XDR (External Data Representation) format used by syncthing-relay
/// </summary>
public static class RelayMessageSerializer
{

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
        
        return SerializeXDRString(request.Token);
    }

    private static JoinRelayRequest DeserializeJoinRelayRequest(byte[] payload)
    {
        if (payload.Length == 0)
            return new JoinRelayRequest("");
        
        var token = DeserializeXDRString(payload, 0, out _);
        return new JoinRelayRequest(token);
    }

    private static byte[] SerializeJoinSessionRequest(JoinSessionRequest request)
    {
        if (request.Key.Length > 32)
            throw new ArgumentException("Key cannot be longer than 32 bytes");
        
        return SerializeXDRBytes(request.Key);
    }

    private static JoinSessionRequest DeserializeJoinSessionRequest(byte[] payload)
    {
        if (payload.Length < 4)
            return new JoinSessionRequest();
        
        var key = DeserializeXDRBytes(payload, 0, 32, out _);
        return new JoinSessionRequest(key);
    }

    private static byte[] SerializeResponse(Response response)
    {
        using var stream = new MemoryStream();
        
        // Code (int32)
        var codeBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(codeBytes, (uint)response.Code);
        stream.Write(codeBytes);
        
        // Message (XDR string)
        var messageBytes = SerializeXDRString(response.Message);
        stream.Write(messageBytes);
        
        return stream.ToArray();
    }

    private static Response DeserializeResponse(byte[] payload)
    {
        if (payload.Length < 4)
            throw new InvalidDataException("Invalid response payload");
        
        var code = (int)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(0, 4));
        var message = DeserializeXDRString(payload, 4, out _);
        
        return new Response(code, message);
    }

    private static byte[] SerializeConnectRequest(ConnectRequest request)
    {
        if (request.Id.Length > 32)
            throw new ArgumentException("ID cannot be longer than 32 bytes");
        
        return SerializeXDRBytes(request.Id);
    }

    private static ConnectRequest DeserializeConnectRequest(byte[] payload)
    {
        if (payload.Length < 4)
            return new ConnectRequest();
        
        var id = DeserializeXDRBytes(payload, 0, 32, out _);
        return new ConnectRequest(id);
    }

    private static byte[] SerializeSessionInvitation(SessionInvitation invitation)
    {
        if (invitation.From.Length > 32 || invitation.Key.Length > 32 || invitation.Address.Length > 32)
            throw new ArgumentException("Field lengths cannot exceed 32 bytes");
        
        using var stream = new MemoryStream();
        
        // From (XDR bytes with max 32)
        var fromBytes = SerializeXDRBytes(invitation.From);
        stream.Write(fromBytes);
        
        // Key (XDR bytes with max 32)  
        var keyBytes = SerializeXDRBytes(invitation.Key);
        stream.Write(keyBytes);
        
        // Address (XDR bytes with max 32)
        var addressBytes = SerializeXDRBytes(invitation.Address);
        stream.Write(addressBytes);
        
        // Port (uint16 in XDR format - becomes uint32 with zero padding)
        var portBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(portBytes, invitation.Port);
        stream.Write(portBytes);
        
        // ServerSocket (bool in XDR format - becomes uint32)
        var serverSocketBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(serverSocketBytes, invitation.ServerSocket ? 1u : 0u);
        stream.Write(serverSocketBytes);
        
        return stream.ToArray();
    }

    private static SessionInvitation DeserializeSessionInvitation(byte[] payload)
    {
        if (payload.Length < 20) // Minimum: 3 empty arrays (12 bytes) + port (4) + serverSocket (4)
            throw new InvalidDataException("Invalid session invitation payload");
        
        var offset = 0;
        
        // Read From (XDR bytes)
        var from = DeserializeXDRBytes(payload, offset, 32, out var fromBytesConsumed);
        offset += fromBytesConsumed;
        
        // Read Key (XDR bytes)
        var key = DeserializeXDRBytes(payload, offset, 32, out var keyBytesConsumed);
        offset += keyBytesConsumed;
        
        // Read Address (XDR bytes)
        var address = DeserializeXDRBytes(payload, offset, 32, out var addressBytesConsumed);
        offset += addressBytesConsumed;
        
        // Read Port (uint32, but only lower 16 bits used)
        if (payload.Length < offset + 4)
            throw new InvalidDataException("Incomplete session invitation payload");
        var portValue = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(offset, 4));
        var port = (ushort)portValue;
        offset += 4;
        
        // Read ServerSocket (uint32 bool)
        if (payload.Length < offset + 4)
            throw new InvalidDataException("Incomplete session invitation payload");
        var serverSocketValue = BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(offset, 4));
        var serverSocket = serverSocketValue != 0;
        
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

    /// <summary>
    /// Serialize string in XDR format (length + padded data)
    /// </summary>
    private static byte[] SerializeXDRString(string value)
    {
        var stringBytes = Encoding.UTF8.GetBytes(value);
        var padding = CalculateXDRPadding(stringBytes.Length);
        var buffer = new byte[4 + stringBytes.Length + padding];
        
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), (uint)stringBytes.Length);
        stringBytes.CopyTo(buffer.AsSpan(4));
        // Padding bytes are already zero-initialized
        
        return buffer;
    }

    /// <summary>
    /// Deserialize XDR string from buffer
    /// </summary>
    private static string DeserializeXDRString(byte[] buffer, int offset, out int bytesConsumed)
    {
        if (buffer.Length < offset + 4)
            throw new InvalidDataException("Buffer too short for XDR string length");
        
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));
        var padding = CalculateXDRPadding(length);
        bytesConsumed = 4 + length + padding;
        
        if (buffer.Length < offset + bytesConsumed)
            throw new InvalidDataException($"Buffer too short for XDR string data: expected {bytesConsumed} bytes");
        
        return Encoding.UTF8.GetString(buffer.AsSpan(offset + 4, length));
    }

    /// <summary>
    /// Serialize bytes in XDR format (length + padded data)
    /// </summary>
    private static byte[] SerializeXDRBytes(byte[] value)
    {
        var padding = CalculateXDRPadding(value.Length);
        var buffer = new byte[4 + value.Length + padding];
        
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(0, 4), (uint)value.Length);
        value.CopyTo(buffer.AsSpan(4));
        // Padding bytes are already zero-initialized
        
        return buffer;
    }

    /// <summary>
    /// Deserialize XDR bytes from buffer with maximum length validation
    /// </summary>
    private static byte[] DeserializeXDRBytes(byte[] buffer, int offset, int maxLength, out int bytesConsumed)
    {
        if (buffer.Length < offset + 4)
            throw new InvalidDataException("Buffer too short for XDR bytes length");
        
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(offset, 4));
        if (length < 0 || length > maxLength)
            throw new InvalidDataException($"Invalid XDR bytes length: {length}, max: {maxLength}");
        
        var padding = CalculateXDRPadding(length);
        bytesConsumed = 4 + length + padding;
        
        if (buffer.Length < offset + bytesConsumed)
            throw new InvalidDataException($"Buffer too short for XDR bytes data: expected {bytesConsumed} bytes");
        
        return buffer.AsSpan(offset + 4, length).ToArray();
    }

    /// <summary>
    /// Calculate XDR padding for 4-byte alignment
    /// </summary>
    private static int CalculateXDRPadding(int length)
    {
        return (4 - (length % 4)) % 4;
    }
}