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
    public void DeserializeHello_InvalidMagic_ThrowsException()
    {
        // Arrange - data with invalid magic
        var invalidData = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00 };

        // Act & Assert
        var ex = Assert.Throws<InvalidDataException>(() =>
            _serializer.DeserializeHello(invalidData, out _));
        Assert.Contains("Invalid BEP magic", ex.Message);
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
            ReadOnly = true,
            IgnorePermissions = false,
            IgnoreDelete = true,
            DisableTempIndexes = false,
            Paused = false
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
        Assert.True(deserialized.Folders[0].ReadOnly);
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
            Hash = ByteString.CopyFrom(new byte[32]),
            WeakHash = 12345678
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
            WeakHash = 0xDEADBEEF,
            BlockNo = 1
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
        Assert.Equal(0xDEADBEEFu, deserialized.WeakHash);
        Assert.Equal(1, deserialized.BlockNo);
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
}
