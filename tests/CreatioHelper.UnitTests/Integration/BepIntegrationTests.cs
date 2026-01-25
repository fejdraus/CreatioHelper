using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using CreatioHelper.Infrastructure.Services.Sync;
using CreatioHelper.Infrastructure.Services.Sync.Proto;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using ProtoIndex = CreatioHelper.Infrastructure.Services.Sync.Proto.Index;
using ProtoFileInfo = CreatioHelper.Infrastructure.Services.Sync.Proto.FileInfo;

namespace CreatioHelper.UnitTests.Integration;

/// <summary>
/// BEP Integration Tests for Hello + ClusterConfig exchange.
/// Tests the complete BEP handshake sequence following Syncthing protocol requirements.
///
/// Key protocol invariants tested:
/// 1. Hello message exchange (first message with magic number)
/// 2. ClusterConfig MUST be the first message after Hello
/// 3. Protocol state transitions (Initial -> Ready)
/// 4. Wire format compatibility with Syncthing
/// </summary>
public class BepIntegrationTests : IDisposable
{
    private readonly Mock<ILogger<BepConnection>> _mockLogger;
    private readonly BepProtobufSerializer _serializer;
    private TcpListener? _listener;
    private readonly List<TcpClient> _clients = new();

    public BepIntegrationTests()
    {
        _mockLogger = new Mock<ILogger<BepConnection>>();
        _serializer = new BepProtobufSerializer();
    }

    #region Test Helpers

    /// <summary>
    /// Creates a connected TCP client/server pair for testing BepConnection.
    /// </summary>
    private async Task<(TcpClient client, TcpClient server)> CreateConnectedPairAsync()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        var clientTask = Task.Run(() => new TcpClient("127.0.0.1", port));
        var serverTask = _listener.AcceptTcpClientAsync();

        await Task.WhenAll(clientTask, serverTask);

        var client = await clientTask;
        var server = await serverTask;

        _clients.Add(client);
        _clients.Add(server);

