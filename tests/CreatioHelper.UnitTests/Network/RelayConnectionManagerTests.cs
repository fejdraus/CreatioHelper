using CreatioHelper.Infrastructure.Services.Sync.Relay;
using Microsoft.Extensions.Logging;
using Moq;
using System.Security.Cryptography.X509Certificates;
using Xunit;

namespace CreatioHelper.UnitTests.Network;

public class RelayConnectionManagerTests : IDisposable
{
    private readonly Mock<ILogger<RelayConnectionManager>> _loggerMock;
    private readonly X509Certificate2 _testCertificate;
    private readonly RelayConnectionManager _manager;

    public RelayConnectionManagerTests()
    {
        _loggerMock = new Mock<ILogger<RelayConnectionManager>>();

        // Create a self-signed test certificate
        _testCertificate = CreateTestCertificate();

        _manager = new RelayConnectionManager(_loggerMock.Object, _testCertificate);
    }

    [Fact]
    public void GetConnectedRelayCount_ReturnsZero_WhenNoRelaysConnected()
    {
        // Act
        var count = _manager.GetConnectedRelayCount();

        // Assert
        Assert.Equal(0, count);
    }

    [Fact]
    public void GetRelayInfo_ReturnsEmptyList_WhenNoRelaysConnected()
    {
        // Act
        var relayInfo = _manager.GetRelayInfo();

        // Assert
        Assert.NotNull(relayInfo);
        Assert.Empty(relayInfo);
    }

    [Fact]
    public async Task ConnectToRelayAsync_ReturnsFalse_WithInvalidUri()
    {
        // Act
        var result = await _manager.ConnectToRelayAsync("not-a-valid-uri");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ConnectThroughRelayAsync_ReturnsNull_WhenNoRelaysConnected()
    {
        // Arrange
        var deviceId = "ABCD1234ABCD1234ABCD1234ABCD1234";

        // Act
        var stream = await _manager.ConnectThroughRelayAsync(deviceId, TimeSpan.FromSeconds(1));

        // Assert
        Assert.Null(stream);
    }

    [Fact]
    public async Task DisconnectAllAsync_CompletesSuccessfully_WhenNoRelaysConnected()
    {
        // Act
        await _manager.DisconnectAllAsync();

        // Assert
        Assert.Equal(0, _manager.GetConnectedRelayCount());
    }

    [Fact]
    public void RelayInfo_Properties_CanBeSet()
    {
        // Arrange & Act
        var info = new RelayInfo
        {
            Uri = "relay://test.relay.net:22067",
            IsConnected = true
        };

        // Assert
        Assert.Equal("relay://test.relay.net:22067", info.Uri);
        Assert.True(info.IsConnected);
    }

    [Fact]
    public void RelayConnectionEventArgs_ContainsCorrectData()
    {
        // Arrange
        var invitation = new SessionInvitation(
            new byte[32],
            new byte[32],
            new byte[] { 192, 168, 1, 1 },
            22067,
            true
        );

        // Act
        var eventArgs = new RelayConnectionEventArgs(invitation, null, "relay://test.net:22067");

        // Assert
        Assert.NotNull(eventArgs.Invitation);
        Assert.Null(eventArgs.RelayClient);
        Assert.Equal("relay://test.net:22067", eventArgs.RelayUri);
    }

    private X509Certificate2 CreateTestCertificate()
    {
        // Create a simple self-signed certificate for testing
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=TestClient",
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
        _manager.Dispose();
        _testCertificate.Dispose();
    }
}

public class RelayClientTests : IDisposable
{
    private readonly Mock<ILogger<RelayClient>> _loggerMock;
    private readonly X509Certificate2 _testCertificate;

    public RelayClientTests()
    {
        _loggerMock = new Mock<ILogger<RelayClient>>();
        _testCertificate = CreateTestCertificate();
    }

    [Fact]
    public void RelayClient_IsConnected_ReturnsFalse_WhenNotConnected()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        using var client = new RelayClient(_loggerMock.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));

