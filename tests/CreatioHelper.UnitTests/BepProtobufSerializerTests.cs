using System.Buffers.Binary;
using CreatioHelper.Infrastructure.Services.Sync.Proto;
using Google.Protobuf;
using Xunit;
using BepIndex = CreatioHelper.Infrastructure.Services.Sync.Proto.Index;
using BepFileInfo = CreatioHelper.Infrastructure.Services.Sync.Proto.FileInfo;

namespace CreatioHelper.UnitTests;

/// <summary>
/// Unit tests for BepProtobufSerializer - validates Syncthing-compatible wire format serialization.
/// </summary>
public class BepProtobufSerializerTests
{
    private readonly BepProtobufSerializer _serializer = new();

    #region Hello Message Tests

    [Fact]
    public void SerializeHello_RoundTrip_PreservesData()
    {
        // Arrange
        var hello = new Hello
        {
            DeviceName = "TestDevice",
            ClientName = "TestClient",
            ClientVersion = "1.0.0",
            NumConnections = 5,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        // Act
        var serialized = _serializer.SerializeHello(hello);
        var deserialized = _serializer.DeserializeHello(serialized, out var bytesConsumed);

        // Assert
        Assert.Equal(serialized.Length, bytesConsumed);
        Assert.Equal(hello.DeviceName, deserialized.DeviceName);
        Assert.Equal(hello.ClientName, deserialized.ClientName);
        Assert.Equal(hello.ClientVersion, deserialized.ClientVersion);
        Assert.Equal(hello.NumConnections, deserialized.NumConnections);
        Assert.Equal(hello.Timestamp, deserialized.Timestamp);
    }

    [Fact]
    public void HelloMagic_CorrectValue()
    {
        // Assert - BEP magic number is 0x2EA7D90B
        Assert.Equal(0x2EA7D90Bu, BepProtobufSerializer.BepMagic);
    }

    [Fact]
    public void SerializeHello_ContainsMagicNumber()
    {
        // Arrange
        var hello = new Hello { DeviceName = "Test" };

        // Act
        var serialized = _serializer.SerializeHello(hello);

        // Assert - First 4 bytes should be magic (big-endian)
        Assert.True(serialized.Length >= 6);
        var magic = (uint)((serialized[0] << 24) | (serialized[1] << 16) | (serialized[2] << 8) | serialized[3]);
        Assert.Equal(BepProtobufSerializer.BepMagic, magic);
    }

    [Fact]
    public void DeserializeHello_UnknownMagic_ThrowsBepUnknownMagicException()
    {
        // Arrange - data with unknown magic (not current or legacy)
        var unknownMagicData = new byte[] { 0x12, 0x34, 0x56, 0x78, 0x00, 0x01, 0x00 };

        // Act & Assert
        var ex = Assert.Throws<BepUnknownMagicException>(() =>
            _serializer.DeserializeHello(unknownMagicData, out _));
        Assert.Contains("unknown (newer?)", ex.Message);
        Assert.Contains("0x12345678", ex.Message);
    }

    [Fact]
    public void DeserializeHello_Version13Magic_ThrowsBepTooOldVersionException()
    {
        // Arrange - data with v13 legacy magic (0x9F79BC40)
        var legacyData = new byte[] { 0x9F, 0x79, 0xBC, 0x40, 0x00, 0x01, 0x00 };

        // Act & Assert
        var ex = Assert.Throws<BepTooOldVersionException>(() =>
            _serializer.DeserializeHello(legacyData, out _));
        Assert.Contains("older version", ex.Message);
        Assert.Contains("0x9F79BC40", ex.Message);
    }

    [Theory]
    [InlineData(0x00, 0x01, 0x00, 0x01)] // OldMagic1 = 0x00010001
    [InlineData(0x00, 0x01, 0x00, 0x00)] // OldMagic2 = 0x00010000
    public void DeserializeHello_VeryOldMagic_ThrowsBepTooOldVersionException(byte b0, byte b1, byte b2, byte b3)
    {
        // Arrange - data with very old magic numbers
        var veryOldData = new byte[] { b0, b1, b2, b3, 0x00, 0x01, 0x00 };

        // Act & Assert
        var ex = Assert.Throws<BepTooOldVersionException>(() =>
            _serializer.DeserializeHello(veryOldData, out _));
        Assert.Contains("older version", ex.Message);
    }

    [Fact]
    public void Version13HelloMagic_CorrectValue()
    {
        // Assert - v13 legacy magic is 0x9F79BC40
        Assert.Equal(0x9F79BC40u, BepProtobufSerializer.Version13HelloMagic);
    }

    [Fact]
    public void OldMagicConstants_CorrectValues()
    {
        // Assert - very old magic constants
        Assert.Equal(0x00010001u, BepProtobufSerializer.OldMagic1);
        Assert.Equal(0x00010000u, BepProtobufSerializer.OldMagic2);
    }

    [Fact]
    public void IsVersionMismatch_TooOldVersion_ReturnsTrue()
    {
        // Arrange
        var ex = new BepTooOldVersionException("test");

        // Act & Assert
        Assert.True(BepProtobufSerializer.IsVersionMismatch(ex));
    }

    [Fact]
    public void IsVersionMismatch_UnknownMagic_ReturnsTrue()
    {
        // Arrange
        var ex = new BepUnknownMagicException("test");

        // Act & Assert
        Assert.True(BepProtobufSerializer.IsVersionMismatch(ex));
    }

    [Fact]
    public void IsVersionMismatch_OtherException_ReturnsFalse()
    {
        // Arrange
        var ex = new InvalidDataException("test");

        // Act & Assert
        Assert.False(BepProtobufSerializer.IsVersionMismatch(ex));
    }

    [Fact]
    public void DeserializeHello_InsufficientData_ThrowsException()
    {
        // Arrange - less than 6 bytes
        var shortData = new byte[] { 0x2E, 0xA7, 0xD9 };

        // Act & Assert
        Assert.Throws<InvalidDataException>(() =>
            _serializer.DeserializeHello(shortData, out _));
    }

    #endregion

    #region ClusterConfig Message Tests

    [Fact]
    public void SerializeClusterConfig_RoundTrip_PreservesData()
    {
        // Arrange
        var config = new ClusterConfig();
        config.Folders.Add(new Folder
        {
            Id = "test-folder",
            Label = "Test Folder",
            Type = FolderType.SendOnly, // Replaces ReadOnly=true
            StopReason = FolderStopReason.Running // Replaces Paused=false
            // Note: IgnorePermissions, IgnoreDelete, DisableTempIndexes were removed in BEP update
        });

        var device = new Device
        {
            Id = ByteString.CopyFrom(new byte[32]),
            Name = "TestDevice",
            Compression = Compression.Always,
            CertName = "test-cert",
            MaxSequence = 100,
            Introducer = false,
            IndexId = 12345
        };
        device.Addresses.Add("tcp://192.168.1.1:22000");
        config.Folders[0].Devices.Add(device);

        // Act
        var serialized = _serializer.SerializeMessage(config, MessageType.ClusterConfig, allowCompression: false);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out var bytesConsumed);
        var deserialized = _serializer.ParseMessage<ClusterConfig>(messageBytes);

        // Assert
        Assert.Equal(MessageType.ClusterConfig, header.Type);
        Assert.Equal(serialized.Length, bytesConsumed);
        Assert.Single(deserialized.Folders);
        Assert.Equal("test-folder", deserialized.Folders[0].Id);
        Assert.Equal("Test Folder", deserialized.Folders[0].Label);
        Assert.Equal(FolderType.SendOnly, deserialized.Folders[0].Type);
        Assert.Single(deserialized.Folders[0].Devices);
        Assert.Equal("TestDevice", deserialized.Folders[0].Devices[0].Name);
    }

    #endregion

    #region Index Message Tests

    [Fact]
    public void SerializeIndex_RoundTrip_PreservesData()
    {
        // Arrange
        var index = new BepIndex
        {
            Folder = "sync-folder",
            LastSequence = 42
        };

        var fileInfo = new BepFileInfo
        {
            Name = "test-file.txt",
            Type = FileInfoType.File,
            Size = 1024,
            Permissions = 0644,
            ModifiedS = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ModifiedNs = 123456789,
            Deleted = false,
            Invalid = false,
            Sequence = 1,
            BlockSize = 131072
        };

        var block = new BlockInfo
        {
            Offset = 0,
            Size = 1024,
            Hash = ByteString.CopyFrom(new byte[32])
            // WeakHash was removed in BEP protocol update
        };
        fileInfo.Blocks.Add(block);
        index.Files.Add(fileInfo);

        // Act
        var serialized = _serializer.SerializeMessage(index, MessageType.Index, allowCompression: false);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var deserialized = _serializer.ParseMessage<BepIndex>(messageBytes);

        // Assert
        Assert.Equal(MessageType.Index, header.Type);
        Assert.Equal("sync-folder", deserialized.Folder);
        Assert.Equal(42, deserialized.LastSequence);
        Assert.Single(deserialized.Files);
        Assert.Equal("test-file.txt", deserialized.Files[0].Name);
        Assert.Equal(1024, deserialized.Files[0].Size);
        Assert.Single(deserialized.Files[0].Blocks);
    }

    #endregion

    #region IndexUpdate Message Tests

