using System.Buffers;
using System.Buffers.Binary;
using Google.Protobuf;
using K4os.Compression.LZ4;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Proto;

/// <summary>
/// Handles Syncthing-compatible wire format serialization/deserialization with LZ4 compression.
/// Wire format: [2 bytes: header length][Header protobuf][4 bytes: message length][Message protobuf]
/// Pre-auth Hello: [4 bytes: magic (0x2EA7D90B)][2 bytes: length][Hello protobuf]
/// </summary>
public class BepProtobufSerializer
{
    private readonly ILogger<BepProtobufSerializer>? _logger;

    /// <summary>
    /// Syncthing BEP magic number: 0x2EA7D90B (current protocol)
    /// </summary>
    public const uint BepMagic = 0x2EA7D90B;

    /// <summary>
    /// Syncthing v13 legacy magic number: 0x9F79BC40 (deprecated, should be rejected)
    /// </summary>
    public const uint Version13HelloMagic = 0x9F79BC40;

    /// <summary>
    /// Very old protocol magic numbers (v0.x) - should be rejected
    /// </summary>
    public const uint OldMagic1 = 0x00010001;
    public const uint OldMagic2 = 0x00010000;

    /// <summary>
    /// Compression threshold in bytes. Messages smaller than this won't be compressed.
    /// </summary>
    public const int CompressionThreshold = 128;

    /// <summary>
    /// Maximum message size (16 MB).
    /// </summary>
    public const int MaxMessageSize = 16 * 1024 * 1024;

    /// <summary>
    /// Minimum compression savings ratio (3.125% = 1/32).
    /// Only compress if we save at least this much.
    /// </summary>
    private const double MinCompressionRatio = 0.03125;

    public BepProtobufSerializer(ILogger<BepProtobufSerializer>? logger = null)
    {
        _logger = logger;
    }

    #region Hello Message (Pre-authentication)