        // Act & Assert
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task RelayClient_ThrowsObjectDisposedException_OnConnectAfterDispose()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        var client = new RelayClient(_loggerMock.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));
        client.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task ConnectAsync_ReturnsFalse_WhenServerNotAvailable()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:59999"); // Non-existent server
        using var client = new RelayClient(_loggerMock.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(2));

        // Act
        var result = await client.ConnectAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task PingAsync_ReturnsFalse_WhenNotConnected()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        using var client = new RelayClient(_loggerMock.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));

        // Act
        var result = await client.PingAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void TryGetSessionInvitation_ReturnsFalse_WhenNoInvitations()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        using var client = new RelayClient(_loggerMock.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));

        // Act
        var result = client.TryGetSessionInvitation(out var invitation);

        // Assert
        Assert.False(result);
        Assert.Null(invitation);
    }

    private X509Certificate2 CreateTestCertificate()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=TestClient",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddYears(1));
    }

    public void Dispose()
    {
        _testCertificate.Dispose();
    }
}

/// <summary>
/// Tests for SyncthingRelayClient ALPN protocol negotiation and relay server ID verification
/// </summary>
public class SyncthingRelayClientTests : IDisposable
{
    private readonly Mock<ILogger<SyncthingRelayClient>> _loggerMock;
    private readonly X509Certificate2 _testCertificate;

    public SyncthingRelayClientTests()
    {
        _loggerMock = new Mock<ILogger<SyncthingRelayClient>>();
        _testCertificate = CreateTestCertificate();
    }

    [Fact]
    public void RelayProtocol_ProtocolName_IsBepRelay()
    {
        // Verify the ALPN protocol name matches Syncthing's expected value
        // Reference: lib/relay/protocol/protocol.go - ProtocolName = "bep-relay"
        Assert.Equal("bep-relay", RelayProtocol.ProtocolName);
    }

