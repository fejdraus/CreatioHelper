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
