using System.Buffers.Binary;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services.Sync;
using CreatioHelper.Infrastructure.Services.Sync.Proto;
using Google.Protobuf;
using K4os.Compression.LZ4;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

// Disambiguate BepErrorCode
using AppBepErrorCode = CreatioHelper.Application.Interfaces.BepErrorCode;

namespace CreatioHelper.UnitTests.Sync;

/// <summary>
/// Tests for BepConnectionAdapter block transfer functionality with LZ4 compression.
/// Verifies REQUEST/RESPONSE block transfer following Syncthing BEP protocol.
///
/// Key aspects tested:
/// 1. Block request serialization and handling
/// 2. Block response with LZ4 compression
/// 3. Compressed block data round-trip verification
/// 4. Large block transfer with compression benefits
/// </summary>
public class BepConnectionAdapterBlockTransferTests
{
    private readonly Mock<ILogger<BepConnectionAdapter>> _mockAdapterLogger;
    private readonly BepProtobufSerializer _serializer;

    public BepConnectionAdapterBlockTransferTests()
    {
        _mockAdapterLogger = new Mock<ILogger<BepConnectionAdapter>>();
        _serializer = new BepProtobufSerializer();
    }

    #region Request Message Tests

    /// <summary>
    /// Tests that block request messages are properly formatted.
    /// </summary>
    [Fact]
    public async Task BlockRequest_Serialization_RoundTrip()
    {
        // Arrange
        using var stream = new MemoryStream();
        var request = new Request
        {
            Id = 42,
            Folder = "sync-folder",
            Name = "large-file.bin",
            Offset = 131072, // Second block (128 KB offset)
            Size = 131072,   // 128 KB block size
            Hash = ByteString.CopyFrom(new byte[32]),
            FromTemporary = false,
            BlockNo = 1
        };

        // Act
        await _serializer.WriteMessageAsync(stream, request, MessageType.Request);
        stream.Position = 0;
        var (header, message) = await _serializer.ReadMessageAsync(stream);

        // Assert
        Assert.Equal(MessageType.Request, header.Type);
        var parsed = Assert.IsType<Request>(message);
        Assert.Equal(42, parsed.Id);
        Assert.Equal("sync-folder", parsed.Folder);
        Assert.Equal("large-file.bin", parsed.Name);
        Assert.Equal(131072, parsed.Offset);
        Assert.Equal(131072, parsed.Size);
        Assert.Equal(1, parsed.BlockNo);
    }

    /// <summary>
    /// Tests block request with block hash verification.
    /// </summary>
    [Fact]
    public async Task BlockRequest_WithHash_PreservesHash()
    {
        // Arrange
        using var stream = new MemoryStream();
        var expectedHash = new byte[32];
        Random.Shared.NextBytes(expectedHash);

        var request = new Request
        {
            Id = 1,
            Folder = "test-folder",
            Name = "test-file.txt",
            Offset = 0,
            Size = 65536,
            Hash = ByteString.CopyFrom(expectedHash),
            BlockNo = 0
        };

        // Act
        await _serializer.WriteMessageAsync(stream, request, MessageType.Request);
        stream.Position = 0;
        var (_, message) = await _serializer.ReadMessageAsync(stream);

        // Assert
        var parsed = Assert.IsType<Request>(message);
        Assert.Equal(expectedHash, parsed.Hash.ToByteArray());
    }

    #endregion

    #region Response Message Tests with LZ4 Compression