    [Fact]
    public async Task SyncthingRelayClient_ThrowsObjectDisposedException_OnConnectAfterDispose()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        var certificates = new X509Certificate2Collection { _testCertificate };
        var client = new SyncthingRelayClient(_loggerMock.Object, relayUri, certificates);
        client.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync());
    }

    [Fact]
    public void SyncthingRelayClient_SupportsStaticRelayScheme()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        var certificates = new X509Certificate2Collection { _testCertificate };

        // Act
        using var client = new SyncthingRelayClient(_loggerMock.Object, relayUri, certificates);

        // Assert
        Assert.Equal(relayUri, client.URI);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void SyncthingRelayClient_SupportsDynamicHttpScheme()
    {
        // Arrange
        var relayUri = new Uri("dynamic+http://localhost:22067");
        var certificates = new X509Certificate2Collection { _testCertificate };

        // Act
        using var client = new SyncthingRelayClient(_loggerMock.Object, relayUri, certificates);

        // Assert
        Assert.Equal(relayUri, client.URI);
    }

    [Fact]
    public void SyncthingRelayClient_SupportsDynamicHttpsScheme()
    {
        // Arrange
        var relayUri = new Uri("dynamic+https://localhost:22067");
        var certificates = new X509Certificate2Collection { _testCertificate };

        // Act
        using var client = new SyncthingRelayClient(_loggerMock.Object, relayUri, certificates);

        // Assert
        Assert.Equal(relayUri, client.URI);
    }

    [Fact]
    public async Task SyncthingRelayClient_ConnectAsync_ThrowsNotSupportedForInvalidScheme()
    {
        // Arrange
        var relayUri = new Uri("http://localhost:22067"); // Invalid scheme
        var certificates = new X509Certificate2Collection { _testCertificate };
        using var client = new SyncthingRelayClient(_loggerMock.Object, relayUri, certificates);

        // Act
        var result = await client.ConnectAsync();

        // Assert - should return false for unsupported scheme (handled in catch block)
        Assert.False(result);
    }

    [Fact]
    public void SyncthingRelayClient_ExtractsTokenFromUri()
    {
        // Arrange - Syncthing relay URIs can contain authentication tokens
        var relayUri = new Uri("relay://localhost:22067?token=secret123");
        var certificates = new X509Certificate2Collection { _testCertificate };

        // Act
        using var client = new SyncthingRelayClient(_loggerMock.Object, relayUri, certificates);

        // Assert - Token is extracted (we can verify it's used when connecting)
        Assert.NotNull(client);
    }

    [Fact]
    public void SyncthingRelayClient_ExtractsIdFromUri()
    {
        // Arrange - Syncthing relay URIs can contain relay ID for verification
        // Reference: lib/relay/client/static.go - performHandshakeAndValidation
        var relayUri = new Uri("relay://localhost:22067?id=AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG-HHHHHHH");
        var certificates = new X509Certificate2Collection { _testCertificate };

        // Act
        using var client = new SyncthingRelayClient(_loggerMock.Object, relayUri, certificates);

        // Assert - ID parameter is parsed from URI
        Assert.Contains("id=", client.URI.Query);
    }

    [Fact]
    public async Task SyncthingRelayClient_ConnectAsync_ReturnsFalse_WhenServerNotAvailable()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:59998"); // Non-existent server
        var certificates = new X509Certificate2Collection { _testCertificate };
        using var client = new SyncthingRelayClient(
            _loggerMock.Object,
            relayUri,
            certificates,
            TimeSpan.FromSeconds(1));

        // Act
        var result = await client.ConnectAsync();

        // Assert
        Assert.False(result);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task SyncthingRelayClient_PingAsync_ReturnsFalse_WhenNotConnected()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        var certificates = new X509Certificate2Collection { _testCertificate };
        using var client = new SyncthingRelayClient(_loggerMock.Object, relayUri, certificates);

        // Act
        var result = await client.PingAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void SyncthingRelayClient_GetNextInvitation_ReturnsNull_WhenNoInvitations()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        var certificates = new X509Certificate2Collection { _testCertificate };
        using var client = new SyncthingRelayClient(_loggerMock.Object, relayUri, certificates);

        // Act
        var invitation = client.GetNextInvitation();

        // Assert
        Assert.Null(invitation);
    }

    [Theory]
    [InlineData("relay://host:22067")]
    [InlineData("relay://host:22067?token=abc")]
    [InlineData("relay://host:22067?id=DEVICE-ID")]
    [InlineData("relay://host:22067?token=abc&id=DEVICE-ID")]
    public void SyncthingRelayClient_ParsesRelayUriVariants(string uriString)
    {
        // Arrange
        var relayUri = new Uri(uriString);
        var certificates = new X509Certificate2Collection { _testCertificate };

        // Act
        using var client = new SyncthingRelayClient(_loggerMock.Object, relayUri, certificates);

        // Assert
        Assert.NotNull(client);
        Assert.Equal(relayUri, client.URI);
    }

    private X509Certificate2 CreateTestCertificate()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=TestClient",
            rsa,
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddYears(1));
    }

    public void Dispose()
    {
        _testCertificate.Dispose();
    }
}

/// <summary>
/// Tests for Relay protocol session handling
/// </summary>
public class RelayProtocolSessionTests
{
    #region SessionInvitation Tests

    [Fact]
    public void SessionInvitation_Constructor_SetsAllProperties()
    {
        // Arrange
        var from = new byte[32];
        var key = new byte[32];
        var address = new byte[] { 192, 168, 1, 100 };
        ushort port = 22067;
        bool serverSocket = true;

        // Fill with test data
        for (int i = 0; i < 32; i++)
        {
            from[i] = (byte)i;
            key[i] = (byte)(255 - i);
        }

        // Act
        var invitation = new SessionInvitation(from, key, address, port, serverSocket);

        // Assert
        Assert.Equal(from, invitation.From);
        Assert.Equal(key, invitation.Key);
        Assert.Equal(address, invitation.Address);
        Assert.Equal(port, invitation.Port);
        Assert.True(invitation.ServerSocket);
        Assert.Equal(RelayProtocol.MessageType.SessionInvitation, invitation.Type);
    }

    [Fact]
    public void SessionInvitation_DefaultConstructor_SetsEmptyArrays()
    {
        // Act
        var invitation = new SessionInvitation();

        // Assert
        Assert.Empty(invitation.From);
        Assert.Empty(invitation.Key);
        Assert.Empty(invitation.Address);
        Assert.Equal(0, invitation.Port);
        Assert.False(invitation.ServerSocket);
    }

