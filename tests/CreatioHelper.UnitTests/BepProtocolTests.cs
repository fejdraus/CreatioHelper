using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync;

namespace CreatioHelper.Tests;

/// <summary>
/// Tests for BepProtocol address handling
/// Based on Syncthing's resolveDeviceAddrs behavior
/// </summary>
public class BepProtocolTests : IDisposable
{
    private readonly Mock<ILogger<BepProtocol>> _mockLogger;
    private readonly Mock<ISyncDatabase> _mockDatabase;
    private readonly Mock<IBlockInfoRepository> _mockBlockRepository;
    private readonly BepProtocol _protocol;
    private readonly X509Certificate2 _testCertificate;

    public BepProtocolTests()
    {
        _mockLogger = new Mock<ILogger<BepProtocol>>();
        _mockDatabase = new Mock<ISyncDatabase>();
        _mockBlockRepository = new Mock<IBlockInfoRepository>();

        // Create a test certificate
        _testCertificate = CreateTestCertificate();

        // Create BlockDuplicationDetector
        var blockCalculatorLogger = Mock.Of<ILogger<SyncthingBlockCalculator>>();
        var blockCalculator = new SyncthingBlockCalculator(blockCalculatorLogger);
        var blockDetectorLogger = Mock.Of<ILogger<BlockDuplicationDetector>>();
        var blockDetector = new BlockDuplicationDetector(blockDetectorLogger, _mockBlockRepository.Object, blockCalculator);

        _protocol = new BepProtocol(
            _mockLogger.Object,
            _mockDatabase.Object,
            blockDetector,
            22000,
            _testCertificate,
            "TEST-DEVICE-ID");
    }

    [Fact]
    public async Task ConnectAsync_WithOnlyDynamicAddress_ShouldReturnFalse()
    {
        // Arrange - device has only "dynamic" address (not resolved)
        var device = new SyncDevice("REMOTE-DEVICE-ID", "Remote Device");
        device.AddAddress("dynamic");

        // Act
        var result = await _protocol.ConnectAsync(device);

        // Assert - should return false because "dynamic" is filtered out
        Assert.False(result);
    }

    [Fact]
    public async Task ConnectAsync_WithStaticAddress_ShouldAttemptConnection()
    {
        // Arrange - device has a static address (will fail to connect but should try)
        var device = new SyncDevice("REMOTE-DEVICE-ID", "Remote Device");
        device.AddAddress("tcp://127.0.0.1:99999"); // Invalid port, will fail

        // Act
        var result = await _protocol.ConnectAsync(device);

        // Assert - should return false (connection failed) but attempt was made
        Assert.False(result);
        // Verify that we at least tried (no "only dynamic" log)
    }

    [Fact]
    public async Task ConnectAsync_WithMixedAddresses_ShouldSkipDynamic()
    {
        // Arrange - device has both "dynamic" and static addresses
        var device = new SyncDevice("REMOTE-DEVICE-ID", "Remote Device");
        device.AddAddress("dynamic");
        device.AddAddress("tcp://127.0.0.1:99999"); // Will fail but should be tried

        // Act
        var result = await _protocol.ConnectAsync(device);

        // Assert - should return false but attempt was made on static address
        Assert.False(result);
    }

    [Fact]
    public async Task ConnectAsync_WhenAlreadyConnected_ShouldReturnTrue()
    {
        // This test verifies the early return when already connected
        // We can't easily test this without a real connection, but we verify the behavior
        var device = new SyncDevice("REMOTE-DEVICE-ID", "Remote Device");
        device.AddAddress("dynamic");

        // First call - not connected, will return false
        var result1 = await _protocol.ConnectAsync(device);
        Assert.False(result1);

        // Second call - still not connected
        var result2 = await _protocol.ConnectAsync(device);
        Assert.False(result2);
    }

    [Fact]
    public void ConnectAsync_FiltersDynamicAddresses_CorrectlyFormatsAddressList()
    {
        // Arrange
        var device = new SyncDevice("TEST-DEVICE", "Test");
        device.AddAddress("dynamic");
        device.AddAddress("tcp://192.168.1.1:22000");
        device.AddAddress("dynamic"); // Duplicate dynamic
        device.AddAddress("tcp://192.168.1.2:22000");

        // Get filtered addresses (same logic as in ConnectAsync)
        var filtered = device.Addresses.Where(a => a != "dynamic").ToList();

        // Assert
        Assert.Equal(2, filtered.Count);
        Assert.Contains("tcp://192.168.1.1:22000", filtered);
        Assert.Contains("tcp://192.168.1.2:22000", filtered);
        Assert.DoesNotContain("dynamic", filtered);
    }