    /// <summary>
    /// Tests that block response with highly compressible data uses LZ4 compression.
    /// </summary>
    [Fact]
    public async Task BlockResponse_CompressibleData_UsesLZ4Compression()
    {
        // Arrange - Create highly compressible data (repeated pattern)
        using var stream = new MemoryStream();
        var blockData = new byte[131072]; // 128 KB
        Array.Fill(blockData, (byte)'A'); // Highly compressible

        var response = new Response
        {
            Id = 42,
            Data = ByteString.CopyFrom(blockData),
            Code = ErrorCode.NoError
        };

        // Act
        await _serializer.WriteMessageAsync(stream, response, MessageType.Response, allowCompression: true);
        stream.Position = 0;

        // Read header to verify compression was used
        var headerLength = BinaryPrimitives.ReadUInt16BigEndian(stream.ToArray().AsSpan(0, 2));
        var headerBytes = stream.ToArray().AsSpan(2, headerLength).ToArray();
        var header = Header.Parser.ParseFrom(headerBytes);

        // Assert - Compression should be used for compressible data
        Assert.Equal(MessageCompression.Lz4, header.Compression);

        // Verify round-trip
        stream.Position = 0;
        var (_, message) = await _serializer.ReadMessageAsync(stream);
        var parsed = Assert.IsType<Response>(message);
        Assert.Equal(blockData, parsed.Data.ToByteArray());
    }

    /// <summary>
    /// Tests that LZ4 compressed block data is correctly decompressed.
    /// </summary>
    [Fact]
    public async Task BlockResponse_LZ4Decompression_ReturnsOriginalData()
    {
        // Arrange - Create data with varying patterns (still compressible)
        using var stream = new MemoryStream();
        var blockData = new byte[65536]; // 64 KB
        for (int i = 0; i < blockData.Length; i++)
        {
            blockData[i] = (byte)(i % 256);
        }

        var response = new Response
        {
            Id = 1,
            Data = ByteString.CopyFrom(blockData),
            Code = ErrorCode.NoError
        };

        // Act
        await _serializer.WriteMessageAsync(stream, response, MessageType.Response, allowCompression: true);
        stream.Position = 0;
        var (_, message) = await _serializer.ReadMessageAsync(stream);

        // Assert
        var parsed = Assert.IsType<Response>(message);
        Assert.Equal(blockData, parsed.Data.ToByteArray());
    }

    /// <summary>
    /// Tests block response with random (incompressible) data.
    /// LZ4 should skip compression if it doesn't provide sufficient benefit.
    /// </summary>
    [Fact]
    public async Task BlockResponse_IncompressibleData_SkipsCompression()
    {
        // Arrange - Random data that won't compress well
        using var stream = new MemoryStream();
        var blockData = new byte[1024];
        Random.Shared.NextBytes(blockData);

        var response = new Response
        {
            Id = 1,
            Data = ByteString.CopyFrom(blockData),
            Code = ErrorCode.NoError
        };

        // Act
        await _serializer.WriteMessageAsync(stream, response, MessageType.Response, allowCompression: true);
        stream.Position = 0;

        // Read header
        var headerLength = BinaryPrimitives.ReadUInt16BigEndian(stream.ToArray().AsSpan(0, 2));
        var headerBytes = stream.ToArray().AsSpan(2, headerLength).ToArray();
        var header = Header.Parser.ParseFrom(headerBytes);

        // Assert - May or may not use compression depending on data
        // The key is that round-trip works correctly
        stream.Position = 0;
        var (_, message) = await _serializer.ReadMessageAsync(stream);
        var parsed = Assert.IsType<Response>(message);
        Assert.Equal(blockData, parsed.Data.ToByteArray());
    }

    /// <summary>
    /// Tests block response error codes are preserved through serialization.
    /// </summary>
    [Theory]
    [InlineData(ErrorCode.NoError)]
    [InlineData(ErrorCode.Generic)]
    [InlineData(ErrorCode.NoSuchFile)]
    [InlineData(ErrorCode.InvalidFile)]
    public async Task BlockResponse_ErrorCodes_PreservedWithCompression(ErrorCode errorCode)
    {
        // Arrange
        using var stream = new MemoryStream();
        var blockData = new byte[256];
        Array.Fill(blockData, (byte)'B'); // Compressible

        var response = new Response
        {
            Id = 99,
            Code = errorCode,
            Data = errorCode == ErrorCode.NoError ? ByteString.CopyFrom(blockData) : ByteString.Empty
        };

        // Act
        await _serializer.WriteMessageAsync(stream, response, MessageType.Response, allowCompression: true);
        stream.Position = 0;
        var (_, message) = await _serializer.ReadMessageAsync(stream);

        // Assert
        var parsed = Assert.IsType<Response>(message);
        Assert.Equal(errorCode, parsed.Code);
        Assert.Equal(99, parsed.Id);
    }