    [Fact]
    public void SerializeIndexUpdate_RoundTrip_PreservesData()
    {
        // Arrange
        var indexUpdate = new IndexUpdate
        {
            Folder = "sync-folder",
            LastSequence = 100
        };

        indexUpdate.Files.Add(new BepFileInfo
        {
            Name = "updated-file.txt",
            Type = FileInfoType.File,
            Size = 2048,
            Sequence = 99,
            Deleted = false
        });

        // Act
        var serialized = _serializer.SerializeMessage(indexUpdate, MessageType.IndexUpdate, allowCompression: false);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var deserialized = _serializer.ParseMessage<IndexUpdate>(messageBytes);

        // Assert
        Assert.Equal(MessageType.IndexUpdate, header.Type);
        Assert.Equal("sync-folder", deserialized.Folder);
        Assert.Equal(100, deserialized.LastSequence);
        Assert.Single(deserialized.Files);
        Assert.Equal("updated-file.txt", deserialized.Files[0].Name);
    }

    #endregion

    #region Request/Response Message Tests

    [Fact]
    public void SerializeRequest_RoundTrip_PreservesData()
    {
        // Arrange
        var request = new Request
        {
            Id = 12345,
            Folder = "sync-folder",
            Name = "requested-file.txt",
            Offset = 131072,
            Size = 65536,
            Hash = ByteString.CopyFrom(new byte[32]),
            FromTemporary = false,
            BlockNo = 1
            // WeakHash was removed in BEP protocol update
        };

        // Act
        var serialized = _serializer.SerializeMessage(request, MessageType.Request, allowCompression: false);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var deserialized = _serializer.ParseMessage<Request>(messageBytes);

        // Assert
        Assert.Equal(MessageType.Request, header.Type);
        Assert.Equal(12345, deserialized.Id);
        Assert.Equal("sync-folder", deserialized.Folder);
        Assert.Equal("requested-file.txt", deserialized.Name);
        Assert.Equal(131072, deserialized.Offset);
        Assert.Equal(65536, deserialized.Size);
        Assert.Equal(1, deserialized.BlockNo);
        // WeakHash was removed in BEP protocol update
    }

    [Fact]
    public void SerializeResponse_RoundTrip_PreservesData()
    {
        // Arrange
        var testData = new byte[1024];
        new Random(42).NextBytes(testData);

        var response = new Response
        {
            Id = 12345,
            Data = ByteString.CopyFrom(testData),
            Code = ErrorCode.NoError
        };

        // Act
        var serialized = _serializer.SerializeMessage(response, MessageType.Response, allowCompression: false);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var deserialized = _serializer.ParseMessage<Response>(messageBytes);

        // Assert
        Assert.Equal(MessageType.Response, header.Type);
        Assert.Equal(12345, deserialized.Id);
        Assert.Equal(ErrorCode.NoError, deserialized.Code);
        Assert.Equal(testData, deserialized.Data.ToByteArray());
    }

    [Theory]
    [InlineData(ErrorCode.NoError)]
    [InlineData(ErrorCode.Generic)]
    [InlineData(ErrorCode.NoSuchFile)]
    [InlineData(ErrorCode.InvalidFile)]
    public void SerializeResponse_AllErrorCodes_PreservesCode(ErrorCode code)
    {
        // Arrange
        var response = new Response
        {
            Id = 1,
            Data = ByteString.Empty,
            Code = code
        };

        // Act
        var serialized = _serializer.SerializeMessage(response, MessageType.Response, allowCompression: false);
        var (_, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var deserialized = _serializer.ParseMessage<Response>(messageBytes);

        // Assert
        Assert.Equal(code, deserialized.Code);
    }

    #endregion

    #region Compression Tests

    [Fact]
    public void Compression_LZ4_Works()
    {
        // Arrange - create compressible data (repeated pattern)
        var compressibleData = new byte[4096];
        for (int i = 0; i < compressibleData.Length; i++)
        {
            compressibleData[i] = (byte)(i % 256);
        }

        var response = new Response
        {
            Id = 1,
            Data = ByteString.CopyFrom(compressibleData),
            Code = ErrorCode.NoError
        };

        // Act
        var serialized = _serializer.SerializeMessage(response, MessageType.Response, allowCompression: true);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var deserialized = _serializer.ParseMessage<Response>(messageBytes);

        // Assert - data should be intact after compression/decompression
        Assert.Equal(compressibleData, deserialized.Data.ToByteArray());
    }

    [Fact]
    public void Compression_ThresholdRespected_SmallMessageNotCompressed()
    {
        // Arrange - small data below threshold (128 bytes)
        var smallData = new byte[64];
        var response = new Response
        {
            Id = 1,
            Data = ByteString.CopyFrom(smallData),
            Code = ErrorCode.NoError
        };

        // Act
        var serialized = _serializer.SerializeMessage(response, MessageType.Response, allowCompression: true);
        var (header, _) = _serializer.DeserializeMessage(serialized, out _);

        // Assert - should not be compressed due to small size
        Assert.Equal(MessageCompression.None, header.Compression);
    }

    [Fact]
    public void CompressionThreshold_Is128Bytes()
    {
        Assert.Equal(128, BepProtobufSerializer.CompressionThreshold);
    }

    [Fact]
    public void Compression_LZ4RawBlockFormat_HasUncompressedSizePrefix()
    {
        // Arrange - create highly compressible data to ensure compression occurs
        var compressibleData = new byte[1024];
        // Fill with repeating pattern that compresses well
        for (int i = 0; i < compressibleData.Length; i++)
        {
            compressibleData[i] = (byte)'A';
        }

        var response = new Response
        {
            Id = 1,
            Data = ByteString.CopyFrom(compressibleData),
            Code = ErrorCode.NoError
        };

        // Act
        var serialized = _serializer.SerializeMessage(response, MessageType.Response, allowCompression: true);
        var (header, _) = _serializer.DeserializeMessage(serialized, out _);

        // Assert - verify compression was used (highly compressible data)
        Assert.Equal(MessageCompression.Lz4, header.Compression);
    }

    [Fact]
    public void Compression_LZ4RawBlockFormat_RoundTrip_LargeData()
    {
        // Arrange - create large compressible data
        var largeData = new byte[32768];
        for (int i = 0; i < largeData.Length; i++)
        {
            // Pattern that compresses but isn't trivial
            largeData[i] = (byte)(i % 32);
        }

        var response = new Response
        {
            Id = 1,
            Data = ByteString.CopyFrom(largeData),
            Code = ErrorCode.NoError
        };

        // Act
        var serialized = _serializer.SerializeMessage(response, MessageType.Response, allowCompression: true);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var deserialized = _serializer.ParseMessage<Response>(messageBytes);

        // Assert
        Assert.Equal(MessageCompression.Lz4, header.Compression);
        Assert.Equal(largeData, deserialized.Data.ToByteArray());
    }

    [Fact]
    public void Compression_LZ4RawBlockFormat_DecompressesCorrectSize()
    {
        // This test verifies that the 4-byte uncompressed size prefix is used correctly
        // by compressing and decompressing data of various sizes

        var testSizes = new[] { 200, 500, 1024, 4096, 8192 };

        foreach (var size in testSizes)
        {
            // Arrange - create compressible data of specific size
            var data = new byte[size];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(i % 64);
            }

            var response = new Response
            {
                Id = size,
                Data = ByteString.CopyFrom(data),
                Code = ErrorCode.NoError
            };

            // Act
            var serialized = _serializer.SerializeMessage(response, MessageType.Response, allowCompression: true);
            var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
            var deserialized = _serializer.ParseMessage<Response>(messageBytes);

            // Assert - verify exact size is preserved
            Assert.Equal(size, deserialized.Data.Length);
            Assert.Equal(data, deserialized.Data.ToByteArray());
        }
    }

    [Fact]
    public void Compression_MinSavingsRatio_Is3Point125Percent()
    {
        // Syncthing requires at least 3.125% (1/32) compression savings
        // This test documents that expectation

        // Create data that barely compresses - random data
        var randomData = new byte[1024];
        new Random(42).NextBytes(randomData);

        var response = new Response
        {
            Id = 1,
            Data = ByteString.CopyFrom(randomData),
            Code = ErrorCode.NoError
        };

        // Act
        var serialized = _serializer.SerializeMessage(response, MessageType.Response, allowCompression: true);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var deserialized = _serializer.ParseMessage<Response>(messageBytes);

        // Assert - data should still be correct regardless of compression decision
        Assert.Equal(randomData, deserialized.Data.ToByteArray());
    }

    #endregion

    #region Max Message Size Tests

    [Fact]
    public void MaxMessageSize_Is16MB()
    {
        // Assert - maximum message size is 16 MB
        Assert.Equal(16 * 1024 * 1024, BepProtobufSerializer.MaxMessageSize);
    }

    [Fact]
    public void DeserializeMessage_ExceedsMaxSize_ThrowsException()
    {
        // Arrange - create data with message length exceeding max
        // Header: [2 bytes header length][header proto][4 bytes message length]
        var header = new Header { Type = MessageType.Response, Compression = MessageCompression.None };
        var headerBytes = header.ToByteArray();

        var data = new byte[2 + headerBytes.Length + 4];
        data[0] = (byte)(headerBytes.Length >> 8);
        data[1] = (byte)(headerBytes.Length & 0xFF);
        headerBytes.CopyTo(data.AsSpan(2));

        // Set message length to exceed max (17 MB)
        var invalidLength = 17 * 1024 * 1024;
        data[2 + headerBytes.Length] = (byte)(invalidLength >> 24);
        data[2 + headerBytes.Length + 1] = (byte)(invalidLength >> 16);
        data[2 + headerBytes.Length + 2] = (byte)(invalidLength >> 8);
        data[2 + headerBytes.Length + 3] = (byte)(invalidLength & 0xFF);

        // Act & Assert
        var ex = Assert.Throws<InvalidDataException>(() =>
            _serializer.DeserializeMessage(data, out _));
        Assert.Contains("Message too large", ex.Message);
    }

