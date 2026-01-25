using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CreatioHelper.Infrastructure.Services.Sync.Relay;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.Tests;

/// <summary>
/// Unit tests for RelayClient session establishment and management.
/// Following Syncthing relay protocol patterns and testing session lifecycle.
/// </summary>
public class RelayClientSessionTests : IDisposable
{
    private readonly Mock<ILogger<RelayClient>> _mockLogger;
    private readonly X509Certificate2 _testCertificate;

    public RelayClientSessionTests()
    {
        _mockLogger = new Mock<ILogger<RelayClient>>();
        _testCertificate = CreateTestCertificate();
    }

    #region Constructor and Initialization Tests

    [Fact]
    public void Constructor_WithValidParameters_CreatesClient()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        var timeout = TimeSpan.FromSeconds(30);

        // Act
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, timeout);

        // Assert
        Assert.NotNull(client);
        Assert.False(client.IsConnected);
    }

    [Theory]
    [InlineData("relay://localhost:22067")]
    [InlineData("relay://192.168.1.1:22067")]
    [InlineData("relay://relay.syncthing.net:443")]
    public void Constructor_WithVariousUris_CreatesClient(string uriString)
    {
        // Arrange
        var relayUri = new Uri(uriString);
        var timeout = TimeSpan.FromSeconds(10);

        // Act
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, timeout);

        // Assert
        Assert.NotNull(client);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    [InlineData(60)]
    [InlineData(300)]
    public void Constructor_WithVariousTimeouts_CreatesClient(int timeoutSeconds)
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        var timeout = TimeSpan.FromSeconds(timeoutSeconds);

        // Act
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, timeout);

        // Assert
        Assert.NotNull(client);
    }

    #endregion

    #region IsConnected Property Tests

    [Fact]
    public void IsConnected_BeforeConnect_ReturnsFalse()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));

        // Act & Assert
        Assert.False(client.IsConnected);
    }

    [Fact]
    public void IsConnected_AfterDispose_ReturnsFalse()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));

        // Act
        client.Dispose();

        // Assert
        Assert.False(client.IsConnected);
    }

    #endregion

    #region ConnectAsync Tests

    [Fact]
    public async Task ConnectAsync_AfterDispose_ThrowsObjectDisposedException()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));
        client.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.ConnectAsync());
    }

    [Fact]
    public async Task ConnectAsync_WithToken_DoesNotThrow()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:59997"); // Non-existent port
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(1));

        // Act - should not throw, just return false
        var result = await client.ConnectAsync("test-token");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ConnectAsync_ToNonExistentServer_ReturnsFalse()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:59996"); // Non-existent server
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(2));

        // Act
        var result = await client.ConnectAsync();

        // Assert
        Assert.False(result);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_WithNullToken_UsesEmptyToken()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:59995"); // Non-existent server
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(1));

        // Act - null token should be handled gracefully
        var result = await client.ConnectAsync();

        // Assert
        Assert.False(result); // Connection fails but no exception
    }

    #endregion

    #region RequestConnectionAsync Tests

    [Fact]
    public async Task RequestConnectionAsync_WhenNotConnected_ThrowsInvalidOperationException()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));
        var deviceId = new byte[32];
        Random.Shared.NextBytes(deviceId);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RequestConnectionAsync(deviceId, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task RequestConnectionAsync_WithEmptyDeviceId_ThrowsInvalidOperationException()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));
        var emptyDeviceId = Array.Empty<byte>();

        // Act & Assert - should throw because not connected (InvalidOperationException)
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.RequestConnectionAsync(emptyDeviceId, TimeSpan.FromSeconds(5)));
    }

    #endregion

    #region TryGetSessionInvitation Tests

    [Fact]
    public void TryGetSessionInvitation_WhenNoInvitations_ReturnsFalse()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));

        // Act
        var result = client.TryGetSessionInvitation(out var invitation);

        // Assert
        Assert.False(result);
        Assert.Null(invitation);
    }

    [Fact]
    public void TryGetSessionInvitation_MultipleCalls_ReturnsFalseConsistently()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));

        // Act & Assert - multiple calls should all return false when no invitations
        for (int i = 0; i < 5; i++)
        {
            var result = client.TryGetSessionInvitation(out var invitation);
            Assert.False(result);
            Assert.Null(invitation);
        }
    }

    #endregion

    #region PingAsync Tests

    [Fact]
    public async Task PingAsync_WhenNotConnected_ReturnsFalse()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));

        // Act
        var result = await client.PingAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task PingAsync_MultipleCalls_AllReturnFalseWhenNotConnected()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));

        // Act & Assert
        for (int i = 0; i < 3; i++)
        {
            var result = await client.PingAsync();
            Assert.False(result);
        }
    }

    #endregion

    #region DisconnectAsync Tests

    [Fact]
    public async Task DisconnectAsync_WhenNotConnected_CompletesSuccessfully()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));

        // Act - should complete without throwing
        await client.DisconnectAsync();

        // Assert
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task DisconnectAsync_MultipleCalls_CompletesWithoutException()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));

        // Act & Assert - multiple disconnect calls should be safe
        await client.DisconnectAsync();
        await client.DisconnectAsync();
        await client.DisconnectAsync();
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task DisconnectAsync_AfterDispose_DoesNotThrow()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));
        client.Dispose();

        // Act & Assert - should not throw
        await client.DisconnectAsync();
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_WhenNotConnected_CompletesWithoutException()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));

        // Act & Assert - should not throw
        client.Dispose();
    }

    [Fact]
    public void Dispose_MultipleCalls_DoesNotThrow()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));

        // Act & Assert - multiple dispose calls should be safe
        client.Dispose();
        client.Dispose();
        client.Dispose();
    }

    [Fact]
    public void Dispose_SetsIsConnectedToFalse()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));

        // Act
        client.Dispose();

        // Assert
        Assert.False(client.IsConnected);
    }

    #endregion

    #region SessionInvitationReceived Event Tests

    [Fact]
    public void SessionInvitationReceived_EventCanBeSubscribed()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));
        SessionInvitation? receivedInvitation = null;

        // Act - subscribe to event
        client.SessionInvitationReceived += (_, invitation) => receivedInvitation = invitation;

        // Assert - subscription should not throw
        Assert.Null(receivedInvitation); // No invitation received yet
    }

    [Fact]
    public void SessionInvitationReceived_MultipleSubscribers_DoesNotThrow()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));
        var invocationCount = 0;

        // Act - subscribe multiple handlers
        client.SessionInvitationReceived += (_, _) => invocationCount++;
        client.SessionInvitationReceived += (_, _) => invocationCount++;
        client.SessionInvitationReceived += (_, _) => invocationCount++;

        // Assert - no exception
        Assert.Equal(0, invocationCount);
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task ConcurrentPingCalls_AllReturnFalseWhenNotConnected()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));

        // Act - concurrent ping calls
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => client.PingAsync())
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert - all should return false
        Assert.All(results, result => Assert.False(result));
    }

    [Fact]
    public async Task ConcurrentTryGetSessionInvitation_ThreadSafe()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));

        // Act - concurrent TryGetSessionInvitation calls
        var tasks = Enumerable.Range(0, 100)
            .Select(_ => Task.Run(() =>
            {
                client.TryGetSessionInvitation(out var invitation);
                return invitation;
            }))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert - all should complete without exception and return null
        Assert.All(results, Assert.Null);
    }

    #endregion

    #region JoinSessionAsync Tests

    [Fact]
    public async Task JoinSessionAsync_WithInvalidInvitation_ReturnsNull()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:22067");
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(2));

        // Create an invitation with a non-routable IP
        var invitation = new SessionInvitation(
            new byte[32],
            new byte[32],
            new byte[] { 127, 0, 0, 1 }, // localhost
            59994, // non-existent port
            false
        );

        // Act
        var stream = await client.JoinSessionAsync(invitation);

        // Assert - should return null due to connection failure
        Assert.Null(stream);
    }

    #endregion

    #region Connection Failure Handling Tests

    [Fact]
    public async Task ConnectAsync_LogsConnectionAttempt()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:59993");
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(1));

        // Act
        await client.ConnectAsync();

        // Assert - verify logging occurred
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("Connecting to relay server")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public async Task ConnectAsync_LogsErrorOnFailure()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:59992");
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(1));

        // Act
        await client.ConnectAsync();

        // Assert - verify error logging occurred
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    #endregion

    #region Helper Methods

    private X509Certificate2 CreateTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=TestRelayClient",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddYears(1));
    }

    public void Dispose()
    {
        _testCertificate.Dispose();
    }

    #endregion
}