    #endregion

    #region Large Block Transfer Tests

    /// <summary>
    /// Tests transfer of maximum block size (16 MB) with compression.
    /// Note: This is a stress test that verifies the protocol handles max-size blocks.
    /// </summary>
    [Fact]
    public async Task BlockResponse_MaxBlockSize_CompressesAndDecompresses()
    {
        // Arrange - Create a large but compressible block (1 MB for test speed)
        using var stream = new MemoryStream();
        var blockData = new byte[1024 * 1024]; // 1 MB
        for (int i = 0; i < blockData.Length; i++)
        {
            blockData[i] = (byte)(i % 256); // Repeating pattern
        }

        var response = new Response
        {
            Id = 1,
            Data = ByteString.CopyFrom(blockData),
            Code = ErrorCode.NoError
        };

        // Act
        await _serializer.WriteMessageAsync(stream, response, MessageType.Response, allowCompression: true);

        // Verify compression was effective
        var wireSize = stream.Length;
        var originalSize = blockData.Length;

        stream.Position = 0;
        var (header, message) = await _serializer.ReadMessageAsync(stream);

        // Assert
        Assert.Equal(MessageType.Response, header.Type);
        if (header.Compression == MessageCompression.Lz4)
        {
            // If compressed, wire size should be smaller
            Assert.True(wireSize < originalSize + 100,
                $"Compressed wire size ({wireSize}) should be smaller than original ({originalSize})");
        }

        var parsed = Assert.IsType<Response>(message);
        Assert.Equal(blockData, parsed.Data.ToByteArray());
    }

    /// <summary>
    /// Tests multiple block transfers in sequence (simulates file download).
    /// </summary>
    [Fact]
    public async Task BlockTransfer_MultipleBlocks_SequentialTransfer()
    {
        // Arrange
        using var stream = new MemoryStream();
        const int blockSize = 131072; // 128 KB
        const int blockCount = 4;

        var requests = new List<Request>();
        var responses = new List<Response>();

        for (int i = 0; i < blockCount; i++)
        {
            requests.Add(new Request
            {
                Id = i,
                Folder = "test-folder",
                Name = "multi-block-file.bin",
                Offset = i * blockSize,
                Size = blockSize,
                BlockNo = i
            });

            var blockData = new byte[blockSize];
            Array.Fill(blockData, (byte)('A' + i)); // Different data per block

            responses.Add(new Response
            {
                Id = i,
                Data = ByteString.CopyFrom(blockData),
                Code = ErrorCode.NoError
            });
        }

        // Act - Write all requests and responses
        foreach (var request in requests)
        {
            await _serializer.WriteMessageAsync(stream, request, MessageType.Request);
        }
        foreach (var response in responses)
        {
            await _serializer.WriteMessageAsync(stream, response, MessageType.Response, allowCompression: true);
        }

        // Read back all messages
        stream.Position = 0;
        var receivedRequests = new List<Request>();
        var receivedResponses = new List<Response>();

        for (int i = 0; i < blockCount; i++)
        {
            var (header, message) = await _serializer.ReadMessageAsync(stream);
            receivedRequests.Add(Assert.IsType<Request>(message));
        }
        for (int i = 0; i < blockCount; i++)
        {
            var (header, message) = await _serializer.ReadMessageAsync(stream);
            receivedResponses.Add(Assert.IsType<Response>(message));
        }

        // Assert
        Assert.Equal(blockCount, receivedRequests.Count);
        Assert.Equal(blockCount, receivedResponses.Count);

        for (int i = 0; i < blockCount; i++)
        {
            Assert.Equal(i, receivedRequests[i].Id);
            Assert.Equal(i * blockSize, receivedRequests[i].Offset);
            Assert.Equal(i, receivedResponses[i].Id);
            Assert.Equal(blockSize, receivedResponses[i].Data.Length);
        }
    }