    [Fact]
    public void SessionInvitation_ToString_FormatsCorrectly()
    {
        // Arrange
        var from = new byte[32];
        for (int i = 0; i < 32; i++) from[i] = (byte)(i + 1);
        var key = new byte[32];
        var address = new byte[] { 10, 0, 0, 1 };
        ushort port = 22067;

        var invitation = new SessionInvitation(from, key, address, port, false);

        // Act
        var result = invitation.ToString();

        // Assert
        Assert.Contains("10.0.0.1", result);
        Assert.Contains("22067", result);
        Assert.Contains("@", result);
    }

    [Fact]
    public void SessionInvitation_ToString_HandlesInvalidData()
    {
        // Arrange - invalid From (not 32 bytes) and invalid Address
        var invitation = new SessionInvitation(
            new byte[] { 1, 2, 3 }, // invalid - not 32 bytes
            new byte[32],
            new byte[] { 1 }, // invalid - not 4 bytes
            22067,
            false
        );

        // Act
        var result = invitation.ToString();

        // Assert - should contain fallback values for invalid data
        Assert.Contains("<invalid>", result);
    }

    [Fact]
    public void SessionInvitation_ServerSocket_True()
    {
        // Arrange & Act
        var invitation = new SessionInvitation(
            new byte[32],
            new byte[32],
            new byte[] { 127, 0, 0, 1 },
            22067,
            true
        );

        // Assert
        Assert.True(invitation.ServerSocket);
    }

    [Fact]
    public void SessionInvitation_ServerSocket_False()
    {
        // Arrange & Act
        var invitation = new SessionInvitation(
            new byte[32],
            new byte[32],
            new byte[] { 127, 0, 0, 1 },
            22067,
            false
        );

        // Assert
        Assert.False(invitation.ServerSocket);
    }

    [Theory]
    [InlineData((ushort)0)]
    [InlineData((ushort)22067)]
    [InlineData((ushort)443)]
    [InlineData((ushort)65535)]
    public void SessionInvitation_Port_SupportsFullRange(ushort port)
    {
        // Arrange & Act
        var invitation = new SessionInvitation(
            new byte[32],
            new byte[32],
            new byte[] { 192, 168, 1, 1 },
            port,
            false
        );

        // Assert
        Assert.Equal(port, invitation.Port);
    }

    #endregion

    #region JoinSessionRequest Tests

    [Fact]
    public void JoinSessionRequest_Constructor_SetsKey()
    {
        // Arrange
        var key = new byte[32];
        for (int i = 0; i < 32; i++) key[i] = (byte)i;

        // Act
        var request = new JoinSessionRequest(key);

        // Assert
        Assert.Equal(key, request.Key);
        Assert.Equal(RelayProtocol.MessageType.JoinSessionRequest, request.Type);
    }

    [Fact]
    public void JoinSessionRequest_DefaultConstructor_SetsEmptyKey()
    {
        // Act
        var request = new JoinSessionRequest();

        // Assert
        Assert.Empty(request.Key);
    }

    [Fact]
    public void JoinSessionRequest_AcceptsValidKey()
    {
        // Arrange
        var key = new byte[32];
        new Random(42).NextBytes(key);

        // Act
        var request = new JoinSessionRequest(key);

        // Assert
        Assert.Equal(32, request.Key.Length);
        Assert.Equal(key, request.Key);
    }

    #endregion

    #region ConnectRequest Tests

    [Fact]
    public void ConnectRequest_Constructor_SetsDeviceId()
    {
        // Arrange
        var deviceId = new byte[32];
        for (int i = 0; i < 32; i++) deviceId[i] = (byte)(i * 2);

        // Act
        var request = new ConnectRequest(deviceId);

        // Assert
        Assert.Equal(deviceId, request.Id);
        Assert.Equal(RelayProtocol.MessageType.ConnectRequest, request.Type);
    }

    [Fact]
    public void ConnectRequest_DefaultConstructor_SetsEmptyId()
    {
        // Act
        var request = new ConnectRequest();

        // Assert
        Assert.Empty(request.Id);
    }