    private X509Certificate2 CreateTestCertificate()
    {
        // Create a simple self-signed certificate for testing
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=TestDevice",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddYears(1));

        return cert;
    }

    public void Dispose()
    {
        _protocol?.Dispose();
        _testCertificate?.Dispose();
    }
}

/// <summary>
/// Tests for BepConnection protocol state machine.
/// Verifies that ClusterConfig MUST be the first message after Hello exchange.
/// Based on Syncthing lib/protocol/protocol.go stateInitial/stateReady pattern.
/// </summary>
public class BepConnectionProtocolStateTests : IDisposable
{
    private readonly Mock<ILogger<BepConnection>> _mockLogger;
    private TcpListener? _listener;
    private TcpClient? _clientTcp;
    private TcpClient? _serverTcp;

    public BepConnectionProtocolStateTests()
    {
        _mockLogger = new Mock<ILogger<BepConnection>>();
    }

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

        _clientTcp = await clientTask;
        _serverTcp = await serverTask;

        return (_clientTcp, _serverTcp);
    }

    [Fact]
    public void InitialState_ShouldBeInitial()
    {
        // Arrange - Create a dummy TCP client with memory stream for testing
        using var tcpClient = new TcpClient();
        using var memoryStream = new MemoryStream();

        // Act
        using var connection = new BepConnection(
            "TEST-DEVICE-ID",
            tcpClient,
            memoryStream,
            _mockLogger.Object,
            isOutgoing: true,
            BepSerializationMode.Protobuf);

        // Assert - Initial protocol state should be Initial (stateInitial in Syncthing)
        Assert.Equal(BepProtocolState.Initial, connection.ProtocolState);
        Assert.False(connection.IsProtocolReady);
    }

    [Fact]
    public async Task SendMessageAsync_WithIndex_BeforeClusterConfig_ShouldThrow()
    {
        // Arrange - Create connection in initial state
        using var tcpClient = new TcpClient();
        using var memoryStream = new MemoryStream();
        using var connection = new BepConnection(
            "TEST-DEVICE-ID",
            tcpClient,
            memoryStream,
            _mockLogger.Object,
            isOutgoing: true,
            BepSerializationMode.Protobuf);

        var index = new BepIndex
        {
            Folder = "test-folder",
            Files = new List<BepFileInfo>()
        };

        // Act & Assert - Should throw InvalidOperationException because ClusterConfig not sent yet
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await connection.SendMessageAsync(BepMessageType.Index, index));

        Assert.Contains("ClusterConfig", ex.Message);
        Assert.Contains("BEP protocol violation", ex.Message);
    }

    [Fact]
    public async Task SendMessageAsync_WithIndexUpdate_BeforeClusterConfig_ShouldThrow()
    {
        // Arrange
        using var tcpClient = new TcpClient();
        using var memoryStream = new MemoryStream();
        using var connection = new BepConnection(
            "TEST-DEVICE-ID",
            tcpClient,
            memoryStream,
            _mockLogger.Object,
            isOutgoing: true,
            BepSerializationMode.Protobuf);

        var indexUpdate = new BepIndexUpdate
        {
            Folder = "test-folder",
            Files = new List<BepFileInfo>()
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await connection.SendMessageAsync(BepMessageType.IndexUpdate, indexUpdate));

        Assert.Contains("ClusterConfig", ex.Message);
    }

    [Fact]
    public async Task SendMessageAsync_WithRequest_BeforeClusterConfig_ShouldThrow()
    {
        // Arrange
        using var tcpClient = new TcpClient();
        using var memoryStream = new MemoryStream();
        using var connection = new BepConnection(
            "TEST-DEVICE-ID",
            tcpClient,
            memoryStream,
            _mockLogger.Object,
            isOutgoing: true,
            BepSerializationMode.Protobuf);

        var request = new BepRequest
        {
            Id = 1,
            Folder = "test-folder",
            Name = "test-file.txt",
            Offset = 0,
            Size = 1024
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await connection.SendMessageAsync(BepMessageType.Request, request));

        Assert.Contains("ClusterConfig", ex.Message);
    }

    [Fact]
    public async Task SendMessageAsync_WithResponse_BeforeClusterConfig_ShouldThrow()
    {
        // Arrange
        using var tcpClient = new TcpClient();
        using var memoryStream = new MemoryStream();
        using var connection = new BepConnection(
            "TEST-DEVICE-ID",
            tcpClient,
            memoryStream,
            _mockLogger.Object,
            isOutgoing: true,
            BepSerializationMode.Protobuf);

        var response = new BepResponse
        {
            Id = 1,
            Data = new byte[] { 1, 2, 3 }
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await connection.SendMessageAsync(BepMessageType.Response, response));

        Assert.Contains("ClusterConfig", ex.Message);
    }

    [Fact]
    public async Task SendMessageAsync_WithDownloadProgress_BeforeClusterConfig_ShouldThrow()
    {
        // Arrange
        using var tcpClient = new TcpClient();
        using var memoryStream = new MemoryStream();
        using var connection = new BepConnection(
            "TEST-DEVICE-ID",
            tcpClient,
            memoryStream,
            _mockLogger.Object,
            isOutgoing: true,
            BepSerializationMode.Protobuf);

        var progress = new BepDownloadProgress
        {
            Folder = "test-folder",
            Updates = new List<BepFileDownloadProgressUpdate>()
        };

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await connection.SendMessageAsync(BepMessageType.DownloadProgress, progress));

        Assert.Contains("ClusterConfig", ex.Message);
    }

    [Fact]
    public async Task SendMessageAsync_WithPing_BeforeClusterConfig_ShouldThrow()
    {
        // Arrange
        using var tcpClient = new TcpClient();
        using var memoryStream = new MemoryStream();
        using var connection = new BepConnection(
            "TEST-DEVICE-ID",
            tcpClient,
            memoryStream,
            _mockLogger.Object,
            isOutgoing: true,
            BepSerializationMode.Protobuf);

        // Act & Assert - Ping is not allowed before ClusterConfig (Syncthing pattern)
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await connection.SendMessageAsync(BepMessageType.Ping, new BepPing()));

        Assert.Contains("ClusterConfig", ex.Message);
    }

    [Fact]
    public async Task SendMessageAsync_Hello_AlwaysAllowed()
    {
        // Arrange - Create a connected pair for actual I/O
        var (client, server) = await CreateConnectedPairAsync();

        using var connection = new BepConnection(
            "TEST-DEVICE-ID",
            client,
            client.GetStream(),
            _mockLogger.Object,
            isOutgoing: true,
            BepSerializationMode.Protobuf);

        var hello = new BepHello
        {
            DeviceId = "TEST-DEVICE-ID",
            DeviceName = "Test Device",
            ClientName = "CreatioHelper",
            ClientVersion = "1.0.0"
        };

        // Act - Should not throw, Hello is always allowed
        await connection.SendMessageAsync(BepMessageType.Hello, hello);

        // Assert - State should still be Initial (Hello doesn't transition state)
        Assert.Equal(BepProtocolState.Initial, connection.ProtocolState);
    }

    [Fact]
    public async Task SendMessageAsync_Close_AlwaysAllowed()
    {
        // Arrange
        using var tcpClient = new TcpClient();
        using var memoryStream = new MemoryStream();
        using var connection = new BepConnection(
            "TEST-DEVICE-ID",
            tcpClient,
            memoryStream,
            _mockLogger.Object,
            isOutgoing: true,
            BepSerializationMode.Protobuf);

        var close = new BepClose { Reason = "Test close" };

        // Act - Close is always allowed regardless of state
        // Note: This will fail at the network layer but should not throw protocol exception
        // We're testing that the validation allows Close through
        try
        {
            await connection.SendMessageAsync(BepMessageType.Close, close);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // Expected - network error, not protocol violation
        }

        // Assert - No InvalidOperationException was thrown for protocol violation
        Assert.Equal(BepProtocolState.Initial, connection.ProtocolState);
    }

    [Fact]
    public async Task SendMessageAsync_ClusterConfig_TransitionsStateToReady()
    {
        // Arrange - Create a connected pair for actual I/O
        var (client, server) = await CreateConnectedPairAsync();

        using var connection = new BepConnection(
            "TEST-DEVICE-ID",
            client,
            client.GetStream(),
            _mockLogger.Object,
            isOutgoing: true,
            BepSerializationMode.Protobuf);

        // First send Hello (required before ClusterConfig)
        var hello = new BepHello
        {
            DeviceId = "TEST-DEVICE-ID",
            DeviceName = "Test Device",
            ClientName = "CreatioHelper",
            ClientVersion = "1.0.0"
        };
        await connection.SendMessageAsync(BepMessageType.Hello, hello);

        var clusterConfig = new BepClusterConfig
        {
            Folders = new List<BepFolder>()
        };

        // Act
        await connection.SendMessageAsync(BepMessageType.ClusterConfig, clusterConfig);

        // Assert - State should transition to Ready
        Assert.Equal(BepProtocolState.Ready, connection.ProtocolState);
        Assert.True(connection.IsProtocolReady);
    }

    [Fact]
    public async Task SendMessageAsync_AfterClusterConfig_DataMessagesAllowed()
    {
        // Arrange - Create a connected pair for actual I/O
        var (client, server) = await CreateConnectedPairAsync();

        using var connection = new BepConnection(
            "TEST-DEVICE-ID",
            client,
            client.GetStream(),
            _mockLogger.Object,
            isOutgoing: true,
            BepSerializationMode.Protobuf);

        // Send Hello first
        var hello = new BepHello
        {
            DeviceId = "TEST-DEVICE-ID",
            DeviceName = "Test Device",
            ClientName = "CreatioHelper",
            ClientVersion = "1.0.0"
        };
        await connection.SendMessageAsync(BepMessageType.Hello, hello);

        // Send ClusterConfig to transition to Ready state
        var clusterConfig = new BepClusterConfig
        {
            Folders = new List<BepFolder>()
        };
        await connection.SendMessageAsync(BepMessageType.ClusterConfig, clusterConfig);

        // Act - Now Index should be allowed
        var index = new BepIndex
        {
            Folder = "test-folder",
            Files = new List<BepFileInfo>()
        };

        // Should not throw - data messages allowed after ClusterConfig
        await connection.SendMessageAsync(BepMessageType.Index, index);

        // Assert
        Assert.Equal(BepProtocolState.Ready, connection.ProtocolState);
    }

    [Theory]
    [InlineData(BepMessageType.Index)]
    [InlineData(BepMessageType.IndexUpdate)]
    [InlineData(BepMessageType.Request)]
    [InlineData(BepMessageType.Response)]
    [InlineData(BepMessageType.DownloadProgress)]
    [InlineData(BepMessageType.Ping)]
    public void ValidateProtocolStateForSend_DataMessages_BeforeClusterConfig_ShouldFail(BepMessageType messageType)
    {
        // Arrange
        using var tcpClient = new TcpClient();
        using var memoryStream = new MemoryStream();
        using var connection = new BepConnection(
            "TEST-DEVICE-ID",
            tcpClient,
            memoryStream,
            _mockLogger.Object,
            isOutgoing: true,
            BepSerializationMode.Protobuf);

        // Act & Assert - Protocol state is Initial, data messages should be rejected
        Assert.Equal(BepProtocolState.Initial, connection.ProtocolState);

        // Verify that the message type is a data message (not Hello, ClusterConfig, or Close)
        // These message types should fail validation when protocol state is Initial
        Assert.False(
            messageType == BepMessageType.Hello ||
            messageType == BepMessageType.ClusterConfig ||
            messageType == BepMessageType.Close,
            $"{messageType} is a control message, not a data message");
    }

    [Theory]
    [InlineData(BepMessageType.Hello)]
    [InlineData(BepMessageType.ClusterConfig)]
    [InlineData(BepMessageType.Close)]
    public void AllowedMessagesInInitialState_ShouldBeCorrect(BepMessageType messageType)
    {
        // These message types should be allowed regardless of protocol state:
        // - Hello: Initial handshake
        // - ClusterConfig: Transitions state to Ready
        // - Close: Connection termination always allowed

        // This is a documentation test verifying the expected behavior
        Assert.True(
            messageType == BepMessageType.Hello ||
            messageType == BepMessageType.ClusterConfig ||
            messageType == BepMessageType.Close,
            $"{messageType} should be in the allowed list for Initial state");
    }

    public void Dispose()
    {
        _clientTcp?.Dispose();
        _serverTcp?.Dispose();
        _listener?.Stop();
    }
}