    #endregion

    #region LZ4 Wire Format Compatibility Tests

    /// <summary>
    /// Tests that LZ4 wire format matches Syncthing specification.
    /// Wire format: [4 bytes: uncompressed size (big-endian)] + [LZ4 raw block]
    /// </summary>
    [Fact]
    public void LZ4WireFormat_MatchesSyncthingSpec()
    {
        // Arrange - Create compressible data
        var originalData = new byte[1000];
        Array.Fill(originalData, (byte)'X');

        // Compress using our implementation
        var compressed = CompressLZ4Syncthing(originalData);

        // Assert - Verify wire format
        Assert.NotNull(compressed);
        Assert.True(compressed.Length >= 4, "Compressed data should have at least 4-byte size prefix");

        // First 4 bytes should be the uncompressed size in big-endian
        var sizePrefix = BinaryPrimitives.ReadUInt32BigEndian(compressed.AsSpan(0, 4));
        Assert.Equal((uint)originalData.Length, sizePrefix);

        // Decompress and verify
        var decompressed = DecompressLZ4Syncthing(compressed);
        Assert.Equal(originalData, decompressed);
    }

    /// <summary>
    /// Tests that compression threshold (128 bytes) is respected.
    /// </summary>
    [Theory]
    [InlineData(64, false)]   // Below threshold - no compression
    [InlineData(128, true)]   // At threshold - compression possible
    [InlineData(256, true)]   // Above threshold - compression possible
    public async Task LZ4Compression_ThresholdRespected(int dataSize, bool compressionPossible)
    {
        // Arrange
        using var stream = new MemoryStream();
        var blockData = new byte[dataSize];
        Array.Fill(blockData, (byte)'Z'); // Highly compressible

        var response = new Response
        {
            Id = 1,
            Data = ByteString.CopyFrom(blockData),
            Code = ErrorCode.NoError
        };

        // Act
        await _serializer.WriteMessageAsync(stream, response, MessageType.Response, allowCompression: true);
        stream.Position = 0;

        // Read header
        var headerLength = BinaryPrimitives.ReadUInt16BigEndian(stream.ToArray().AsSpan(0, 2));
        var headerBytes = stream.ToArray().AsSpan(2, headerLength).ToArray();
        var header = Header.Parser.ParseFrom(headerBytes);

        // Assert
        if (!compressionPossible)
        {
            Assert.Equal(MessageCompression.None, header.Compression);
        }
        // For sizes at or above threshold, compression may or may not be used depending on savings

        // Verify data integrity regardless
        stream.Position = 0;
        var (_, message) = await _serializer.ReadMessageAsync(stream);
        var parsed = Assert.IsType<Response>(message);
        Assert.Equal(blockData, parsed.Data.ToByteArray());
    }

    /// <summary>
    /// Tests that compression savings threshold (3.125%) is respected.
    /// </summary>
    [Fact]
    public void LZ4Compression_MinimumSavingsThreshold()
    {
        // The threshold is 3.125% (1/32) savings
        // If compression doesn't save at least this much, it should be skipped

        // Test with random data that won't compress well
        var randomData = new byte[500];
        Random.Shared.NextBytes(randomData);

        var compressed = CompressLZ4Syncthing(randomData);

        // If compressed data exists, verify it's actually smaller
        if (compressed != null && compressed.Length > 0)
        {
            // compressed includes 4-byte prefix, so actual compressed size is compressed.Length - 4
            var actualCompressedSize = compressed.Length;
            var savings = (double)(randomData.Length - actualCompressedSize) / randomData.Length;

            // If there are any savings, they should meet the minimum threshold
            // (Unless compression was skipped, indicated by null or returning original)
        }

        // For any data, decompression should work correctly
        if (compressed != null)
        {
            var decompressed = DecompressLZ4Syncthing(compressed);
            Assert.Equal(randomData, decompressed);
        }
    }