        return (client, server);
    }

    /// <summary>
    /// Creates a BepHello message for testing.
    /// </summary>
    private static BepHello CreateTestHello(string deviceName, string clientVersion = "1.0.0")
    {
        return new BepHello
        {
            DeviceId = $"DEVICE-{deviceName}",
            DeviceName = deviceName,
            ClientName = "CreatioHelper",
            ClientVersion = clientVersion,
            NumConnections = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };
    }

    /// <summary>
    /// Creates a BepClusterConfig message for testing.
    /// </summary>
    private static BepClusterConfig CreateTestClusterConfig(string folderId = "test-folder")
    {
        return new BepClusterConfig
        {
            Folders = new List<BepFolder>
            {
                new BepFolder
                {
                    Id = folderId,
                    Label = "Test Folder",
                    ReadOnly = false,
                    Paused = false,
                    Devices = new List<BepDevice>
                    {
                        new BepDevice
                        {
                            Id = new byte[32],
                            Name = "TestDevice",
                            Compression = BepCompression.Metadata,
                            MaxSequence = 0,
                            IndexId = 0
                        }
                    }
                }
            }
        };
    }

    #endregion

    #region Hello Exchange Tests

    /// <summary>
    /// Tests that Hello message can be sent and received correctly.
    /// Verifies wire format: [4B magic (0x2EA7D90B)][2B length][Hello protobuf]
    /// </summary>
    [Fact]
    public async Task BepIntegration_HelloExchange_RoundTrip()
    {
        // Arrange
        var (client, server) = await CreateConnectedPairAsync();
        var clientStream = client.GetStream();
        var serverStream = server.GetStream();

        var sentHello = CreateTestHello("LocalDevice");

        // Convert to proto
        var protoHello = new Hello
        {
            DeviceName = sentHello.DeviceName,
            ClientName = sentHello.ClientName,
            ClientVersion = sentHello.ClientVersion,
            NumConnections = sentHello.NumConnections,
            Timestamp = sentHello.Timestamp
        };

        // Act - Write Hello from client
        await _serializer.WriteHelloAsync(clientStream, protoHello);

        // Read Hello on server
        var receivedHello = await _serializer.ReadHelloAsync(serverStream);

        // Assert
        Assert.Equal(sentHello.DeviceName, receivedHello.DeviceName);
        Assert.Equal(sentHello.ClientName, receivedHello.ClientName);
        Assert.Equal(sentHello.ClientVersion, receivedHello.ClientVersion);
        Assert.Equal(sentHello.NumConnections, receivedHello.NumConnections);
        Assert.Equal(sentHello.Timestamp, receivedHello.Timestamp);
    }

    /// <summary>
    /// Tests bidirectional Hello exchange between two peers.
    /// Both peers must send and receive Hello messages.
    /// </summary>
    [Fact]
    public async Task BepIntegration_HelloExchange_Bidirectional()
    {
        // Arrange
        var (client, server) = await CreateConnectedPairAsync();
        var clientStream = client.GetStream();
        var serverStream = server.GetStream();

        var clientHello = new Hello
        {
            DeviceName = "ClientDevice",
            ClientName = "CreatioHelper",
            ClientVersion = "1.0.0",
            NumConnections = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        var serverHello = new Hello
        {
            DeviceName = "ServerDevice",
            ClientName = "Syncthing",
            ClientVersion = "v1.27.0",
            NumConnections = 1,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        // Act - Both sides send Hello simultaneously
        var writeClientTask = _serializer.WriteHelloAsync(clientStream, clientHello);
        var writeServerTask = _serializer.WriteHelloAsync(serverStream, serverHello);
        await Task.WhenAll(writeClientTask, writeServerTask);

        // Both sides read Hello
        var readServerTask = _serializer.ReadHelloAsync(serverStream);
        var readClientTask = _serializer.ReadHelloAsync(clientStream);
        await Task.WhenAll(readServerTask, readClientTask);

        var receivedByServer = await readServerTask;
        var receivedByClient = await readClientTask;

        // Assert
        Assert.Equal("ClientDevice", receivedByServer.DeviceName);
        Assert.Equal("CreatioHelper", receivedByServer.ClientName);

        Assert.Equal("ServerDevice", receivedByClient.DeviceName);
        Assert.Equal("Syncthing", receivedByClient.ClientName);
    }

    /// <summary>
    /// Tests Hello message wire format matches Syncthing exactly.
    /// Wire format: [4B magic (big-endian 0x2EA7D90B)][2B length (big-endian)][Hello protobuf]
    /// </summary>
    [Fact]
    public async Task BepIntegration_HelloWireFormat_MatchesSyncthing()
    {
        // Arrange
        using var stream = new MemoryStream();
        var hello = new Hello
        {
            DeviceName = "TestDevice",
            ClientName = "Syncthing",
            ClientVersion = "v1.27.0"
        };

        // Act
        await _serializer.WriteHelloAsync(stream, hello);
        var bytes = stream.ToArray();

        // Assert - Verify magic number (first 4 bytes)
        Assert.True(bytes.Length >= 6);
        var magic = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(0, 4));
        Assert.Equal(BepProtobufSerializer.BepMagic, magic);

        // Verify length field (next 2 bytes)
        var helloLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4, 2));
        Assert.Equal(bytes.Length - 6, helloLength);

        // Verify protobuf payload can be parsed
        var helloBytes = bytes.AsSpan(6, helloLength).ToArray();
        var parsed = Hello.Parser.ParseFrom(helloBytes);
        Assert.Equal("TestDevice", parsed.DeviceName);
    }

    /// <summary>
    /// Tests that legacy magic numbers (v13) are correctly rejected.
    /// </summary>
    [Fact]
    public async Task BepIntegration_LegacyMagic_ThrowsBepTooOldVersionException()
    {
        // Arrange - Create stream with v13 legacy magic (0x9F79BC40)
        using var stream = new MemoryStream();
        var header = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 0x9F79BC40);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), 1);
        stream.Write(header);
        stream.WriteByte(0); // dummy body
        stream.Position = 0;

        // Act & Assert
        var ex = await Assert.ThrowsAsync<BepTooOldVersionException>(
            async () => await _serializer.ReadHelloAsync(stream));
        Assert.Contains("older version", ex.Message);
    }

    /// <summary>
    /// Tests that unknown magic numbers are correctly rejected.
    /// </summary>
    [Fact]
    public async Task BepIntegration_UnknownMagic_ThrowsBepUnknownMagicException()
    {
        // Arrange - Create stream with unknown magic
        using var stream = new MemoryStream();
        var header = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 0xDEADBEEF);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), 1);
        stream.Write(header);
        stream.WriteByte(0); // dummy body
        stream.Position = 0;

        // Act & Assert
        var ex = await Assert.ThrowsAsync<BepUnknownMagicException>(
            async () => await _serializer.ReadHelloAsync(stream));
        Assert.Contains("unknown", ex.Message);
    }

    #endregion

    #region ClusterConfig Exchange Tests

    /// <summary>
    /// Tests ClusterConfig message exchange after Hello.
    /// ClusterConfig MUST be the first message after Hello exchange.
    /// </summary>
    [Fact]
    public async Task BepIntegration_ClusterConfigExchange_RoundTrip()
    {
        // Arrange
        using var stream = new MemoryStream();
        var config = new ClusterConfig();
        config.Folders.Add(new Folder
        {
            Id = "default",
            Label = "Default Folder",
            Type = FolderType.SendReceive,
            StopReason = FolderStopReason.Running
        });
        config.Folders[0].Devices.Add(new Device
        {
            Id = ByteString.CopyFrom(new byte[32]),
            Name = "TestDevice",
            Compression = Compression.Metadata,
            MaxSequence = 100,
            IndexId = 12345
        });

        // Act - Write ClusterConfig
        await _serializer.WriteMessageAsync(stream, config, MessageType.ClusterConfig);
        stream.Position = 0;

        // Read ClusterConfig
        var (header, message) = await _serializer.ReadMessageAsync(stream);

        // Assert
        Assert.Equal(MessageType.ClusterConfig, header.Type);
        var parsed = Assert.IsType<ClusterConfig>(message);
        Assert.Single(parsed.Folders);
        Assert.Equal("default", parsed.Folders[0].Id);
        Assert.Single(parsed.Folders[0].Devices);
        Assert.Equal("TestDevice", parsed.Folders[0].Devices[0].Name);
    }

    /// <summary>
    /// Tests bidirectional ClusterConfig exchange.
    /// Both peers must send ClusterConfig after Hello.
    /// </summary>
    [Fact]
    public async Task BepIntegration_ClusterConfigExchange_Bidirectional()
    {
        // Arrange
        var (client, server) = await CreateConnectedPairAsync();
        var clientStream = client.GetStream();
        var serverStream = server.GetStream();

        var clientConfig = new ClusterConfig();
        clientConfig.Folders.Add(new Folder
        {
            Id = "client-folder",
            Label = "Client Folder",
            Type = FolderType.SendReceive,
            StopReason = FolderStopReason.Running
        });

        var serverConfig = new ClusterConfig();
        serverConfig.Folders.Add(new Folder
        {
            Id = "server-folder",
            Label = "Server Folder",
            Type = FolderType.SendOnly,
            StopReason = FolderStopReason.Running
        });

        // Act - Both sides send ClusterConfig
        var writeClientTask = _serializer.WriteMessageAsync(clientStream, clientConfig, MessageType.ClusterConfig);
        var writeServerTask = _serializer.WriteMessageAsync(serverStream, serverConfig, MessageType.ClusterConfig);
        await Task.WhenAll(writeClientTask, writeServerTask);

        // Both sides read ClusterConfig
        var readServerTask = _serializer.ReadMessageAsync(serverStream);
        var readClientTask = _serializer.ReadMessageAsync(clientStream);
        await Task.WhenAll(readServerTask, readClientTask);

        var (serverHeader, serverMessage) = await readServerTask;
        var (clientHeader, clientMessage) = await readClientTask;

        // Assert
        Assert.Equal(MessageType.ClusterConfig, serverHeader.Type);
        var serverParsed = Assert.IsType<ClusterConfig>(serverMessage);
        Assert.Equal("client-folder", serverParsed.Folders[0].Id);

        Assert.Equal(MessageType.ClusterConfig, clientHeader.Type);
        var clientParsed = Assert.IsType<ClusterConfig>(clientMessage);
        Assert.Equal("server-folder", clientParsed.Folders[0].Id);
    }

    /// <summary>
    /// Tests ClusterConfig with multiple folders and devices.
    /// </summary>
    [Fact]
    public async Task BepIntegration_ClusterConfigComplex_PreservesAllData()
    {
        // Arrange
        using var stream = new MemoryStream();
        var config = new ClusterConfig();

        // Add multiple folders
        for (int f = 0; f < 3; f++)
        {
            var folder = new Folder
            {
                Id = $"folder-{f}",
                Label = $"Folder {f}",
                Type = f % 2 == 0 ? FolderType.SendReceive : FolderType.SendOnly,
                StopReason = FolderStopReason.Running
            };

            // Add multiple devices to each folder
            for (int d = 0; d < 2; d++)
            {
                folder.Devices.Add(new Device
                {
                    Id = ByteString.CopyFrom(new byte[32]),
                    Name = $"Device-{f}-{d}",
                    Compression = d % 2 == 0 ? Compression.Always : Compression.Metadata,
                    MaxSequence = f * 100 + d,
                    IndexId = (ulong)(f * 1000 + d)
                });
                folder.Devices[d].Addresses.Add($"tcp://192.168.{f}.{d}:22000");
            }

            config.Folders.Add(folder);
        }

        // Act
        await _serializer.WriteMessageAsync(stream, config, MessageType.ClusterConfig);
        stream.Position = 0;
        var (header, message) = await _serializer.ReadMessageAsync(stream);

        // Assert
        var parsed = Assert.IsType<ClusterConfig>(message);
        Assert.Equal(3, parsed.Folders.Count);

        for (int f = 0; f < 3; f++)
        {
            Assert.Equal($"folder-{f}", parsed.Folders[f].Id);
            Assert.Equal($"Folder {f}", parsed.Folders[f].Label);
            Assert.Equal(2, parsed.Folders[f].Devices.Count);

            for (int d = 0; d < 2; d++)
            {
                Assert.Equal($"Device-{f}-{d}", parsed.Folders[f].Devices[d].Name);
                Assert.Single(parsed.Folders[f].Devices[d].Addresses);
            }
        }
    }

    #endregion

    #region Full Handshake Sequence Tests

    /// <summary>
    /// Tests the complete BEP handshake sequence: Hello + ClusterConfig exchange.
    /// This is the required startup sequence for any BEP connection.
    /// </summary>
    [Fact]
    public async Task BepIntegration_FullHandshake_HelloThenClusterConfig()
    {
        // Arrange
        var (client, server) = await CreateConnectedPairAsync();
        var clientStream = client.GetStream();
        var serverStream = server.GetStream();

        var clientHello = new Hello
        {
            DeviceName = "ClientDevice",
            ClientName = "CreatioHelper",
            ClientVersion = "1.0.0"
        };

        var serverHello = new Hello
        {
            DeviceName = "ServerDevice",
            ClientName = "Syncthing",
            ClientVersion = "v1.27.0"
        };

        var clientConfig = new ClusterConfig();
        clientConfig.Folders.Add(new Folder { Id = "shared-folder", Label = "Shared" });

        var serverConfig = new ClusterConfig();
        serverConfig.Folders.Add(new Folder { Id = "shared-folder", Label = "Shared" });

        // Act - Phase 1: Hello exchange
        var writeClientHelloTask = _serializer.WriteHelloAsync(clientStream, clientHello);
        var writeServerHelloTask = _serializer.WriteHelloAsync(serverStream, serverHello);
        await Task.WhenAll(writeClientHelloTask, writeServerHelloTask);

        var readClientHelloTask = _serializer.ReadHelloAsync(clientStream);
        var readServerHelloTask = _serializer.ReadHelloAsync(serverStream);
        await Task.WhenAll(readClientHelloTask, readServerHelloTask);

        // Phase 2: ClusterConfig exchange
        var writeClientConfigTask = _serializer.WriteMessageAsync(clientStream, clientConfig, MessageType.ClusterConfig);
        var writeServerConfigTask = _serializer.WriteMessageAsync(serverStream, serverConfig, MessageType.ClusterConfig);
        await Task.WhenAll(writeClientConfigTask, writeServerConfigTask);

        var readClientConfigTask = _serializer.ReadMessageAsync(clientStream);
        var readServerConfigTask = _serializer.ReadMessageAsync(serverStream);
        await Task.WhenAll(readClientConfigTask, readServerConfigTask);

        // Assert - Hello exchange succeeded
        var receivedServerHello = await readClientHelloTask;
        Assert.Equal("ServerDevice", receivedServerHello.DeviceName);

        var receivedClientHello = await readServerHelloTask;
        Assert.Equal("ClientDevice", receivedClientHello.DeviceName);

        // Assert - ClusterConfig exchange succeeded
        var (clientConfigHeader, clientConfigMessage) = await readClientConfigTask;
        Assert.Equal(MessageType.ClusterConfig, clientConfigHeader.Type);
        var clientParsed = Assert.IsType<ClusterConfig>(clientConfigMessage);
        Assert.Equal("shared-folder", clientParsed.Folders[0].Id);

        var (serverConfigHeader, serverConfigMessage) = await readServerConfigTask;
        Assert.Equal(MessageType.ClusterConfig, serverConfigHeader.Type);
        var serverParsed = Assert.IsType<ClusterConfig>(serverConfigMessage);
        Assert.Equal("shared-folder", serverParsed.Folders[0].Id);
    }

    /// <summary>
    /// Tests the full handshake with subsequent Index message.
    /// After Hello + ClusterConfig, Index messages can be exchanged.
    /// </summary>
    [Fact]
    public async Task BepIntegration_FullHandshake_ThenIndex()
    {
        // Arrange
        using var stream = new MemoryStream();

        var hello = new Hello
        {
            DeviceName = "TestDevice",
            ClientName = "CreatioHelper",
            ClientVersion = "1.0.0"
        };

        var config = new ClusterConfig();
        config.Folders.Add(new Folder { Id = "test-folder", Label = "Test" });

        var index = new ProtoIndex
        {
            Folder = "test-folder",
            LastSequence = 42
        };
        index.Files.Add(new ProtoFileInfo
        {
            Name = "test-file.txt",
            Type = FileInfoType.File,
            Size = 1024,
            Sequence = 1
        });

        // Act - Write sequence
        await _serializer.WriteHelloAsync(stream, hello);
        await _serializer.WriteMessageAsync(stream, config, MessageType.ClusterConfig);
        await _serializer.WriteMessageAsync(stream, index, MessageType.Index);

        // Read sequence
        stream.Position = 0;
        var receivedHello = await _serializer.ReadHelloAsync(stream);
        var (configHeader, configMessage) = await _serializer.ReadMessageAsync(stream);
        var (indexHeader, indexMessage) = await _serializer.ReadMessageAsync(stream);

        // Assert
        Assert.Equal("TestDevice", receivedHello.DeviceName);

        Assert.Equal(MessageType.ClusterConfig, configHeader.Type);
        Assert.IsType<ClusterConfig>(configMessage);

        Assert.Equal(MessageType.Index, indexHeader.Type);
        var parsedIndex = Assert.IsType<ProtoIndex>(indexMessage);
        Assert.Equal("test-folder", parsedIndex.Folder);
        Assert.Equal(42, parsedIndex.LastSequence);
        Assert.Single(parsedIndex.Files);
        Assert.Equal("test-file.txt", parsedIndex.Files[0].Name);
    }

    #endregion

    #region Protocol State Machine Tests

    /// <summary>
    /// Tests that BepConnection starts in Initial state.
    /// </summary>
    [Fact]
    public void BepIntegration_InitialState_IsInitial()
    {
        // Arrange
        using var tcpClient = new TcpClient();
        using var memoryStream = new MemoryStream();

        // Act
        using var connection = new BepConnection(
            "TEST-DEVICE",
            tcpClient,
            memoryStream,
            _mockLogger.Object,
            isOutgoing: true,
            BepSerializationMode.Protobuf);

        // Assert
        Assert.Equal(BepProtocolState.Initial, connection.ProtocolState);
        Assert.False(connection.IsProtocolReady);
    }

    /// <summary>
    /// Tests that sending ClusterConfig transitions state to Ready.
    /// </summary>
    [Fact]
    public async Task BepIntegration_SendClusterConfig_TransitionsToReady()
    {
        // Arrange
        var (client, _) = await CreateConnectedPairAsync();

        using var connection = new BepConnection(
            "TEST-DEVICE",
            client,
            client.GetStream(),
            _mockLogger.Object,
            isOutgoing: true,
            BepSerializationMode.Protobuf);

        // First send Hello (required before ClusterConfig)
        var hello = new BepHello
        {
            DeviceId = "TEST-DEVICE",
            DeviceName = "Test Device",
            ClientName = "CreatioHelper",
            ClientVersion = "1.0.0"
        };
        await connection.SendMessageAsync(BepMessageType.Hello, hello);

        var config = new BepClusterConfig { Folders = new List<BepFolder>() };

        // Act
        await connection.SendMessageAsync(BepMessageType.ClusterConfig, config);

        // Assert
        Assert.Equal(BepProtocolState.Ready, connection.ProtocolState);
        Assert.True(connection.IsProtocolReady);
    }

    /// <summary>
    /// Tests that data messages (Index, Request, etc.) are rejected before ClusterConfig.
    /// </summary>
    [Theory]
    [InlineData(BepMessageType.Index)]
    [InlineData(BepMessageType.IndexUpdate)]
    [InlineData(BepMessageType.Request)]
    [InlineData(BepMessageType.Response)]
    [InlineData(BepMessageType.DownloadProgress)]
    [InlineData(BepMessageType.Ping)]
    public async Task BepIntegration_DataMessageBeforeClusterConfig_Throws(BepMessageType messageType)
    {
        // Arrange
        using var tcpClient = new TcpClient();
        using var memoryStream = new MemoryStream();

        using var connection = new BepConnection(
            "TEST-DEVICE",
            tcpClient,
            memoryStream,
            _mockLogger.Object,
            isOutgoing: true,
            BepSerializationMode.Protobuf);

        object message = messageType switch
        {
            BepMessageType.Index => new BepIndex { Folder = "test", Files = new List<BepFileInfo>() },
            BepMessageType.IndexUpdate => new BepIndexUpdate { Folder = "test", Files = new List<BepFileInfo>() },
            BepMessageType.Request => new BepRequest { Id = 1, Folder = "test", Name = "file.txt" },
            BepMessageType.Response => new BepResponse { Id = 1, Data = new byte[0] },
            BepMessageType.DownloadProgress => new BepDownloadProgress { Folder = "test", Updates = new List<BepFileDownloadProgressUpdate>() },
            BepMessageType.Ping => new BepPing(),
            _ => throw new ArgumentException($"Unknown message type: {messageType}")
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (message is BepIndex index) await connection.SendMessageAsync(BepMessageType.Index, index);
            else if (message is BepIndexUpdate update) await connection.SendMessageAsync(BepMessageType.IndexUpdate, update);
            else if (message is BepRequest request) await connection.SendMessageAsync(BepMessageType.Request, request);
            else if (message is BepResponse response) await connection.SendMessageAsync(BepMessageType.Response, response);
            else if (message is BepDownloadProgress progress) await connection.SendMessageAsync(BepMessageType.DownloadProgress, progress);
            else if (message is BepPing ping) await connection.SendMessageAsync(BepMessageType.Ping, ping);
        });

        Assert.Contains("ClusterConfig", ex.Message);
        Assert.Contains("BEP protocol violation", ex.Message);
    }

    /// <summary>
    /// Tests that Hello and Close messages are always allowed regardless of protocol state.
    /// </summary>
    [Fact]
    public async Task BepIntegration_HelloAndClose_AlwaysAllowed()
    {
        // Arrange
        var (client, _) = await CreateConnectedPairAsync();

        using var connection = new BepConnection(
            "TEST-DEVICE",
            client,
            client.GetStream(),
            _mockLogger.Object,
            isOutgoing: true,
            BepSerializationMode.Protobuf);

        var hello = new BepHello
        {
            DeviceId = "TEST-DEVICE",
            DeviceName = "Test",
            ClientName = "CreatioHelper",
            ClientVersion = "1.0.0"
        };

        // Act & Assert - Hello should not throw in Initial state
        await connection.SendMessageAsync(BepMessageType.Hello, hello);
        Assert.Equal(BepProtocolState.Initial, connection.ProtocolState);
    }

    /// <summary>
    /// Tests that after ClusterConfig, all message types are allowed.
    /// </summary>
    [Fact]
    public async Task BepIntegration_AfterClusterConfig_AllMessagesAllowed()
    {
        // Arrange
        var (client, _) = await CreateConnectedPairAsync();

        using var connection = new BepConnection(
            "TEST-DEVICE",
            client,
            client.GetStream(),
            _mockLogger.Object,
            isOutgoing: true,
            BepSerializationMode.Protobuf);

        // Send Hello and ClusterConfig
        var hello = new BepHello
        {
            DeviceId = "TEST-DEVICE",
            DeviceName = "Test",
            ClientName = "CreatioHelper",
            ClientVersion = "1.0.0"
        };
        await connection.SendMessageAsync(BepMessageType.Hello, hello);

        var config = new BepClusterConfig { Folders = new List<BepFolder>() };
        await connection.SendMessageAsync(BepMessageType.ClusterConfig, config);

        // Act - Now Index should be allowed
        var index = new BepIndex
        {
            Folder = "test-folder",
            Files = new List<BepFileInfo>()
        };

        // Should not throw
        await connection.SendMessageAsync(BepMessageType.Index, index);

        // Assert
        Assert.Equal(BepProtocolState.Ready, connection.ProtocolState);
    }

    #endregion

    #region Wire Format Compatibility Tests

    /// <summary>
    /// Tests that BEP message wire format matches Syncthing exactly.
    /// Wire format: [2B header length][Header protobuf][4B message length][Message protobuf]
    /// </summary>
    [Fact]
    public async Task BepIntegration_MessageWireFormat_MatchesSyncthing()
    {
        // Arrange
        using var stream = new MemoryStream();
        var ping = new Ping();

        // Act
        await _serializer.WriteMessageAsync(stream, ping, MessageType.Ping);
        var bytes = stream.ToArray();

        // Assert - Verify structure
        Assert.True(bytes.Length >= 2);

        // Header length (2 bytes, big-endian)
        var headerLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(0, 2));
        Assert.True(headerLength > 0);

        // Parse header
        var headerBytes = bytes.AsSpan(2, headerLength).ToArray();
        var header = Header.Parser.ParseFrom(headerBytes);
        Assert.Equal(MessageType.Ping, header.Type);

        // Message length (4 bytes, big-endian)
        var msgLenOffset = 2 + headerLength;
        var messageLength = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(msgLenOffset, 4));
        Assert.True(messageLength >= 0);

        // Verify total length
        Assert.Equal(2 + headerLength + 4 + messageLength, bytes.Length);
    }

    /// <summary>
    /// Tests ClusterConfig wire format with nested structures.
    /// </summary>
    [Fact]
    public async Task BepIntegration_ClusterConfigWireFormat_NestedStructures()
    {
        // Arrange
        using var stream = new MemoryStream();
        var config = new ClusterConfig();
        var folder = new Folder
        {
            Id = "folder-id",
            Label = "Folder Label",
            Type = FolderType.SendReceive,
            StopReason = FolderStopReason.Running
        };
        folder.Devices.Add(new Device
        {
            Id = ByteString.CopyFrom(new byte[32]),
            Name = "DeviceName",
            Compression = Compression.Always,
            MaxSequence = 999,
            IndexId = 123456789
        });
        folder.Devices[0].Addresses.Add("tcp://192.168.1.1:22000");
        folder.Devices[0].Addresses.Add("tcp://192.168.1.2:22000");
        config.Folders.Add(folder);

        // Act
        await _serializer.WriteMessageAsync(stream, config, MessageType.ClusterConfig);
        stream.Position = 0;
        var (header, message) = await _serializer.ReadMessageAsync(stream);

        // Assert
        Assert.Equal(MessageType.ClusterConfig, header.Type);
        var parsed = Assert.IsType<ClusterConfig>(message);
        Assert.Single(parsed.Folders);
        Assert.Equal("folder-id", parsed.Folders[0].Id);
        Assert.Single(parsed.Folders[0].Devices);
        Assert.Equal("DeviceName", parsed.Folders[0].Devices[0].Name);
        Assert.Equal(2, parsed.Folders[0].Devices[0].Addresses.Count);
    }

    /// <summary>
    /// Tests that LZ4 compression is used for compressible data.
    /// </summary>
    [Fact]
    public async Task BepIntegration_LZ4Compression_WorksCorrectly()
    {
        // Arrange - Create highly compressible data
        using var stream = new MemoryStream();
        var compressibleData = new byte[4096];
        Array.Fill(compressibleData, (byte)'A');

        var response = new Response
        {
            Id = 1,
            Data = ByteString.CopyFrom(compressibleData),
            Code = ErrorCode.NoError
        };

        // Act
        await _serializer.WriteMessageAsync(stream, response, MessageType.Response, allowCompression: true);
        stream.Position = 0;

        // Read header to verify compression
        var headerLength = BinaryPrimitives.ReadUInt16BigEndian(stream.ToArray().AsSpan(0, 2));
        var headerBytes = stream.ToArray().AsSpan(2, headerLength).ToArray();
        var header = Header.Parser.ParseFrom(headerBytes);

        // Assert
        Assert.Equal(MessageCompression.Lz4, header.Compression);

        // Verify round-trip
        stream.Position = 0;
        var (_, message) = await _serializer.ReadMessageAsync(stream);
        var parsed = Assert.IsType<Response>(message);
        Assert.Equal(compressibleData, parsed.Data.ToByteArray());
    }

    /// <summary>
    /// Tests magic number constants match Syncthing.
    /// </summary>
    [Fact]
    public void BepIntegration_MagicConstants_MatchSyncthing()
    {
        // From syncthing/lib/protocol/bep.go
        Assert.Equal(0x2EA7D90Bu, BepProtobufSerializer.BepMagic);
        Assert.Equal(0x9F79BC40u, BepProtobufSerializer.Version13HelloMagic);
        Assert.Equal(0x00010001u, BepProtobufSerializer.OldMagic1);
        Assert.Equal(0x00010000u, BepProtobufSerializer.OldMagic2);
    }

    #endregion

    #region Error Handling Tests

    /// <summary>
    /// Tests that truncated Hello message throws EndOfStreamException.
    /// </summary>
    [Fact]
    public async Task BepIntegration_TruncatedHello_ThrowsEndOfStream()
    {
        // Arrange - Only 3 bytes (incomplete magic)
        using var stream = new MemoryStream(new byte[] { 0x2E, 0xA7, 0xD9 });

        // Act & Assert
        await Assert.ThrowsAsync<EndOfStreamException>(
            async () => await _serializer.ReadHelloAsync(stream));
    }

    /// <summary>
    /// Tests that Hello message exceeding max length is rejected.
    /// </summary>
    [Fact]
    public async Task BepIntegration_HelloTooLarge_Throws()
    {
        // Arrange - Create header with oversized length (32768)
        using var stream = new MemoryStream();
        var header = new byte[6];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), BepProtobufSerializer.BepMagic);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4, 2), 32768); // Too large
        stream.Write(header);
        stream.Write(new byte[32768]); // Dummy data
        stream.Position = 0;

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await _serializer.ReadHelloAsync(stream));
        Assert.Contains("Hello message too big", ex.Message);
    }

    /// <summary>
    /// Tests that message exceeding max size (16MB) is rejected.
    /// </summary>
    [Fact]
    public void BepIntegration_MessageTooLarge_Throws()
    {
        // Arrange - Create data with message length exceeding max
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

    #region Index Message Integration Tests

    /// <summary>
    /// Tests Index message with file entries after ClusterConfig exchange.
    /// </summary>
    [Fact]
    public async Task BepIntegration_IndexWithFiles_AfterClusterConfig()
    {
        // Arrange
        using var stream = new MemoryStream();

        // Create index with multiple files
        var index = new ProtoIndex
        {
            Folder = "sync-folder",
            LastSequence = 100
        };

        // Add regular file
        var file = new ProtoFileInfo
        {
            Name = "documents/report.pdf",
            Type = FileInfoType.File,
            Size = 1048576, // 1 MB
            Permissions = 0x1A4, // 0644
            ModifiedS = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Deleted = false,
            Sequence = 50,
            BlockSize = 131072 // 128 KB
        };
        file.Blocks.Add(new BlockInfo
        {
            Offset = 0,
            Size = 131072,
            Hash = ByteString.CopyFrom(new byte[32])
        });
        index.Files.Add(file);

        // Add directory
        index.Files.Add(new ProtoFileInfo
        {
            Name = "documents",
            Type = FileInfoType.Directory,
            Permissions = 0x1ED, // 0755
            Sequence = 49
        });

        // Add deleted file
        index.Files.Add(new ProtoFileInfo
        {
            Name = "old-file.txt",
            Type = FileInfoType.File,
            Deleted = true,
            Sequence = 99
        });

        // Act
        await _serializer.WriteMessageAsync(stream, index, MessageType.Index);
        stream.Position = 0;
        var (header, message) = await _serializer.ReadMessageAsync(stream);

        // Assert
        Assert.Equal(MessageType.Index, header.Type);
        var parsed = Assert.IsType<ProtoIndex>(message);
        Assert.Equal("sync-folder", parsed.Folder);
        Assert.Equal(100, parsed.LastSequence);
        Assert.Equal(3, parsed.Files.Count);

        // Regular file
        Assert.Equal("documents/report.pdf", parsed.Files[0].Name);
        Assert.Equal(FileInfoType.File, parsed.Files[0].Type);
        Assert.Equal(1048576, parsed.Files[0].Size);
        Assert.Single(parsed.Files[0].Blocks);

        // Directory
        Assert.Equal("documents", parsed.Files[1].Name);
        Assert.Equal(FileInfoType.Directory, parsed.Files[1].Type);
        Assert.Empty(parsed.Files[1].Blocks);

        // Deleted file
        Assert.Equal("old-file.txt", parsed.Files[2].Name);
        Assert.True(parsed.Files[2].Deleted);
    }

    #endregion

    #region Request/Response Integration Tests

    /// <summary>
    /// Tests Request and Response message exchange for block transfer.
    /// </summary>
    [Fact]
    public async Task BepIntegration_RequestResponse_BlockTransfer()
    {
        // Arrange
        using var stream = new MemoryStream();

        // Create request for a block
        var request = new Request
        {
            Id = 42,
            Folder = "sync-folder",
            Name = "large-file.bin",
            Offset = 131072, // Second block
            Size = 131072, // 128 KB
            Hash = ByteString.CopyFrom(new byte[32]),
            FromTemporary = false,
            BlockNo = 1
        };

        // Create response with block data
        var blockData = new byte[131072];
        Random.Shared.NextBytes(blockData);

        var response = new Response
        {
            Id = 42,
            Data = ByteString.CopyFrom(blockData),
            Code = ErrorCode.NoError
        };

        // Act - Write request and response
        await _serializer.WriteMessageAsync(stream, request, MessageType.Request);
        await _serializer.WriteMessageAsync(stream, response, MessageType.Response);

        stream.Position = 0;

        var (reqHeader, reqMessage) = await _serializer.ReadMessageAsync(stream);
        var (respHeader, respMessage) = await _serializer.ReadMessageAsync(stream);

        // Assert - Request
        Assert.Equal(MessageType.Request, reqHeader.Type);
        var parsedRequest = Assert.IsType<Request>(reqMessage);
        Assert.Equal(42, parsedRequest.Id);
        Assert.Equal("large-file.bin", parsedRequest.Name);
        Assert.Equal(131072, parsedRequest.Offset);
        Assert.Equal(1, parsedRequest.BlockNo);

        // Assert - Response
        Assert.Equal(MessageType.Response, respHeader.Type);
        var parsedResponse = Assert.IsType<Response>(respMessage);
        Assert.Equal(42, parsedResponse.Id);
        Assert.Equal(ErrorCode.NoError, parsedResponse.Code);
        Assert.Equal(blockData, parsedResponse.Data.ToByteArray());
    }

    /// <summary>
    /// Tests Response error codes.
    /// </summary>
    [Theory]
    [InlineData(ErrorCode.NoError)]
    [InlineData(ErrorCode.Generic)]
    [InlineData(ErrorCode.NoSuchFile)]
    [InlineData(ErrorCode.InvalidFile)]
    public async Task BepIntegration_ResponseErrorCodes_RoundTrip(ErrorCode errorCode)
    {
        // Arrange
        using var stream = new MemoryStream();
        var response = new Response
        {
            Id = 1,
            Code = errorCode,
            Data = ByteString.Empty
        };

        // Act
        await _serializer.WriteMessageAsync(stream, response, MessageType.Response);
        stream.Position = 0;
        var (_, message) = await _serializer.ReadMessageAsync(stream);

        // Assert
        var parsed = Assert.IsType<Response>(message);
        Assert.Equal(errorCode, parsed.Code);
    }

    #endregion

    #region Control Messages Integration Tests

    /// <summary>
    /// Tests Ping/Close control messages.
    /// </summary>
    [Fact]
    public async Task BepIntegration_ControlMessages_PingAndClose()
    {
        // Arrange
        using var stream = new MemoryStream();

        var ping = new Ping();
        var close = new Close { Reason = "Connection terminated by user" };

        // Act
        await _serializer.WriteMessageAsync(stream, ping, MessageType.Ping);
        await _serializer.WriteMessageAsync(stream, close, MessageType.Close);

        stream.Position = 0;

        var (pingHeader, pingMessage) = await _serializer.ReadMessageAsync(stream);
        var (closeHeader, closeMessage) = await _serializer.ReadMessageAsync(stream);

        // Assert - Ping
        Assert.Equal(MessageType.Ping, pingHeader.Type);
        Assert.IsType<Ping>(pingMessage);

        // Assert - Close
        Assert.Equal(MessageType.Close, closeHeader.Type);
        var parsedClose = Assert.IsType<Close>(closeMessage);
        Assert.Equal("Connection terminated by user", parsedClose.Reason);
    }

    /// <summary>
    /// Tests DownloadProgress message.
    /// </summary>
    [Fact]
    public async Task BepIntegration_DownloadProgress_RoundTrip()
    {
        // Arrange
        using var stream = new MemoryStream();
        var progress = new DownloadProgress
        {
            Folder = "sync-folder"
        };
        progress.Updates.Add(new FileDownloadProgressUpdate
        {
            UpdateType = FileDownloadProgressUpdateType.Append,
            Name = "downloading-file.bin",
            BlockSize = 131072
        });
        progress.Updates[0].BlockIndexes.AddRange(new[] { 0, 1, 2, 3, 4 });

        // Act
        await _serializer.WriteMessageAsync(stream, progress, MessageType.DownloadProgress);
        stream.Position = 0;
        var (header, message) = await _serializer.ReadMessageAsync(stream);

        // Assert
        Assert.Equal(MessageType.DownloadProgress, header.Type);
        var parsed = Assert.IsType<DownloadProgress>(message);
        Assert.Equal("sync-folder", parsed.Folder);
        Assert.Single(parsed.Updates);
        Assert.Equal("downloading-file.bin", parsed.Updates[0].Name);
        Assert.Equal(5, parsed.Updates[0].BlockIndexes.Count);
    }

    #endregion

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            try { client.Dispose(); } catch { }
        }
        try { _listener?.Stop(); } catch { }
    }
}