/// <summary>
/// Additional tests for RelayClient state transitions and lifecycle management.
/// </summary>
public class RelayClientLifecycleTests : IDisposable
{
    private readonly Mock<ILogger<RelayClient>> _mockLogger;
    private readonly X509Certificate2 _testCertificate;

    public RelayClientLifecycleTests()
    {
        _mockLogger = new Mock<ILogger<RelayClient>>();
        _testCertificate = CreateTestCertificate();
    }

    [Fact]
    public void NewClient_HasCorrectInitialState()
    {
        // Arrange & Act
        var relayUri = new Uri("relay://localhost:22067");
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(10));

        // Assert
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task FailedConnection_StateRemainsDisconnected()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:59991"); // Non-existent
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(1));

        // Act
        var result = await client.ConnectAsync();

        // Assert
        Assert.False(result);
        Assert.False(client.IsConnected);
    }

    [Fact]
    public async Task DisconnectAfterFailedConnect_CompletesSuccessfully()
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:59990");
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(1));

        // Act
        await client.ConnectAsync();
        await client.DisconnectAsync();

        // Assert
        Assert.False(client.IsConnected);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public async Task MultipleConnectionAttempts_HandledGracefully(int attempts)
    {
        // Arrange
        var relayUri = new Uri("relay://localhost:59989");
        using var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(1));

        // Act & Assert
        for (int i = 0; i < attempts; i++)
        {
            var result = await client.ConnectAsync();
            Assert.False(result);
            Assert.False(client.IsConnected);
        }
    }

    [Fact]
    public async Task DisposeWhileConnecting_HandledGracefully()
    {
        // Arrange
        var relayUri = new Uri("relay://10.255.255.1:22067"); // Non-routable IP for slow timeout
        var client = new RelayClient(_mockLogger.Object, relayUri, _testCertificate, TimeSpan.FromSeconds(30));

        // Act - start connecting, then dispose
        await client.ConnectAsync();
        await Task.Delay(100); // Give it time to start
        client.Dispose();

        // Assert - should not throw
        Assert.False(client.IsConnected);
    }

    private X509Certificate2 CreateTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=TestRelayClient",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

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
/// Tests for RelayClient with SessionInvitation handling.
/// </summary>
public class RelayClientSessionInvitationTests : IDisposable
{
    private readonly X509Certificate2 _testCertificate;