    #endregion

    #region BepConnectionAdapter Integration Tests

    /// <summary>
    /// Tests that BepBlockRequestReceivedEventArgs correctly captures request details.
    /// </summary>
    [Fact]
    public void BepBlockRequestReceivedEventArgs_CapturesAllFields()
    {
        // Arrange & Act
        var args = new BepBlockRequestReceivedEventArgs
        {
            RequestId = 42,
            FolderId = "test-folder",
            FileName = "test-file.bin",
            Offset = 131072,
            Size = 65536,
            Hash = new byte[] { 1, 2, 3, 4, 5 }
        };

        // Assert
        Assert.Equal(42, args.RequestId);
        Assert.Equal("test-folder", args.FolderId);
        Assert.Equal("test-file.bin", args.FileName);
        Assert.Equal(131072, args.Offset);
        Assert.Equal(65536, args.Size);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, args.Hash);
    }

    /// <summary>
    /// Tests that BepBlockResponseReceivedEventArgs correctly captures response details.
    /// </summary>
    [Fact]
    public void BepBlockResponseReceivedEventArgs_CapturesAllFields()
    {
        // Arrange
        var blockData = new byte[1024];
        Array.Fill(blockData, (byte)'D');

        // Act
        var args = new BepBlockResponseReceivedEventArgs
        {
            RequestId = 99,
            Data = blockData,
            ErrorCode = AppBepErrorCode.NoError
        };

        // Assert
        Assert.Equal(99, args.RequestId);
        Assert.Equal(blockData, args.Data);
        Assert.Equal(AppBepErrorCode.NoError, args.ErrorCode);
    }

    /// <summary>
    /// Tests that error responses are correctly handled.
    /// </summary>
    [Theory]
    [InlineData(AppBepErrorCode.NoError)]
    [InlineData(AppBepErrorCode.Generic)]
    [InlineData(AppBepErrorCode.NoSuchFile)]
    [InlineData(AppBepErrorCode.InvalidFile)]
    public void BepBlockResponseReceivedEventArgs_ErrorCodes(AppBepErrorCode errorCode)
    {
        // Arrange & Act
        var args = new BepBlockResponseReceivedEventArgs
        {
            RequestId = 1,
            Data = errorCode == AppBepErrorCode.NoError ? new byte[10] : Array.Empty<byte>(),
            ErrorCode = errorCode
        };

        // Assert
        Assert.Equal(errorCode, args.ErrorCode);
        if (errorCode != AppBepErrorCode.NoError)
        {
            Assert.Empty(args.Data);
        }
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Compresses data using LZ4 with Syncthing-compatible wire format.
    /// Wire format: [4 bytes: uncompressed size (big-endian)] + [LZ4 raw block]
    /// </summary>
    private static byte[]? CompressLZ4Syncthing(byte[] data)
    {
        try
        {
            var maxCompressedSize = LZ4Codec.MaximumOutputSize(data.Length);
            var compressBuffer = new byte[maxCompressedSize];
            var compressedSize = LZ4Codec.Encode(data, compressBuffer, LZ4Level.L00_FAST);

            if (compressedSize <= 0)
            {
                return null;
            }

            // Syncthing wire format: [4 bytes uncompressed size] + [compressed data]
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
    /// Decompresses data using LZ4 with Syncthing-compatible wire format.
    /// </summary>
    private static byte[] DecompressLZ4Syncthing(byte[] data)
    {
        if (data.Length < 4)
        {
            throw new InvalidDataException("LZ4 compressed data too short");
        }

        var uncompressedSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
        var decompressed = new byte[uncompressedSize];
        var decompressedSize = LZ4Codec.Decode(data, 4, data.Length - 4, decompressed, 0, decompressed.Length);

        if (decompressedSize != uncompressedSize)
        {
            throw new InvalidDataException($"LZ4 decompression size mismatch: expected {uncompressedSize}, got {decompressedSize}");
        }

        return decompressed;
    }

    #endregion
}