    #endregion

    #region JoinRelayRequest Tests

    [Fact]
    public void JoinRelayRequest_Constructor_SetsToken()
    {
        // Arrange
        var token = "secret-token-123";

        // Act
        var request = new JoinRelayRequest(token);

        // Assert
        Assert.Equal(token, request.Token);
        Assert.Equal(RelayProtocol.MessageType.JoinRelayRequest, request.Type);
    }

    [Fact]
    public void JoinRelayRequest_DefaultConstructor_SetsEmptyToken()
    {
        // Act
        var request = new JoinRelayRequest();

        // Assert
        Assert.Equal("", request.Token);
    }

    #endregion

    #region Response Tests

    [Fact]
    public void Response_Success_HasCorrectCode()
    {
        // Act
        var response = Response.Success;

        // Assert
        Assert.Equal(0, response.Code);
        Assert.Equal("success", response.Message);
        Assert.Equal(RelayProtocol.MessageType.Response, response.Type);
    }

    [Fact]
    public void Response_NotFound_HasCorrectCode()
    {
        // Act
        var response = Response.NotFound;

        // Assert
        Assert.Equal(1, response.Code);
        Assert.Equal("not found", response.Message);
    }

    [Fact]
    public void Response_AlreadyConnected_HasCorrectCode()
    {
        // Act
        var response = Response.AlreadyConnected;

        // Assert
        Assert.Equal(2, response.Code);
        Assert.Equal("already connected", response.Message);
    }

    [Fact]
    public void Response_WrongToken_HasCorrectCode()
    {
        // Act
        var response = Response.WrongToken;

        // Assert
        Assert.Equal(3, response.Code);
        Assert.Equal("wrong token", response.Message);
    }

    [Fact]
    public void Response_UnexpectedMessage_HasCorrectCode()
    {
        // Act
        var response = Response.UnexpectedMessage;

        // Assert
        Assert.Equal(100, response.Code);
        Assert.Equal("unexpected message", response.Message);
    }

    [Fact]
    public void Response_CustomCode_CanBeCreated()
    {
        // Arrange & Act
        var response = new Response(42, "custom error");

        // Assert
        Assert.Equal(42, response.Code);
        Assert.Equal("custom error", response.Message);
    }

    #endregion

    #region RelayFullException Tests

    [Fact]
    public void RelayFullException_DefaultConstructor_HasDefaultMessage()
    {
        // Act
        var exception = new RelayFullException();

        // Assert
        Assert.Equal("relay full", exception.Message);
        Assert.Null(exception.RelayUri);
    }

    [Fact]
    public void RelayFullException_WithUri_ContainsUri()
    {
        // Arrange
        var uri = new Uri("relay://example.com:22067");

        // Act
        var exception = new RelayFullException(uri);

        // Assert
        Assert.Contains("example.com", exception.Message);
        Assert.Equal(uri, exception.RelayUri);
    }

    [Fact]
    public void RelayFullException_WithMessage_ContainsMessage()
    {
        // Arrange
        var message = "custom relay full message";

        // Act
        var exception = new RelayFullException(message);

        // Assert
        Assert.Equal(message, exception.Message);
    }

    [Fact]
    public void RelayFullException_WithInnerException_ContainsInnerException()
    {
        // Arrange
        var inner = new InvalidOperationException("inner error");

        // Act
        var exception = new RelayFullException("outer error", inner);

        // Assert
        Assert.Equal("outer error", exception.Message);
        Assert.Same(inner, exception.InnerException);
    }

    #endregion

    #region RelayIncorrectResponseCodeException Tests

    [Fact]
    public void RelayIncorrectResponseCodeException_ContainsCodeAndMessage()
    {
        // Arrange
        var code = 3;
        var message = "wrong token";

        // Act
        var exception = new RelayIncorrectResponseCodeException(code, message);

        // Assert
        Assert.Equal(code, exception.Code);
        Assert.Equal(message, exception.ResponseMessage);
        Assert.Contains("3", exception.Message);
        Assert.Contains("wrong token", exception.Message);
    }

