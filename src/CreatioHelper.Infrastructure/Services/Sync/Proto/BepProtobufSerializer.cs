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
    /// Syncthing BEP magic number: 0x2EA7D90B
    /// </summary>
    public const uint BepMagic = 0x2EA7D90B;

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
    /// </summary>
    public Hello DeserializeHello(ReadOnlySpan<byte> data, out int bytesConsumed)
    {
        if (data.Length < 6)
        {
            throw new InvalidDataException("Not enough data for Hello message header");
        }

        var magic = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
        if (magic != BepMagic)
        {
            throw new InvalidDataException($"Invalid BEP magic: 0x{magic:X8}, expected 0x{BepMagic:X8}");
        }

        var helloLength = BinaryPrimitives.ReadUInt16BigEndian(data[4..6]);

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
            _ => throw new InvalidDataException($"Unknown message type: {header.Type}")
        };
    }

    #endregion

    #region LZ4 Compression

    private static byte[]? CompressLZ4(byte[] data)
    {
        try
        {
            var maxCompressedSize = LZ4Codec.MaximumOutputSize(data.Length);
            var compressed = new byte[maxCompressedSize];
            var compressedSize = LZ4Codec.Encode(data, compressed, LZ4Level.L00_FAST);

            if (compressedSize <= 0)
            {
                return null;
            }

            var result = new byte[compressedSize];
            Array.Copy(compressed, result, compressedSize);
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static byte[] DecompressLZ4(byte[] data)
    {
        // Syncthing uses block format with the decompressed size prepended
        // Try different approaches

        // First, try direct decompression (assuming size is known from protocol)
        // We'll use a heuristic: try progressively larger buffers
        var maxDecompressedSize = data.Length * 10; // Start with 10x expansion
        const int absoluteMax = 64 * 1024 * 1024; // 64MB absolute max

        while (maxDecompressedSize <= absoluteMax)
        {
            try
            {
                var decompressed = new byte[maxDecompressedSize];
                var decompressedSize = LZ4Codec.Decode(data, decompressed);

                if (decompressedSize > 0)
                {
                    var result = new byte[decompressedSize];
                    Array.Copy(decompressed, result, decompressedSize);
                    return result;
                }
            }
            catch (InvalidOperationException)
            {
                // Buffer too small, try larger
            }

            maxDecompressedSize *= 2;
        }

        throw new InvalidDataException("Failed to decompress LZ4 data");
    }

    private static bool IsCompressionWorthwhile(int originalSize, int compressedSize)
    {
        // Only use compression if we save at least MinCompressionRatio
        var savings = originalSize - compressedSize;
        return savings > 0 && (double)savings / originalSize >= MinCompressionRatio;
    }

    #endregion

    #region Stream Operations

    /// <summary>
    /// Reads a Hello message from a stream.
    /// </summary>
    public async Task<Hello> ReadHelloAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        // Read magic and length
        var headerBuffer = new byte[6];
        await stream.ReadExactlyAsync(headerBuffer, cancellationToken);

        var magic = BinaryPrimitives.ReadUInt32BigEndian(headerBuffer.AsSpan(0, 4));
        if (magic != BepMagic)
        {
            throw new InvalidDataException($"Invalid BEP magic: 0x{magic:X8}, expected 0x{BepMagic:X8}");
        }

        var helloLength = BinaryPrimitives.ReadUInt16BigEndian(headerBuffer.AsSpan(4, 2));

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
}