    /// <summary>
    /// Serializes a Hello message with magic prefix (pre-authentication).
    /// Format: [4 bytes: magic][2 bytes: length][Hello protobuf]
    /// </summary>
    public byte[] SerializeHello(Hello hello)
    {
        var helloBytes = hello.ToByteArray();
        var result = new byte[4 + 2 + helloBytes.Length];

        // Magic number (big-endian)
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), BepMagic);

        // Hello length (big-endian)
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4, 2), (ushort)helloBytes.Length);

        // Hello message
        helloBytes.CopyTo(result.AsSpan(6));

        return result;
    }

    /// <summary>
    /// Deserializes a Hello message from wire format.
    /// Validates magic number and rejects legacy protocol versions.
    /// </summary>
    /// <exception cref="BepTooOldVersionException">Thrown when remote device speaks an incompatible older protocol version.</exception>
    /// <exception cref="BepUnknownMagicException">Thrown when remote device speaks an unknown (possibly newer) protocol version.</exception>
    public Hello DeserializeHello(ReadOnlySpan<byte> data, out int bytesConsumed)
    {
        if (data.Length < 6)
        {
            throw new InvalidDataException("Not enough data for Hello message header");
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);

        // Validate magic number following Syncthing's exact pattern
        switch (magic)
        {
            case BepMagic:
                // Current protocol version - proceed with deserialization
                break;

            case OldMagic1:
            case OldMagic2:
            case Version13HelloMagic:
                // Old protocol versions - reject with specific error
                _logger?.LogWarning("Received legacy BEP magic 0x{Magic:X8} from remote device - protocol version too old", magic);
                throw new BepTooOldVersionException(
                    $"The remote device speaks an older version of the protocol (magic: 0x{magic:X8}) not compatible with this version");

            default:
                // Unknown magic - possibly a newer or unrelated protocol
                _logger?.LogWarning("Received unknown BEP magic 0x{Magic:X8} from remote device", magic);
                throw new BepUnknownMagicException(
                    $"The remote device speaks an unknown (newer?) version of the protocol (magic: 0x{magic:X8})");
        }

        var helloLength = BinaryPrimitives.ReadUInt16BigEndian(data[4..6]);

        // Syncthing limits Hello message size to 32767 bytes
        if (helloLength > 32767)
        {
            throw new InvalidDataException($"Hello message too big: {helloLength} bytes");
        }

        if (data.Length < 6 + helloLength)
        {
            throw new InvalidDataException($"Not enough data for Hello message body: need {6 + helloLength}, have {data.Length}");
        }

        bytesConsumed = 6 + helloLength;
        return Hello.Parser.ParseFrom(data.Slice(6, helloLength));
    }

    #endregion

    #region Regular Messages (Post-authentication)

    /// <summary>
    /// Serializes a message with header to wire format.
    /// Format: [2 bytes: header length][Header protobuf][4 bytes: message length][Message protobuf]
    /// </summary>
    public byte[] SerializeMessage<T>(T message, MessageType messageType, bool allowCompression = true) where T : IMessage
    {
        var messageBytes = message.ToByteArray();
        var compression = MessageCompression.None;

        // Apply compression if beneficial
        byte[] finalMessageBytes = messageBytes;
        if (allowCompression && messageBytes.Length >= CompressionThreshold)
        {
            var compressed = CompressLZ4(messageBytes);
            if (compressed != null && IsCompressionWorthwhile(messageBytes.Length, compressed.Length))
            {
                finalMessageBytes = compressed;
                compression = MessageCompression.Lz4;
                _logger?.LogTrace("Compressed message from {Original} to {Compressed} bytes ({Ratio:P1})",
                    messageBytes.Length, compressed.Length, (double)compressed.Length / messageBytes.Length);
            }
        }

        var header = new Header
        {
            Type = messageType,
            Compression = compression
        };

        var headerBytes = header.ToByteArray();

        // Calculate total size
        var totalSize = 2 + headerBytes.Length + 4 + finalMessageBytes.Length;
        var result = new byte[totalSize];

        var offset = 0;

        // Header length (big-endian)
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(offset, 2), (ushort)headerBytes.Length);
        offset += 2;

        // Header
        headerBytes.CopyTo(result.AsSpan(offset, headerBytes.Length));
        offset += headerBytes.Length;

        // Message length (big-endian)
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(offset, 4), finalMessageBytes.Length);
        offset += 4;

        // Message
        finalMessageBytes.CopyTo(result.AsSpan(offset));

        return result;
    }

    /// <summary>
    /// Deserializes a message from wire format.
    /// Returns the header and raw message bytes (decompressed if needed).
    /// </summary>
    public (Header header, byte[] messageBytes) DeserializeMessage(ReadOnlySpan<byte> data, out int bytesConsumed)
    {
        if (data.Length < 2)
        {
            throw new InvalidDataException("Not enough data for header length");
        }

        var headerLength = BinaryPrimitives.ReadUInt16BigEndian(data[..2]);
        var offset = 2;

        if (data.Length < offset + headerLength)
        {
            throw new InvalidDataException($"Not enough data for header: need {offset + headerLength}, have {data.Length}");
        }

        var header = Header.Parser.ParseFrom(data.Slice(offset, headerLength));
        offset += headerLength;

        if (data.Length < offset + 4)
        {
            throw new InvalidDataException("Not enough data for message length");
        }

        var messageLength = BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4));
        offset += 4;

        if (messageLength > MaxMessageSize)
        {
            throw new InvalidDataException($"Message too large: {messageLength} bytes, max {MaxMessageSize}");
        }

        if (messageLength < 0)
        {
            throw new InvalidDataException($"Invalid message length: {messageLength}");
        }

        if (data.Length < offset + messageLength)
        {
            throw new InvalidDataException($"Not enough data for message body: need {offset + messageLength}, have {data.Length}");
        }

        var messageBytes = data.Slice(offset, messageLength).ToArray();
        bytesConsumed = offset + messageLength;

        // Decompress if needed
        if (header.Compression == MessageCompression.Lz4)
        {
            messageBytes = DecompressLZ4(messageBytes);
            _logger?.LogTrace("Decompressed message from {Compressed} to {Original} bytes",
                messageLength, messageBytes.Length);
        }

        return (header, messageBytes);
    }

    /// <summary>
    /// Deserializes a typed message from raw bytes.
    /// </summary>
    public T ParseMessage<T>(byte[] messageBytes) where T : IMessage<T>, new()
    {
        var parser = new MessageParser<T>(() => new T());
        return parser.ParseFrom(messageBytes);
    }

    /// <summary>
    /// Parses a message based on the header type.
    /// </summary>
    public IMessage ParseMessageByType(Header header, byte[] messageBytes)
    {
        return header.Type switch
        {
            MessageType.ClusterConfig => ClusterConfig.Parser.ParseFrom(messageBytes),
            MessageType.Index => Index.Parser.ParseFrom(messageBytes),
            MessageType.IndexUpdate => IndexUpdate.Parser.ParseFrom(messageBytes),
            MessageType.Request => Request.Parser.ParseFrom(messageBytes),
            MessageType.Response => Response.Parser.ParseFrom(messageBytes),
            MessageType.DownloadProgress => DownloadProgress.Parser.ParseFrom(messageBytes),
            MessageType.Ping => Ping.Parser.ParseFrom(messageBytes),
            MessageType.Close => Close.Parser.ParseFrom(messageBytes),
            // CreatioHelper P2P Upgrade Extensions
            MessageType.AgentUpdateRequest => AgentUpdateRequest.Parser.ParseFrom(messageBytes),
            MessageType.AgentUpdateResponse => AgentUpdateResponse.Parser.ParseFrom(messageBytes),
            _ => throw new InvalidDataException($"Unknown message type: {header.Type}")
        };
    }

    #endregion

    #region LZ4 Compression

    /// <summary>
    /// Compresses data using LZ4 raw block format with Syncthing-compatible wire format.
    /// Wire format: [4 bytes: uncompressed size (big-endian)] + [LZ4 compressed block]
    /// This matches Syncthing's lz4Compress function in protocol.go
    /// </summary>
    private static byte[]? CompressLZ4(byte[] data)
    {
        try
        {
            // LZ4 raw block compression (not frame format)
            var maxCompressedSize = LZ4Codec.MaximumOutputSize(data.Length);
            var compressBuffer = new byte[maxCompressedSize];

            // Use Encode which uses raw block format
            var compressedSize = LZ4Codec.Encode(data, compressBuffer, LZ4Level.L00_FAST);

            if (compressedSize <= 0)
            {
                // Data is not compressible
                return null;
            }

            // Syncthing wire format: [4 bytes uncompressed size] + [compressed data]
            // The uncompressed size is written as big-endian uint32
            var result = new byte[4 + compressedSize];
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), (uint)data.Length);
            Array.Copy(compressBuffer, 0, result, 4, compressedSize);

            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decompresses data using LZ4 raw block format with Syncthing-compatible wire format.
    /// Wire format: [4 bytes: uncompressed size (big-endian)] + [LZ4 compressed block]
    /// This matches Syncthing's lz4Decompress function in protocol.go
    /// </summary>
    private static byte[] DecompressLZ4(byte[] data)
    {
        if (data.Length < 4)
        {
            throw new InvalidDataException("LZ4 compressed data too short - missing size prefix");
        }

        // Read uncompressed size from first 4 bytes (big-endian)
        var uncompressedSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));

        // Sanity check on size
        if (uncompressedSize > MaxMessageSize)
        {
            throw new InvalidDataException($"LZ4 uncompressed size {uncompressedSize} exceeds maximum {MaxMessageSize}");
        }

        // Allocate buffer with exact size
        var decompressed = new byte[uncompressedSize];

        // Decompress the raw LZ4 block (data after the 4-byte prefix)
        var decompressedSize = LZ4Codec.Decode(data, 4, data.Length - 4, decompressed, 0, decompressed.Length);

        if (decompressedSize != uncompressedSize)
        {
            throw new InvalidDataException(
                $"LZ4 decompression size mismatch: expected {uncompressedSize}, got {decompressedSize}");
        }

        return decompressed;
    }

    /// <summary>
    /// Determines if compression is worthwhile based on Syncthing's threshold.
    /// Only compress if we save at least 3.125% (1/32) of the original size.
    /// This matches Syncthing's writeCompressedMessage logic in protocol.go:
    /// "The compressed size may be at most n-n/32 = .96875*n bytes"
    /// </summary>
    private static bool IsCompressionWorthwhile(int originalSize, int compressedSize)
    {
        // Only use compression if we save at least MinCompressionRatio (3.125% = 1/32)
        // Note: compressedSize includes the 4-byte prefix, so we account for that
        var savings = originalSize - compressedSize;
        return savings > 0 && (double)savings / originalSize >= MinCompressionRatio;
    }

    #endregion

    #region Stream Operations

    /// <summary>
    /// Reads a Hello message from a stream.
    /// Validates magic number and rejects legacy protocol versions.
    /// </summary>
    /// <exception cref="BepTooOldVersionException">Thrown when remote device speaks an incompatible older protocol version.</exception>
    /// <exception cref="BepUnknownMagicException">Thrown when remote device speaks an unknown (possibly newer) protocol version.</exception>
    public async Task<Hello> ReadHelloAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        // Read magic and length
        var headerBuffer = new byte[6];
        await stream.ReadExactlyAsync(headerBuffer, cancellationToken);

        var magic = BinaryPrimitives.ReadUInt32BigEndian(headerBuffer.AsSpan(0, 4));

        // Validate magic number following Syncthing's exact pattern
        switch (magic)
        {
            case BepMagic:
                // Current protocol version - proceed with deserialization
                break;

            case OldMagic1:
            case OldMagic2:
            case Version13HelloMagic:
                // Old protocol versions - reject with specific error
                _logger?.LogWarning("Received legacy BEP magic 0x{Magic:X8} from remote device - protocol version too old", magic);
                throw new BepTooOldVersionException(
                    $"The remote device speaks an older version of the protocol (magic: 0x{magic:X8}) not compatible with this version");

            default:
                // Unknown magic - possibly a newer or unrelated protocol
                _logger?.LogWarning("Received unknown BEP magic 0x{Magic:X8} from remote device", magic);
                throw new BepUnknownMagicException(
                    $"The remote device speaks an unknown (newer?) version of the protocol (magic: 0x{magic:X8})");
        }

        var helloLength = BinaryPrimitives.ReadUInt16BigEndian(headerBuffer.AsSpan(4, 2));

        // Syncthing limits Hello message size to 32767 bytes
        if (helloLength > 32767)
        {
            throw new InvalidDataException($"Hello message too big: {helloLength} bytes");
        }

        // Read Hello body
        var helloBuffer = new byte[helloLength];
        await stream.ReadExactlyAsync(helloBuffer, cancellationToken);

        return Hello.Parser.ParseFrom(helloBuffer);
    }

    /// <summary>
    /// Writes a Hello message to a stream.
    /// </summary>
    public async Task WriteHelloAsync(Stream stream, Hello hello, CancellationToken cancellationToken = default)
    {
        var data = SerializeHello(hello);
        await stream.WriteAsync(data, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Reads a message from a stream.
    /// </summary>
    public async Task<(Header header, IMessage message)> ReadMessageAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        // Read header length
        var headerLengthBuffer = new byte[2];
        await stream.ReadExactlyAsync(headerLengthBuffer, cancellationToken);
        var headerLength = BinaryPrimitives.ReadUInt16BigEndian(headerLengthBuffer);

        // Read header
        var headerBuffer = new byte[headerLength];
        await stream.ReadExactlyAsync(headerBuffer, cancellationToken);
        var header = Header.Parser.ParseFrom(headerBuffer);

        // Read message length
        var messageLengthBuffer = new byte[4];
        await stream.ReadExactlyAsync(messageLengthBuffer, cancellationToken);
        var messageLength = BinaryPrimitives.ReadInt32BigEndian(messageLengthBuffer);

        if (messageLength > MaxMessageSize)
        {
            throw new InvalidDataException($"Message too large: {messageLength} bytes, max {MaxMessageSize}");
        }

        if (messageLength < 0)
        {
            throw new InvalidDataException($"Invalid message length: {messageLength}");
        }

        // Read message body
        var messageBuffer = new byte[messageLength];
        if (messageLength > 0)
        {
            await stream.ReadExactlyAsync(messageBuffer, cancellationToken);
        }

        // Decompress if needed
        if (header.Compression == MessageCompression.Lz4 && messageBuffer.Length > 0)
        {
            messageBuffer = DecompressLZ4(messageBuffer);
            _logger?.LogTrace("Decompressed message from {Compressed} to {Original} bytes",
                messageLength, messageBuffer.Length);
        }

        var message = ParseMessageByType(header, messageBuffer);
        return (header, message);
    }

    /// <summary>
    /// Writes a message to a stream.
    /// </summary>
    public async Task WriteMessageAsync<T>(Stream stream, T message, MessageType messageType, bool allowCompression = true, CancellationToken cancellationToken = default) where T : IMessage
    {
        var data = SerializeMessage(message, messageType, allowCompression);
        await stream.WriteAsync(data, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    #endregion

    /// <summary>
    /// Checks if an exception indicates a protocol version mismatch that should be reported to the user.
    /// </summary>
    public static bool IsVersionMismatch(Exception ex)
    {
        return ex is BepTooOldVersionException or BepUnknownMagicException;
    }
}

/// <summary>
/// Exception thrown when the remote device speaks an older, incompatible version of the BEP protocol.
/// This is a reliable indicator that the connection cannot proceed and the user should be notified.
/// </summary>
public class BepTooOldVersionException : Exception
{
    public BepTooOldVersionException(string message) : base(message) { }
    public BepTooOldVersionException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Exception thrown when the remote device speaks an unknown (possibly newer) version of the BEP protocol.
/// This could indicate the remote device is running a newer version of the protocol that we don't understand.
/// </summary>
public class BepUnknownMagicException : Exception
{
    public BepUnknownMagicException(string message) : base(message) { }
    public BepUnknownMagicException(string message, Exception innerException) : base(message, innerException) { }
}