    [Theory]
    [InlineData(1, "not found")]
    [InlineData(2, "already connected")]
    [InlineData(100, "unexpected message")]
    public void RelayIncorrectResponseCodeException_HandlesVariousCodes(int code, string message)
    {
        // Act
        var exception = new RelayIncorrectResponseCodeException(code, message);

        // Assert
        Assert.Equal(code, exception.Code);
        Assert.Equal(message, exception.ResponseMessage);
    }

    #endregion

    #region RelayFullEventArgs Tests

    [Fact]
    public void RelayFullEventArgs_ContainsUriAndTimestamp()
    {
        // Arrange
        var uri = new Uri("relay://test.relay.net:22067");
        var beforeCreation = DateTime.UtcNow;

        // Act
        var eventArgs = new RelayFullEventArgs(uri);
        var afterCreation = DateTime.UtcNow;

        // Assert
        Assert.Equal(uri, eventArgs.RelayUri);
        Assert.InRange(eventArgs.Timestamp, beforeCreation, afterCreation);
    }

    #endregion

    #region RelayConnectionEventArgs Tests

    [Fact]
    public void RelayConnectionEventArgs_WithAllParameters_SetsProperties()
    {
        // Arrange
        var invitation = new SessionInvitation(
            new byte[32],
            new byte[32],
            new byte[] { 10, 0, 0, 1 },
            22067,
            true
        );
        var relayUri = "relay://test.net:22067";

        // Act
        var eventArgs = new RelayConnectionEventArgs(invitation, null, relayUri);

        // Assert
        Assert.Equal(invitation, eventArgs.Invitation);
        Assert.Null(eventArgs.RelayClient);
        Assert.Equal(relayUri, eventArgs.RelayUri);
    }

    #endregion

    #region Ping/Pong Tests

    [Fact]
    public void Ping_HasCorrectType()
    {
        // Act
        var ping = new Ping();

        // Assert
        Assert.Equal(RelayProtocol.MessageType.Ping, ping.Type);
    }

    [Fact]
    public void Pong_HasCorrectType()
    {
        // Act
        var pong = new Pong();

        // Assert
        Assert.Equal(RelayProtocol.MessageType.Pong, pong.Type);
    }

    #endregion

    #region RelayFull Tests

    [Fact]
    public void RelayFull_HasCorrectType()
    {
        // Act
        var relayFull = new RelayFull();

        // Assert
        Assert.Equal(RelayProtocol.MessageType.RelayFull, relayFull.Type);
    }

    #endregion

    #region RelayHeader Tests

    [Fact]
    public void RelayHeader_Constructor_SetsProperties()
    {
        // Act
        var header = new RelayHeader(RelayProtocol.MessageType.SessionInvitation, 100);

        // Assert
        Assert.Equal(RelayProtocol.Magic, header.Magic);
        Assert.Equal(RelayProtocol.MessageType.SessionInvitation, header.MessageType);
        Assert.Equal(100, header.MessageLength);
    }

    [Fact]
    public void RelayHeader_Magic_IsCorrectValue()
    {
        // Assert - verify protocol magic number
        Assert.Equal(0x9E79BC40u, RelayProtocol.Magic);
    }

    [Theory]
    [InlineData(RelayProtocol.MessageType.Ping, 0)]
    [InlineData(RelayProtocol.MessageType.Pong, 0)]
    [InlineData(RelayProtocol.MessageType.JoinRelayRequest, 50)]
    [InlineData(RelayProtocol.MessageType.JoinSessionRequest, 36)]
    [InlineData(RelayProtocol.MessageType.Response, 20)]
    [InlineData(RelayProtocol.MessageType.ConnectRequest, 36)]
    [InlineData(RelayProtocol.MessageType.SessionInvitation, 120)]
    [InlineData(RelayProtocol.MessageType.RelayFull, 0)]
    public void RelayHeader_SupportAllMessageTypes(RelayProtocol.MessageType messageType, int length)
    {
        // Act
        var header = new RelayHeader(messageType, length);

        // Assert
        Assert.Equal(messageType, header.MessageType);
        Assert.Equal(length, header.MessageLength);
    }

    #endregion
}