    public RelayClientSessionInvitationTests()
    {
        _testCertificate = CreateTestCertificate();
    }

    [Fact]
    public void SessionInvitation_WithValidData_FormatsCorrectly()
    {
        // Arrange
        var from = new byte[32];
        var key = new byte[32];
        var address = new byte[] { 192, 168, 1, 100 };
        Random.Shared.NextBytes(from);
        Random.Shared.NextBytes(key);

        // Act
        var invitation = new SessionInvitation(from, key, address, 22067, true);

        // Assert
        Assert.Equal(RelayProtocol.MessageType.SessionInvitation, invitation.Type);
        Assert.Equal(from, invitation.From);
        Assert.Equal(key, invitation.Key);
        Assert.Equal(address, invitation.Address);
        Assert.Equal(22067, invitation.Port);
        Assert.True(invitation.ServerSocket);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SessionInvitation_ServerSocket_PreservesValue(bool serverSocket)
    {
        // Arrange & Act
        var invitation = new SessionInvitation(
            new byte[32],
            new byte[32],
            new byte[] { 10, 0, 0, 1 },
            8080,
            serverSocket
        );

        // Assert
        Assert.Equal(serverSocket, invitation.ServerSocket);
    }

    [Theory]
    [InlineData((ushort)1)]
    [InlineData((ushort)80)]
    [InlineData((ushort)443)]
    [InlineData((ushort)22067)]
    [InlineData((ushort)65535)]
    public void SessionInvitation_Port_SupportsAllValues(ushort port)
    {
        // Act
        var invitation = new SessionInvitation(
            new byte[32],
            new byte[32],
            new byte[] { 127, 0, 0, 1 },
            port,
            false
        );

        // Assert
        Assert.Equal(port, invitation.Port);
    }

    [Fact]
    public void SessionInvitation_ToString_ContainsIPAndPort()
    {
        // Arrange
        var from = new byte[32];
        for (int i = 0; i < 32; i++) from[i] = (byte)i;
        var invitation = new SessionInvitation(
            from,
            new byte[32],
            new byte[] { 192, 168, 1, 100 },
            22067,
            true
        );

        // Act
        var str = invitation.ToString();

        // Assert
        Assert.Contains("192.168.1.100", str);
        Assert.Contains("22067", str);
    }

    [Fact]
    public void SessionInvitation_DefaultConstructor_SetsEmptyDefaults()
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

    private X509Certificate2 CreateTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=TestRelayClient",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddYears(1));
    }

    public void Dispose()
    {
        _testCertificate.Dispose();
    }
}