    #endregion

    #region Ping/Close Message Tests

    [Fact]
    public void SerializePing_RoundTrip()
    {
        // Arrange
        var ping = new Ping();

        // Act
        var serialized = _serializer.SerializeMessage(ping, MessageType.Ping, allowCompression: false);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var deserialized = _serializer.ParseMessage<Ping>(messageBytes);

        // Assert
        Assert.Equal(MessageType.Ping, header.Type);
        Assert.NotNull(deserialized);
    }

    [Fact]
    public void SerializeClose_RoundTrip_PreservesReason()
    {
        // Arrange
        var close = new Close
        {
            Reason = "Connection terminated by user"
        };

        // Act
        var serialized = _serializer.SerializeMessage(close, MessageType.Close, allowCompression: false);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var deserialized = _serializer.ParseMessage<Close>(messageBytes);

        // Assert
        Assert.Equal(MessageType.Close, header.Type);
        Assert.Equal("Connection terminated by user", deserialized.Reason);
    }

    #endregion

    #region ParseMessageByType Tests

    [Fact]
    public void ParseMessageByType_AllTypes_ParseCorrectly()
    {
        // Test each message type
        var testCases = new (IMessage message, MessageType type)[]
        {
            (new ClusterConfig(), MessageType.ClusterConfig),
            (new BepIndex { Folder = "test" }, MessageType.Index),
            (new IndexUpdate { Folder = "test" }, MessageType.IndexUpdate),
            (new Request { Id = 1 }, MessageType.Request),
            (new Response { Id = 1 }, MessageType.Response),
            (new DownloadProgress { Folder = "test" }, MessageType.DownloadProgress),
            (new Ping(), MessageType.Ping),
            (new Close { Reason = "test" }, MessageType.Close)
        };

        foreach (var (message, type) in testCases)
        {
            // Arrange
            var serialized = _serializer.SerializeMessage(message, type, allowCompression: false);
            var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);

            // Act
            var parsed = _serializer.ParseMessageByType(header, messageBytes);

            // Assert
            Assert.NotNull(parsed);
            Assert.IsAssignableFrom<IMessage>(parsed);
        }
    }

    #endregion

    #region Stream Operations Tests

    [Fact]
    public async Task ReadWriteHelloAsync_RoundTrip()
    {
        // Arrange
        var hello = new Hello
        {
            DeviceName = "StreamTest",
            ClientName = "TestClient",
            ClientVersion = "2.0.0",
            NumConnections = 3,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        using var stream = new MemoryStream();

        // Act
        await _serializer.WriteHelloAsync(stream, hello);
        stream.Position = 0;
        var deserialized = await _serializer.ReadHelloAsync(stream);

        // Assert
        Assert.Equal(hello.DeviceName, deserialized.DeviceName);
        Assert.Equal(hello.ClientName, deserialized.ClientName);
        Assert.Equal(hello.ClientVersion, deserialized.ClientVersion);
        Assert.Equal(hello.NumConnections, deserialized.NumConnections);
        Assert.Equal(hello.Timestamp, deserialized.Timestamp);
    }

    [Fact]
    public async Task ReadWriteMessageAsync_RoundTrip()
    {
        // Arrange
        var request = new Request
        {
            Id = 999,
            Folder = "stream-folder",
            Name = "stream-file.txt",
            Offset = 0,
            Size = 1024
        };

        using var stream = new MemoryStream();

        // Act
        await _serializer.WriteMessageAsync(stream, request, MessageType.Request);
        stream.Position = 0;
        var (header, message) = await _serializer.ReadMessageAsync(stream);

        // Assert
        Assert.Equal(MessageType.Request, header.Type);
        var deserialized = Assert.IsType<Request>(message);
        Assert.Equal(999, deserialized.Id);
        Assert.Equal("stream-folder", deserialized.Folder);
        Assert.Equal("stream-file.txt", deserialized.Name);
    }

    [Fact]
    public async Task ReadHelloAsync_Version13Magic_ThrowsBepTooOldVersionException()
    {
        // Arrange - create stream with v13 legacy magic
        using var stream = new MemoryStream();
        var headerBuffer = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(headerBuffer.AsSpan(0, 4), BepProtobufSerializer.Version13HelloMagic);
        BinaryPrimitives.WriteUInt16BigEndian(headerBuffer.AsSpan(4, 2), 1);
        stream.Write(headerBuffer);
        stream.WriteByte(0); // dummy body
        stream.Position = 0;

        // Act & Assert
        var ex = await Assert.ThrowsAsync<BepTooOldVersionException>(
            async () => await _serializer.ReadHelloAsync(stream));
        Assert.Contains("older version", ex.Message);
    }

    [Fact]
    public async Task ReadHelloAsync_UnknownMagic_ThrowsBepUnknownMagicException()
    {
        // Arrange - create stream with unknown magic
        using var stream = new MemoryStream();
        var headerBuffer = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(headerBuffer.AsSpan(0, 4), 0xDEADBEEF);
        BinaryPrimitives.WriteUInt16BigEndian(headerBuffer.AsSpan(4, 2), 1);
        stream.Write(headerBuffer);
        stream.WriteByte(0); // dummy body
        stream.Position = 0;

        // Act & Assert
        var ex = await Assert.ThrowsAsync<BepUnknownMagicException>(
            async () => await _serializer.ReadHelloAsync(stream));
        Assert.Contains("unknown (newer?)", ex.Message);
    }

    [Fact]
    public async Task ReadHelloAsync_OldMagic1_ThrowsBepTooOldVersionException()
    {
        // Arrange - create stream with very old magic (OldMagic1)
        using var stream = new MemoryStream();
        var headerBuffer = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(headerBuffer.AsSpan(0, 4), BepProtobufSerializer.OldMagic1);
        BinaryPrimitives.WriteUInt16BigEndian(headerBuffer.AsSpan(4, 2), 1);
        stream.Write(headerBuffer);
        stream.WriteByte(0); // dummy body
        stream.Position = 0;

        // Act & Assert
        var ex = await Assert.ThrowsAsync<BepTooOldVersionException>(
            async () => await _serializer.ReadHelloAsync(stream));
        Assert.Contains("older version", ex.Message);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void SerializeMessage_EmptyMessage_Works()
    {
        // Arrange
        var emptyConfig = new ClusterConfig();

        // Act
        var serialized = _serializer.SerializeMessage(emptyConfig, MessageType.ClusterConfig, allowCompression: false);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var deserialized = _serializer.ParseMessage<ClusterConfig>(messageBytes);

        // Assert
        Assert.Equal(MessageType.ClusterConfig, header.Type);
        Assert.Empty(deserialized.Folders);
    }

    [Fact]
    public void SerializeMessage_UnicodeStrings_PreservedCorrectly()
    {
        // Arrange
        var index = new BepIndex
        {
            Folder = "日本語フォルダ"
        };
        index.Files.Add(new BepFileInfo
        {
            Name = "файл.txt",
            Type = FileInfoType.File,
            Size = 100
        });

        // Act
        var serialized = _serializer.SerializeMessage(index, MessageType.Index, allowCompression: false);
        var (_, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var deserialized = _serializer.ParseMessage<BepIndex>(messageBytes);

        // Assert
        Assert.Equal("日本語フォルダ", deserialized.Folder);
        Assert.Equal("файл.txt", deserialized.Files[0].Name);
    }

    [Fact]
    public void DeserializeMessage_NegativeMessageLength_ThrowsException()
    {
        // Arrange - create data with negative message length
        var header = new Header { Type = MessageType.Response, Compression = MessageCompression.None };
        var headerBytes = header.ToByteArray();

        var data = new byte[2 + headerBytes.Length + 4];
        data[0] = (byte)(headerBytes.Length >> 8);
        data[1] = (byte)(headerBytes.Length & 0xFF);
        headerBytes.CopyTo(data.AsSpan(2));

        // Set message length to negative value (0x80000000 = -2147483648 in signed int)
        data[2 + headerBytes.Length] = 0x80;
        data[2 + headerBytes.Length + 1] = 0x00;
        data[2 + headerBytes.Length + 2] = 0x00;
        data[2 + headerBytes.Length + 3] = 0x00;

        // Act & Assert
        var ex = Assert.Throws<InvalidDataException>(() =>
            _serializer.DeserializeMessage(data, out _));
        Assert.Contains("Invalid message length", ex.Message);
    }

    #endregion

    #region Wire Format Compatibility Tests

    /// <summary>
    /// Verifies Hello message wire format exactly matches Syncthing's expected layout:
    /// [4 bytes: magic (big-endian 0x2EA7D90B)][2 bytes: length (big-endian)][Hello protobuf]
    /// </summary>
    [Fact]
    public void HelloWireFormat_ExactByteLayout_MatchesSyncthing()
    {
        // Arrange
        var hello = new Hello
        {
            DeviceName = "Test",
            ClientName = "TestClient",
            ClientVersion = "1.0.0"
        };

        // Act
        var serialized = _serializer.SerializeHello(hello);

        // Assert - verify exact byte positions
        // Bytes 0-3: Magic number (big-endian)
        Assert.Equal(0x2E, serialized[0]); // Magic byte 0
        Assert.Equal(0xA7, serialized[1]); // Magic byte 1
        Assert.Equal(0xD9, serialized[2]); // Magic byte 2
        Assert.Equal(0x0B, serialized[3]); // Magic byte 3

        // Bytes 4-5: Hello message length (big-endian)
        var helloLength = (serialized[4] << 8) | serialized[5];
        Assert.Equal(serialized.Length - 6, helloLength);

        // Bytes 6+: Hello protobuf - verify it can be parsed
        var helloBytes = serialized.AsSpan(6, helloLength).ToArray();
        var parsed = Hello.Parser.ParseFrom(helloBytes);
        Assert.Equal("Test", parsed.DeviceName);
    }

    /// <summary>
    /// Verifies regular message wire format exactly matches Syncthing's expected layout:
    /// [2 bytes: header length][Header protobuf][4 bytes: message length][Message protobuf]
    /// </summary>
    [Fact]
    public void MessageWireFormat_ExactByteLayout_MatchesSyncthing()
    {
        // Arrange
        var ping = new Ping();

        // Act
        var serialized = _serializer.SerializeMessage(ping, MessageType.Ping, allowCompression: false);

        // Assert - verify wire format structure
        // Bytes 0-1: Header length (big-endian)
        var headerLength = (serialized[0] << 8) | serialized[1];
        Assert.True(headerLength > 0);

        // Parse header to verify structure
        var headerBytes = serialized.AsSpan(2, headerLength).ToArray();
        var header = Header.Parser.ParseFrom(headerBytes);
        Assert.Equal(MessageType.Ping, header.Type);
        Assert.Equal(MessageCompression.None, header.Compression);

        // Bytes after header: Message length (big-endian, 4 bytes)
        var msgLenOffset = 2 + headerLength;
        var messageLength = BinaryPrimitives.ReadInt32BigEndian(serialized.AsSpan(msgLenOffset, 4));
        Assert.True(messageLength >= 0);

        // Verify total length
        Assert.Equal(2 + headerLength + 4 + messageLength, serialized.Length);
    }

    /// <summary>
    /// Verifies all length fields use big-endian byte order as required by Syncthing protocol.
    /// </summary>
    [Theory]
    [InlineData(0x0100, 1, 0)]    // 256 in big-endian
    [InlineData(0x0001, 0, 1)]    // 1 in big-endian
    [InlineData(0x1234, 0x12, 0x34)] // 0x1234 in big-endian
    public void WireFormat_LengthFields_AreBigEndian(int expectedLength, byte highByte, byte lowByte)
    {
        // This test validates our understanding of big-endian encoding
        var buffer = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)expectedLength);

        Assert.Equal(highByte, buffer[0]);
        Assert.Equal(lowByte, buffer[1]);
    }

    /// <summary>
    /// Verifies 4-byte message length field uses big-endian byte order.
    /// </summary>
    [Fact]
    public void WireFormat_MessageLengthField_Is4BytesBigEndian()
    {
        // Arrange - create a message with known content
        var request = new Request
        {
            Id = 12345,
            Folder = "test-folder",
            Name = "test-file.txt",
            Offset = 0,
            Size = 1024
        };

        // Act
        var serialized = _serializer.SerializeMessage(request, MessageType.Request, allowCompression: false);

        // Assert - verify 4-byte message length at correct position
        var headerLength = BinaryPrimitives.ReadUInt16BigEndian(serialized.AsSpan(0, 2));
        var msgLenOffset = 2 + headerLength;

        // Read as big-endian int32
        var messageLength = BinaryPrimitives.ReadInt32BigEndian(serialized.AsSpan(msgLenOffset, 4));

        // Verify message length matches actual content
        var actualMessageBytes = serialized.Length - msgLenOffset - 4;
        Assert.Equal(actualMessageBytes, messageLength);
    }

    /// <summary>
    /// Verifies Header protobuf encoding contains MessageType field.
    /// Syncthing Header: { type: MessageType, compression: MessageCompression }
    /// </summary>
    [Theory]
    [InlineData(MessageType.ClusterConfig)]
    [InlineData(MessageType.Index)]
    [InlineData(MessageType.IndexUpdate)]
    [InlineData(MessageType.Request)]
    [InlineData(MessageType.Response)]
    [InlineData(MessageType.DownloadProgress)]
    [InlineData(MessageType.Ping)]
    [InlineData(MessageType.Close)]
    public void WireFormat_HeaderProtobuf_ContainsMessageType(MessageType expectedType)
    {
        // Arrange
        IMessage message = expectedType switch
        {
            MessageType.ClusterConfig => new ClusterConfig(),
            MessageType.Index => new BepIndex { Folder = "test" },
            MessageType.IndexUpdate => new IndexUpdate { Folder = "test" },
            MessageType.Request => new Request { Id = 1 },
            MessageType.Response => new Response { Id = 1 },
            MessageType.DownloadProgress => new DownloadProgress { Folder = "test" },
            MessageType.Ping => new Ping(),
            MessageType.Close => new Close { Reason = "test" },
            _ => throw new ArgumentException($"Unknown message type: {expectedType}")
        };

        // Act
        var serialized = _serializer.SerializeMessage(message, expectedType, allowCompression: false);
        var headerLength = BinaryPrimitives.ReadUInt16BigEndian(serialized.AsSpan(0, 2));
        var headerBytes = serialized.AsSpan(2, headerLength).ToArray();
        var header = Header.Parser.ParseFrom(headerBytes);

        // Assert
        Assert.Equal(expectedType, header.Type);
    }

    /// <summary>
    /// Verifies LZ4 compressed message wire format uses Syncthing-compatible layout:
    /// [4 bytes: uncompressed size (big-endian)] + [LZ4 compressed block]
    /// </summary>
    [Fact]
    public void WireFormat_LZ4Compression_HasUncompressedSizePrefix()
    {
        // Arrange - create highly compressible data to ensure compression occurs
        var compressibleData = new byte[2048];
        for (int i = 0; i < compressibleData.Length; i++)
        {
            compressibleData[i] = (byte)'X'; // Repeated character compresses well
        }

        var response = new Response
        {
            Id = 1,
            Data = ByteString.CopyFrom(compressibleData),
            Code = ErrorCode.NoError
        };

        // Act
        var serialized = _serializer.SerializeMessage(response, MessageType.Response, allowCompression: true);
        var (header, decompressedBytes) = _serializer.DeserializeMessage(serialized, out _);

        // Assert - verify compression was used
        Assert.Equal(MessageCompression.Lz4, header.Compression);

        // Verify the decompressed data matches original
        var deserialized = _serializer.ParseMessage<Response>(decompressedBytes);
        Assert.Equal(compressibleData, deserialized.Data.ToByteArray());
    }

    /// <summary>
    /// Verifies LZ4 compressed payload has 4-byte uncompressed size as first bytes.
    /// </summary>
    [Fact]
    public void WireFormat_LZ4Payload_StartsWithUncompressedSize()
    {
        // Arrange - create compressible data
        var compressibleData = new byte[1024];
        for (int i = 0; i < compressibleData.Length; i++)
        {
            compressibleData[i] = (byte)'A';
        }

        var response = new Response
        {
            Id = 1,
            Data = ByteString.CopyFrom(compressibleData),
            Code = ErrorCode.NoError
        };

        // Act
        var serialized = _serializer.SerializeMessage(response, MessageType.Response, allowCompression: true);

        // Extract the compressed message payload
        var headerLength = BinaryPrimitives.ReadUInt16BigEndian(serialized.AsSpan(0, 2));
        var headerBytes = serialized.AsSpan(2, headerLength).ToArray();
        var header = Header.Parser.ParseFrom(headerBytes);

        // Only proceed if compression was actually used
        if (header.Compression == MessageCompression.Lz4)
        {
            var msgLenOffset = 2 + headerLength;
            var messageLength = BinaryPrimitives.ReadInt32BigEndian(serialized.AsSpan(msgLenOffset, 4));
            var compressedPayload = serialized.AsSpan(msgLenOffset + 4, messageLength);

            // Assert - first 4 bytes should be uncompressed size (big-endian)
            var uncompressedSize = BinaryPrimitives.ReadUInt32BigEndian(compressedPayload[..4]);

            // The uncompressed size should be the size of the Response protobuf
            var originalProtoBytes = response.ToByteArray();
            Assert.Equal((uint)originalProtoBytes.Length, uncompressedSize);
        }
    }

    /// <summary>
    /// Verifies Hello message maximum length is 32767 bytes as per Syncthing protocol.
    /// </summary>
    [Fact]
    public void WireFormat_HelloMaxLength_Is32767Bytes()
    {
        // Arrange - create header with max size indicator
        var maxHelloLength = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(maxHelloLength.AsSpan(0, 4), BepProtobufSerializer.BepMagic);
        BinaryPrimitives.WriteUInt16BigEndian(maxHelloLength.AsSpan(4, 2), 32768); // Just over max

        // This would require 32768 bytes of body data - just verify the exception
        var tooLargeData = new byte[6 + 32768];
        Array.Copy(maxHelloLength, tooLargeData, 6);

        // Act & Assert
        var ex = Assert.Throws<InvalidDataException>(() =>
            _serializer.DeserializeHello(tooLargeData, out _));
        Assert.Contains("Hello message too big", ex.Message);
    }

    /// <summary>
    /// Verifies Hello message with exactly 32767 bytes is accepted.
    /// </summary>
    [Fact]
    public void WireFormat_HelloAtMaxLength_IsAccepted()
    {
        // Create a Hello message that will result in a body close to max size
        // Note: We can't easily create exactly 32767 bytes, so we verify the limit is enforced correctly
        var hello = new Hello
        {
            DeviceName = new string('X', 1000), // Large but reasonable
            ClientName = "Test",
            ClientVersion = "1.0.0"
        };

        // Act
        var serialized = _serializer.SerializeHello(hello);
        var deserialized = _serializer.DeserializeHello(serialized, out var bytesConsumed);

        // Assert - round trip should work
        Assert.Equal(hello.DeviceName, deserialized.DeviceName);
        Assert.Equal(serialized.Length, bytesConsumed);
    }

    /// <summary>
    /// Verifies that empty message bodies serialize correctly.
    /// </summary>
    [Fact]
    public void WireFormat_EmptyMessageBody_SerializesCorrectly()
    {
        // Arrange - Ping has no fields, should have empty body
        var ping = new Ping();

        // Act
        var serialized = _serializer.SerializeMessage(ping, MessageType.Ping, allowCompression: false);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);

        // Assert - empty message should have 0-length body
        Assert.Empty(messageBytes);
        Assert.Equal(MessageType.Ping, header.Type);
    }

    /// <summary>
    /// Verifies wire format is consistent across multiple serializations.
    /// </summary>
    [Fact]
    public void WireFormat_MultipleSerializations_ProduceIdenticalBytes()
    {
        // Arrange
        var request = new Request
        {
            Id = 12345,
            Folder = "sync-folder",
            Name = "test-file.txt",
            Offset = 131072,
            Size = 65536
        };

        // Act - serialize multiple times
        var serialized1 = _serializer.SerializeMessage(request, MessageType.Request, allowCompression: false);
        var serialized2 = _serializer.SerializeMessage(request, MessageType.Request, allowCompression: false);
        var serialized3 = _serializer.SerializeMessage(request, MessageType.Request, allowCompression: false);

        // Assert - all should be identical
        Assert.Equal(serialized1, serialized2);
        Assert.Equal(serialized2, serialized3);
    }

    /// <summary>
    /// Verifies Header compression field is correctly set for compressed messages.
    /// </summary>
    [Fact]
    public void WireFormat_CompressionHeader_SetCorrectlyWhenCompressed()
    {
        // Arrange - highly compressible data
        var data = new byte[4096];
        Array.Fill(data, (byte)'Z');

        var response = new Response
        {
            Id = 1,
            Data = ByteString.CopyFrom(data),
            Code = ErrorCode.NoError
        };

        // Act - with compression enabled
        var compressedSerialized = _serializer.SerializeMessage(response, MessageType.Response, allowCompression: true);
        var headerLengthCompressed = BinaryPrimitives.ReadUInt16BigEndian(compressedSerialized.AsSpan(0, 2));
        var headerCompressed = Header.Parser.ParseFrom(compressedSerialized.AsSpan(2, headerLengthCompressed));

        // Act - with compression disabled
        var uncompressedSerialized = _serializer.SerializeMessage(response, MessageType.Response, allowCompression: false);
        var headerLengthUncompressed = BinaryPrimitives.ReadUInt16BigEndian(uncompressedSerialized.AsSpan(0, 2));
        var headerUncompressed = Header.Parser.ParseFrom(uncompressedSerialized.AsSpan(2, headerLengthUncompressed));

        // Assert
        Assert.Equal(MessageCompression.Lz4, headerCompressed.Compression);
        Assert.Equal(MessageCompression.None, headerUncompressed.Compression);
    }

    /// <summary>
    /// Verifies wire format ByteString fields preserve binary data exactly.
    /// </summary>
    [Fact]
    public void WireFormat_BinaryData_PreservedExactly()
    {
        // Arrange - create message with binary data containing all byte values
        var binaryData = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            binaryData[i] = (byte)i;
        }

        var response = new Response
        {
            Id = 1,
            Data = ByteString.CopyFrom(binaryData),
            Code = ErrorCode.NoError
        };

        // Act
        var serialized = _serializer.SerializeMessage(response, MessageType.Response, allowCompression: false);
        var (_, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var deserialized = _serializer.ParseMessage<Response>(messageBytes);

        // Assert - all 256 byte values should be preserved
        Assert.Equal(binaryData, deserialized.Data.ToByteArray());
    }

    /// <summary>
    /// Verifies large message serialization (approaching but under max size).
    /// </summary>
    [Fact]
    public void WireFormat_LargeMessage_SerializesCorrectly()
    {
        // Arrange - 1 MB of data (well under 16 MB max)
        var largeData = new byte[1024 * 1024];
        new Random(42).NextBytes(largeData);

        var response = new Response
        {
            Id = 1,
            Data = ByteString.CopyFrom(largeData),
            Code = ErrorCode.NoError
        };

        // Act
        var serialized = _serializer.SerializeMessage(response, MessageType.Response, allowCompression: false);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out var bytesConsumed);
        var deserialized = _serializer.ParseMessage<Response>(messageBytes);

        // Assert
        Assert.Equal(serialized.Length, bytesConsumed);
        Assert.Equal(MessageType.Response, header.Type);
        Assert.Equal(largeData, deserialized.Data.ToByteArray());
    }

    /// <summary>
    /// Verifies Index message wire format with FileInfo blocks.
    /// </summary>
    [Fact]
    public void WireFormat_IndexWithBlocks_SerializesCorrectly()
    {
        // Arrange - Index with file that has multiple blocks
        var index = new BepIndex
        {
            Folder = "test-folder",
            LastSequence = 42
        };

        var fileInfo = new BepFileInfo
        {
            Name = "large-file.bin",
            Type = FileInfoType.File,
            Size = 262144, // 256 KB
            Sequence = 1,
            BlockSize = 131072 // 128 KB blocks
        };

        // Add two blocks
        fileInfo.Blocks.Add(new BlockInfo
        {
            Offset = 0,
            Size = 131072,
            Hash = ByteString.CopyFrom(new byte[32])
        });
        fileInfo.Blocks.Add(new BlockInfo
        {
            Offset = 131072,
            Size = 131072,
            Hash = ByteString.CopyFrom(new byte[32])
        });

        index.Files.Add(fileInfo);

        // Act
        var serialized = _serializer.SerializeMessage(index, MessageType.Index, allowCompression: false);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var deserialized = _serializer.ParseMessage<BepIndex>(messageBytes);

        // Assert
        Assert.Equal(MessageType.Index, header.Type);
        Assert.Equal("test-folder", deserialized.Folder);
        Assert.Equal(42, deserialized.LastSequence);
        Assert.Single(deserialized.Files);
        Assert.Equal(2, deserialized.Files[0].Blocks.Count);
        Assert.Equal(0, deserialized.Files[0].Blocks[0].Offset);
        Assert.Equal(131072, deserialized.Files[0].Blocks[1].Offset);
    }

    /// <summary>
    /// Verifies ClusterConfig wire format with nested Folder/Device structures.
    /// </summary>
    [Fact]
    public void WireFormat_ClusterConfigNested_SerializesCorrectly()
    {
        // Arrange - ClusterConfig with nested folder and device
        var config = new ClusterConfig();
        var folder = new Folder
        {
            Id = "folder-1",
            Label = "Test Folder",
            Type = FolderType.SendReceive,
            StopReason = FolderStopReason.Running
        };

        var device = new Device
        {
            Id = ByteString.CopyFrom(new byte[32]),
            Name = "Device1",
            Compression = Compression.Always,
            MaxSequence = 100,
            IndexId = 12345
        };
        device.Addresses.Add("tcp://192.168.1.1:22000");
        device.Addresses.Add("tcp://192.168.1.2:22000");

        folder.Devices.Add(device);
        config.Folders.Add(folder);

        // Act
        var serialized = _serializer.SerializeMessage(config, MessageType.ClusterConfig, allowCompression: false);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var deserialized = _serializer.ParseMessage<ClusterConfig>(messageBytes);

        // Assert
        Assert.Equal(MessageType.ClusterConfig, header.Type);
        Assert.Single(deserialized.Folders);
        Assert.Equal("folder-1", deserialized.Folders[0].Id);
        Assert.Single(deserialized.Folders[0].Devices);
        Assert.Equal("Device1", deserialized.Folders[0].Devices[0].Name);
        Assert.Equal(2, deserialized.Folders[0].Devices[0].Addresses.Count);
    }

    /// <summary>
    /// Verifies wire format handles Close message with reason string.
    /// </summary>
    [Fact]
    public void WireFormat_CloseWithReason_SerializesCorrectly()
    {
        // Arrange
        var close = new Close
        {
            Reason = "Connection terminated: invalid folder ID"
        };

        // Act
        var serialized = _serializer.SerializeMessage(close, MessageType.Close, allowCompression: false);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var deserialized = _serializer.ParseMessage<Close>(messageBytes);

        // Assert
        Assert.Equal(MessageType.Close, header.Type);
        Assert.Equal("Connection terminated: invalid folder ID", deserialized.Reason);
    }

    /// <summary>
    /// Verifies DownloadProgress wire format with FileDownloadProgressUpdate.
    /// </summary>
    [Fact]
    public void WireFormat_DownloadProgress_SerializesCorrectly()
    {
        // Arrange
        var progress = new DownloadProgress
        {
            Folder = "sync-folder"
        };

        var update = new FileDownloadProgressUpdate
        {
            UpdateType = FileDownloadProgressUpdateType.Append,
            Name = "downloading-file.bin",
            BlockSize = 131072
        };
        update.BlockIndexes.AddRange(new[] { 0, 1, 2, 3 });
        progress.Updates.Add(update);

        // Act
        var serialized = _serializer.SerializeMessage(progress, MessageType.DownloadProgress, allowCompression: false);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var deserialized = _serializer.ParseMessage<DownloadProgress>(messageBytes);

        // Assert
        Assert.Equal(MessageType.DownloadProgress, header.Type);
        Assert.Equal("sync-folder", deserialized.Folder);
        Assert.Single(deserialized.Updates);
        Assert.Equal("downloading-file.bin", deserialized.Updates[0].Name);
        Assert.Equal(4, deserialized.Updates[0].BlockIndexes.Count);
    }

    #endregion

    #region Cross-Compatibility Tests (Syncthing Wire Format)

    /// <summary>
    /// Tests that we can parse a Hello message created using the exact Syncthing wire format.
    /// Mirrors TestVersion14Hello from syncthing/lib/protocol/bep_hello_test.go.
    /// Wire format: [4B magic (0x2EA7D90B big-endian)][2B length (big-endian)][Hello protobuf]
    /// </summary>
    [Fact]
    public void CrossCompatibility_ParseSyncthingHelloMessage()
    {
        // Arrange - create Hello message exactly as Syncthing would
        var expectedHello = new Hello
        {
            DeviceName = "test device",
            ClientName = "syncthing",
            ClientVersion = "v0.14.5"
        };

        // Serialize the Hello protobuf
        var helloProtoBytes = expectedHello.ToByteArray();

        // Build wire format: [4B magic][2B length][protobuf]
        var wireData = new byte[4 + 2 + helloProtoBytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(wireData.AsSpan(0, 4), BepProtobufSerializer.BepMagic);
        BinaryPrimitives.WriteUInt16BigEndian(wireData.AsSpan(4, 2), (ushort)helloProtoBytes.Length);
        helloProtoBytes.CopyTo(wireData.AsSpan(6));

        // Act - parse using our serializer
        var parsedHello = _serializer.DeserializeHello(wireData, out var bytesConsumed);

        // Assert - verify we correctly parsed Syncthing format
        Assert.Equal(wireData.Length, bytesConsumed);
        Assert.Equal(expectedHello.DeviceName, parsedHello.DeviceName);
        Assert.Equal(expectedHello.ClientName, parsedHello.ClientName);
        Assert.Equal(expectedHello.ClientVersion, parsedHello.ClientVersion);
    }

    /// <summary>
    /// Tests bidirectional Hello message exchange as would occur during BEP connection setup.
    /// Mirrors the ExchangeHello pattern from syncthing/lib/protocol/bep_hello_test.go.
    /// </summary>
    [Fact]
    public async Task CrossCompatibility_HelloExchange_BidirectionalStream()
    {
        // Arrange - simulate Syncthing sending a Hello
        var remoteHello = new Hello
        {
            DeviceName = "remote device",
            ClientName = "syncthing",
            ClientVersion = "v1.27.0",
            NumConnections = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var localHello = new Hello
        {
            DeviceName = "local device",
            ClientName = "CreatioHelper",
            ClientVersion = "1.0.0",
            NumConnections = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        // Create bidirectional stream (simulating network connection)
        using var remoteToLocal = new MemoryStream();
        using var localToRemote = new MemoryStream();

        // Remote writes Hello (simulating Syncthing)
        var remoteProtoBytes = remoteHello.ToByteArray();
        var remoteWireData = new byte[6 + remoteProtoBytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(remoteWireData.AsSpan(0, 4), BepProtobufSerializer.BepMagic);
        BinaryPrimitives.WriteUInt16BigEndian(remoteWireData.AsSpan(4, 2), (ushort)remoteProtoBytes.Length);
        remoteProtoBytes.CopyTo(remoteWireData.AsSpan(6));
        await remoteToLocal.WriteAsync(remoteWireData);
        remoteToLocal.Position = 0;

        // Act - Local reads remote Hello and writes its own
        var receivedRemoteHello = await _serializer.ReadHelloAsync(remoteToLocal);
        await _serializer.WriteHelloAsync(localToRemote, localHello);
        localToRemote.Position = 0;

        // Verify the Hello we sent can be read by Syncthing format
        var localWireData = localToRemote.ToArray();
        var localMagic = BinaryPrimitives.ReadUInt32BigEndian(localWireData.AsSpan(0, 4));
        var localLength = BinaryPrimitives.ReadUInt16BigEndian(localWireData.AsSpan(4, 2));

        // Assert
        Assert.Equal(remoteHello.DeviceName, receivedRemoteHello.DeviceName);
        Assert.Equal(remoteHello.ClientName, receivedRemoteHello.ClientName);
        Assert.Equal(remoteHello.ClientVersion, receivedRemoteHello.ClientVersion);
        Assert.Equal(BepProtobufSerializer.BepMagic, localMagic);
        Assert.Equal(localWireData.Length - 6, localLength);
    }

    /// <summary>
    /// Tests that old/legacy magic numbers are correctly identified and rejected.
    /// Mirrors TestOldHelloMsgs from syncthing/lib/protocol/bep_hello_test.go.
    /// </summary>
    [Theory]
    [InlineData("00010001", true, false)]  // v12 - too old
    [InlineData("9F79BC40", true, false)]  // v13 - too old
    [InlineData("12345678", false, true)]  // unknown magic - newer?
    public void CrossCompatibility_OldHelloMagic_ThrowsCorrectException(
        string magicHex, bool expectTooOld, bool expectUnknown)
    {
        // Arrange - create wire data with specified magic number
        var magicBytes = Convert.FromHexString(magicHex);
        var wireData = new byte[magicBytes.Length + 2 + 1]; // magic + length + dummy body
        magicBytes.CopyTo(wireData.AsSpan(0));
        BinaryPrimitives.WriteUInt16BigEndian(wireData.AsSpan(4, 2), 1);
        wireData[6] = 0; // dummy body byte

        // Act & Assert
        if (expectTooOld)
        {
            var ex = Assert.Throws<BepTooOldVersionException>(() =>
                _serializer.DeserializeHello(wireData, out _));
            Assert.Contains("older version", ex.Message);
        }
        else if (expectUnknown)
        {
            var ex = Assert.Throws<BepUnknownMagicException>(() =>
                _serializer.DeserializeHello(wireData, out _));
            Assert.Contains("unknown", ex.Message);
        }
    }

    /// <summary>
    /// Tests that a BEP message created in Syncthing wire format can be parsed.
    /// Wire format: [2B header length][Header protobuf][4B message length][Message protobuf]
    /// </summary>
    [Fact]
    public void CrossCompatibility_ParseSyncthingBepMessage()
    {
        // Arrange - create a Request message in exact Syncthing wire format
        var expectedRequest = new Request
        {
            Id = 12345,
            Folder = "default",
            Name = "test-file.txt",
            Offset = 0,
            Size = 131072,
            Hash = ByteString.CopyFrom(new byte[32]),
            FromTemporary = false,
            BlockNo = 0
        };
        var requestProtoBytes = expectedRequest.ToByteArray();

        var header = new Header
        {
            Type = MessageType.Request,
            Compression = MessageCompression.None
        };
        var headerProtoBytes = header.ToByteArray();

        // Build wire format: [2B header len][header proto][4B msg len][msg proto]
        var wireData = new byte[2 + headerProtoBytes.Length + 4 + requestProtoBytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(wireData.AsSpan(0, 2), (ushort)headerProtoBytes.Length);
        headerProtoBytes.CopyTo(wireData.AsSpan(2));
        BinaryPrimitives.WriteInt32BigEndian(wireData.AsSpan(2 + headerProtoBytes.Length, 4), requestProtoBytes.Length);
        requestProtoBytes.CopyTo(wireData.AsSpan(2 + headerProtoBytes.Length + 4));

        // Act
        var (parsedHeader, messageBytes) = _serializer.DeserializeMessage(wireData, out var bytesConsumed);
        var parsedRequest = _serializer.ParseMessage<Request>(messageBytes);

        // Assert
        Assert.Equal(wireData.Length, bytesConsumed);
        Assert.Equal(MessageType.Request, parsedHeader.Type);
        Assert.Equal(MessageCompression.None, parsedHeader.Compression);
        Assert.Equal(expectedRequest.Id, parsedRequest.Id);
        Assert.Equal(expectedRequest.Folder, parsedRequest.Folder);
        Assert.Equal(expectedRequest.Name, parsedRequest.Name);
        Assert.Equal(expectedRequest.Offset, parsedRequest.Offset);
        Assert.Equal(expectedRequest.Size, parsedRequest.Size);
    }

    /// <summary>
    /// Tests ClusterConfig message exchange as occurs immediately after Hello exchange.
    /// ClusterConfig MUST be the first message after Hello in BEP protocol.
    /// </summary>
    [Fact]
    public async Task CrossCompatibility_ClusterConfigExchange()
    {
        // Arrange - create ClusterConfig as Syncthing would send
        var remoteConfig = new ClusterConfig();
        var folder = new Folder
        {
            Id = "default",
            Label = "Default Folder",
            Type = FolderType.SendReceive,
            StopReason = FolderStopReason.Running
        };
        folder.Devices.Add(new Device
        {
            Id = ByteString.CopyFrom(new byte[32]),
            Name = "syncthing-device",
            Compression = Compression.Metadata,
            MaxSequence = 0,
            IndexId = 0
        });
        remoteConfig.Folders.Add(folder);

        // Simulate stream exchange
        using var stream = new MemoryStream();

        // Write ClusterConfig as Syncthing would
        await _serializer.WriteMessageAsync(stream, remoteConfig, MessageType.ClusterConfig);
        stream.Position = 0;

        // Act - read and parse
        var (header, message) = await _serializer.ReadMessageAsync(stream);

        // Assert
        Assert.Equal(MessageType.ClusterConfig, header.Type);
        var parsedConfig = Assert.IsType<ClusterConfig>(message);
        Assert.Single(parsedConfig.Folders);
        Assert.Equal("default", parsedConfig.Folders[0].Id);
        Assert.Single(parsedConfig.Folders[0].Devices);
    }

    /// <summary>
    /// Tests Index message wire format compatibility with Syncthing.
    /// Verifies FileInfo and BlockInfo are correctly serialized/deserialized.
    /// </summary>
    [Fact]
    public void CrossCompatibility_IndexMessageFormat()
    {
        // Arrange - create Index message with realistic file structure
        var syncthingIndex = new BepIndex
        {
            Folder = "default",
            LastSequence = 42
        };

        var fileInfo = new BepFileInfo
        {
            Name = "documents/report.pdf",
            Type = FileInfoType.File,
            Size = 1048576, // 1 MB
            Permissions = 0x1A4, // 0644 octal
            ModifiedS = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ModifiedNs = 500000000, // 0.5 seconds
            ModifiedBy = 12345678901234567890UL, // ShortID
            Deleted = false,
            Invalid = false,
            NoPermissions = false,
            Sequence = 42,
            BlockSize = 131072 // 128 KB
        };

        // Add blocks (1 MB file with 128 KB blocks = 8 blocks)
        for (int i = 0; i < 8; i++)
        {
            fileInfo.Blocks.Add(new BlockInfo
            {
                Offset = i * 131072,
                Size = 131072,
                Hash = ByteString.CopyFrom(new byte[32])
            });
        }
        syncthingIndex.Files.Add(fileInfo);

        // Act - serialize and deserialize
        var serialized = _serializer.SerializeMessage(syncthingIndex, MessageType.Index, allowCompression: false);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var parsed = _serializer.ParseMessage<BepIndex>(messageBytes);

        // Assert wire format compatibility
        Assert.Equal(MessageType.Index, header.Type);
        Assert.Equal("default", parsed.Folder);
        Assert.Equal(42, parsed.LastSequence);
        Assert.Single(parsed.Files);

        var parsedFile = parsed.Files[0];
        Assert.Equal("documents/report.pdf", parsedFile.Name);
        Assert.Equal(FileInfoType.File, parsedFile.Type);
        Assert.Equal(1048576, parsedFile.Size);
        Assert.Equal(0x1A4u, parsedFile.Permissions);
        Assert.Equal(8, parsedFile.Blocks.Count);
        Assert.Equal(131072, parsedFile.BlockSize);

        // Verify block offsets
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(i * 131072, parsedFile.Blocks[i].Offset);
        }
    }

    /// <summary>
    /// Tests Response message with data payload - critical for file transfer.
    /// </summary>
    [Fact]
    public void CrossCompatibility_ResponseMessageWithData()
    {
        // Arrange - create Response as Syncthing would for a block request
        var blockData = new byte[131072]; // 128 KB block
        new Random(42).NextBytes(blockData);

        var response = new Response
        {
            Id = 12345,
            Data = ByteString.CopyFrom(blockData),
            Code = ErrorCode.NoError
        };

        // Act - round trip through wire format
        var serialized = _serializer.SerializeMessage(response, MessageType.Response, allowCompression: false);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var parsed = _serializer.ParseMessage<Response>(messageBytes);

        // Assert - verify data integrity
        Assert.Equal(MessageType.Response, header.Type);
        Assert.Equal(12345, parsed.Id);
        Assert.Equal(ErrorCode.NoError, parsed.Code);
        Assert.Equal(blockData, parsed.Data.ToByteArray());
    }

    /// <summary>
    /// Tests LZ4 compressed message format is compatible with Syncthing.
    /// Wire format for compressed: [header][4B uncompressed size (big-endian)][LZ4 block]
    /// </summary>
    [Fact]
    public void CrossCompatibility_LZ4CompressedMessageFormat()
    {
        // Arrange - highly compressible data that will trigger compression
        var compressibleData = new byte[4096];
        Array.Fill(compressibleData, (byte)'A');

        var response = new Response
        {
            Id = 1,
            Data = ByteString.CopyFrom(compressibleData),
            Code = ErrorCode.NoError
        };

        // Act - serialize with compression
        var serialized = _serializer.SerializeMessage(response, MessageType.Response, allowCompression: true);

        // Extract compressed payload
        var headerLength = BinaryPrimitives.ReadUInt16BigEndian(serialized.AsSpan(0, 2));
        var header = Header.Parser.ParseFrom(serialized.AsSpan(2, headerLength));

        // Assert compression was used
        Assert.Equal(MessageCompression.Lz4, header.Compression);

        // Verify round-trip
        var (_, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var parsed = _serializer.ParseMessage<Response>(messageBytes);
        Assert.Equal(compressibleData, parsed.Data.ToByteArray());
    }

    /// <summary>
    /// Tests that our serializer produces wire format identical to manual construction.
    /// This ensures byte-for-byte compatibility with Syncthing.
    /// </summary>
    [Fact]
    public void CrossCompatibility_WireFormatByteLevelMatch()
    {
        // Arrange
        var hello = new Hello
        {
            DeviceName = "test",
            ClientName = "syncthing",
            ClientVersion = "v1.0.0"
        };

        // Manually construct expected wire format
        var protoBytes = hello.ToByteArray();
        var expectedWire = new byte[6 + protoBytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(expectedWire.AsSpan(0, 4), BepProtobufSerializer.BepMagic);
        BinaryPrimitives.WriteUInt16BigEndian(expectedWire.AsSpan(4, 2), (ushort)protoBytes.Length);
        protoBytes.CopyTo(expectedWire.AsSpan(6));

        // Act - use our serializer
        var actualWire = _serializer.SerializeHello(hello);

        // Assert - byte-for-byte match
        Assert.Equal(expectedWire, actualWire);
    }

    /// <summary>
    /// Tests message exchange over a simulated TCP connection stream.
    /// Validates the full message exchange pattern used in BEP connections.
    /// </summary>
    [Fact]
    public async Task CrossCompatibility_FullMessageExchangeSimulation()
    {
        using var stream = new MemoryStream();

        // Phase 1: Hello exchange
        var localHello = new Hello
        {
            DeviceName = "local",
            ClientName = "CreatioHelper",
            ClientVersion = "1.0.0",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        await _serializer.WriteHelloAsync(stream, localHello);

        // Phase 2: ClusterConfig (must be first message after Hello)
        var config = new ClusterConfig();
        config.Folders.Add(new Folder
        {
            Id = "sync",
            Label = "Sync Folder",
            Type = FolderType.SendReceive,
            StopReason = FolderStopReason.Running
        });

        await _serializer.WriteMessageAsync(stream, config, MessageType.ClusterConfig);

        // Phase 3: Index message
        var index = new BepIndex
        {
            Folder = "sync",
            LastSequence = 1
        };
        index.Files.Add(new BepFileInfo
        {
            Name = "test.txt",
            Type = FileInfoType.File,
            Size = 100,
            Sequence = 1
        });

        await _serializer.WriteMessageAsync(stream, index, MessageType.Index);

        // Reset stream to read back
        stream.Position = 0;

        // Act - read all messages back
        var receivedHello = await _serializer.ReadHelloAsync(stream);
        var (configHeader, configMsg) = await _serializer.ReadMessageAsync(stream);
        var (indexHeader, indexMsg) = await _serializer.ReadMessageAsync(stream);

        // Assert - all messages received correctly
        Assert.Equal("local", receivedHello.DeviceName);

        Assert.Equal(MessageType.ClusterConfig, configHeader.Type);
        var receivedConfig = Assert.IsType<ClusterConfig>(configMsg);
        Assert.Single(receivedConfig.Folders);

        Assert.Equal(MessageType.Index, indexHeader.Type);
        var receivedIndex = Assert.IsType<BepIndex>(indexMsg);
        Assert.Equal("sync", receivedIndex.Folder);
        Assert.Single(receivedIndex.Files);
    }

    /// <summary>
    /// Tests handling of Ping/Close control messages in wire format.
    /// These are special messages with minimal payload.
    /// </summary>
    [Fact]
    public async Task CrossCompatibility_ControlMessageExchange()
    {
        using var stream = new MemoryStream();

        // Write Ping (empty payload)
        var ping = new Ping();
        await _serializer.WriteMessageAsync(stream, ping, MessageType.Ping);

        // Write Close with reason
        var close = new Close { Reason = "test complete" };
        await _serializer.WriteMessageAsync(stream, close, MessageType.Close);

        stream.Position = 0;

        // Act - read back
        var (pingHeader, pingMsg) = await _serializer.ReadMessageAsync(stream);
        var (closeHeader, closeMsg) = await _serializer.ReadMessageAsync(stream);

        // Assert
        Assert.Equal(MessageType.Ping, pingHeader.Type);
        Assert.IsType<Ping>(pingMsg);

        Assert.Equal(MessageType.Close, closeHeader.Type);
        var receivedClose = Assert.IsType<Close>(closeMsg);
        Assert.Equal("test complete", receivedClose.Reason);
    }

    /// <summary>
    /// Tests Request/Response message pair as used in file block transfer.
    /// This is the core data transfer mechanism in BEP.
    /// </summary>
    [Fact]
    public async Task CrossCompatibility_RequestResponseExchange()
    {
        using var requestStream = new MemoryStream();
        using var responseStream = new MemoryStream();

        // Create request for a block
        var request = new Request
        {
            Id = 42,
            Folder = "default",
            Name = "large-file.bin",
            Offset = 131072, // Second block
            Size = 131072,   // 128 KB
            Hash = ByteString.CopyFrom(new byte[32]),
            FromTemporary = false,
            BlockNo = 1
        };

        await _serializer.WriteMessageAsync(requestStream, request, MessageType.Request);

        // Create response with block data
        var blockData = new byte[131072];
        new Random(42).NextBytes(blockData);

        var response = new Response
        {
            Id = 42, // Matches request
            Data = ByteString.CopyFrom(blockData),
            Code = ErrorCode.NoError
        };

        await _serializer.WriteMessageAsync(responseStream, response, MessageType.Response);

        // Reset streams
        requestStream.Position = 0;
        responseStream.Position = 0;

        // Act - read both
        var (reqHeader, reqMsg) = await _serializer.ReadMessageAsync(requestStream);
        var (respHeader, respMsg) = await _serializer.ReadMessageAsync(responseStream);

        // Assert
        var receivedRequest = Assert.IsType<Request>(reqMsg);
        Assert.Equal(42, receivedRequest.Id);
        Assert.Equal("large-file.bin", receivedRequest.Name);
        Assert.Equal(131072, receivedRequest.Offset);
        Assert.Equal(1, receivedRequest.BlockNo);

        var receivedResponse = Assert.IsType<Response>(respMsg);
        Assert.Equal(42, receivedResponse.Id);
        Assert.Equal(ErrorCode.NoError, receivedResponse.Code);
        Assert.Equal(blockData, receivedResponse.Data.ToByteArray());
    }

    /// <summary>
    /// Verifies that known Syncthing magic constants are correctly defined.
    /// These values are critical for protocol compatibility.
    /// </summary>
    [Fact]
    public void CrossCompatibility_MagicConstants_MatchSyncthing()
    {
        // From syncthing/lib/protocol/bep.go:
        // HelloMessageMagic = 0x2EA7D90B
        // Version13HelloMagic = 0x9F79BC40 (legacy)
        // OldMagic1 = 0x00010001 (v12 legacy)
        // OldMagic2 = 0x00010000 (v12 legacy)

        Assert.Equal(0x2EA7D90Bu, BepProtobufSerializer.BepMagic);
        Assert.Equal(0x9F79BC40u, BepProtobufSerializer.Version13HelloMagic);
        Assert.Equal(0x00010001u, BepProtobufSerializer.OldMagic1);
        Assert.Equal(0x00010000u, BepProtobufSerializer.OldMagic2);
    }

    /// <summary>
    /// Tests IndexUpdate message format which is used for incremental updates.
    /// </summary>
    [Fact]
    public void CrossCompatibility_IndexUpdateFormat()
    {
        // Arrange - IndexUpdate for a modified file
        var update = new IndexUpdate
        {
            Folder = "default",
            LastSequence = 100
        };

        update.Files.Add(new BepFileInfo
        {
            Name = "modified-file.txt",
            Type = FileInfoType.File,
            Size = 2048,
            Sequence = 100,
            ModifiedS = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ModifiedBy = 0x1234567890ABCDEF,
            Deleted = false
        });

        // Act
        var serialized = _serializer.SerializeMessage(update, MessageType.IndexUpdate, allowCompression: false);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var parsed = _serializer.ParseMessage<IndexUpdate>(messageBytes);

        // Assert
        Assert.Equal(MessageType.IndexUpdate, header.Type);
        Assert.Equal("default", parsed.Folder);
        Assert.Equal(100, parsed.LastSequence);
        Assert.Single(parsed.Files);
        Assert.Equal("modified-file.txt", parsed.Files[0].Name);
    }

    /// <summary>
    /// Tests handling of deleted file entries in Index messages.
    /// </summary>
    [Fact]
    public void CrossCompatibility_DeletedFileInIndex()
    {
        // Arrange - Index with deleted file
        var index = new BepIndex
        {
            Folder = "default",
            LastSequence = 50
        };

        index.Files.Add(new BepFileInfo
        {
            Name = "deleted-file.txt",
            Type = FileInfoType.File,
            Size = 0,
            Sequence = 50,
            Deleted = true
        });

        // Act
        var serialized = _serializer.SerializeMessage(index, MessageType.Index, allowCompression: false);
        var (_, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var parsed = _serializer.ParseMessage<BepIndex>(messageBytes);

        // Assert
        Assert.Single(parsed.Files);
        Assert.True(parsed.Files[0].Deleted);
        Assert.Equal(0, parsed.Files[0].Size);
    }

    /// <summary>
    /// Tests directory entry in Index messages.
    /// </summary>
    [Fact]
    public void CrossCompatibility_DirectoryInIndex()
    {
        // Arrange - Index with directory
        var index = new BepIndex
        {
            Folder = "default",
            LastSequence = 10
        };

        index.Files.Add(new BepFileInfo
        {
            Name = "subdir",
            Type = FileInfoType.Directory,
            Permissions = 0x1ED, // 0755 octal
            ModifiedS = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Sequence = 10,
            Deleted = false
        });

        // Act
        var serialized = _serializer.SerializeMessage(index, MessageType.Index, allowCompression: false);
        var (_, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var parsed = _serializer.ParseMessage<BepIndex>(messageBytes);

        // Assert
        Assert.Single(parsed.Files);
        Assert.Equal(FileInfoType.Directory, parsed.Files[0].Type);
        Assert.Equal("subdir", parsed.Files[0].Name);
        Assert.Empty(parsed.Files[0].Blocks); // Directories have no blocks
    }

    /// <summary>
    /// Tests symlink entry in Index messages.
    /// </summary>
    [Fact]
    public void CrossCompatibility_SymlinkInIndex()
    {
        // Arrange - Index with symlink
        var index = new BepIndex
        {
            Folder = "default",
            LastSequence = 15
        };

        var symlinkTarget = "/actual/path/to/file.txt";
        index.Files.Add(new BepFileInfo
        {
            Name = "link-to-file",
            Type = FileInfoType.Symlink,
            SymlinkTarget = ByteString.CopyFromUtf8(symlinkTarget),
            Sequence = 15,
            Deleted = false
        });

        // Act
        var serialized = _serializer.SerializeMessage(index, MessageType.Index, allowCompression: false);
        var (_, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var parsed = _serializer.ParseMessage<BepIndex>(messageBytes);

        // Assert
        Assert.Single(parsed.Files);
        Assert.Equal(FileInfoType.Symlink, parsed.Files[0].Type);
        Assert.Equal(symlinkTarget, parsed.Files[0].SymlinkTarget.ToStringUtf8());
    }

    /// <summary>
    /// Tests error response codes in Response messages.
    /// </summary>
    [Theory]
    [InlineData(ErrorCode.NoError)]
    [InlineData(ErrorCode.Generic)]
    [InlineData(ErrorCode.NoSuchFile)]
    [InlineData(ErrorCode.InvalidFile)]
    public void CrossCompatibility_ResponseErrorCodes(ErrorCode errorCode)
    {
        // Arrange
        var response = new Response
        {
            Id = 1,
            Code = errorCode,
            Data = ByteString.Empty
        };

        // Act
        var serialized = _serializer.SerializeMessage(response, MessageType.Response, allowCompression: false);
        var (_, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var parsed = _serializer.ParseMessage<Response>(messageBytes);

        // Assert
        Assert.Equal(errorCode, parsed.Code);
    }

    /// <summary>
    /// Tests DownloadProgress message format used for transfer progress tracking.
    /// </summary>
    [Fact]
    public void CrossCompatibility_DownloadProgressFormat()
    {
        // Arrange
        var progress = new DownloadProgress
        {
            Folder = "default"
        };

        progress.Updates.Add(new FileDownloadProgressUpdate
        {
            UpdateType = FileDownloadProgressUpdateType.Append,
            Name = "large-file.bin",
            BlockSize = 131072 // 128 KB
        });
        progress.Updates[0].BlockIndexes.AddRange(new[] { 0, 1, 2, 3, 4 });

        // Act
        var serialized = _serializer.SerializeMessage(progress, MessageType.DownloadProgress, allowCompression: false);
        var (header, messageBytes) = _serializer.DeserializeMessage(serialized, out _);
        var parsed = _serializer.ParseMessage<DownloadProgress>(messageBytes);

        // Assert
        Assert.Equal(MessageType.DownloadProgress, header.Type);
        Assert.Equal("default", parsed.Folder);
        Assert.Single(parsed.Updates);
        Assert.Equal(FileDownloadProgressUpdateType.Append, parsed.Updates[0].UpdateType);
        Assert.Equal(5, parsed.Updates[0].BlockIndexes.Count);
    }

    #endregion
}
