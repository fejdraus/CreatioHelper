using CreatioHelper.Infrastructure.Services.Network.Stun;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using Xunit;

namespace CreatioHelper.UnitTests.Network;

public class StunClientTests : IDisposable
{
    private readonly Mock<ILogger<StunClient>> _loggerMock;
    private readonly StunClient _client;

    public StunClientTests()
    {
        _loggerMock = new Mock<ILogger<StunClient>>();
        _client = new StunClient(_loggerMock.Object);
    }

    [Fact]
    public void StunResult_DefaultValues_AreNull()
    {
        // Arrange & Act
        var result = new StunResult();

        // Assert
        Assert.Null(result.MappedEndPoint);
        Assert.Null(result.LocalEndPoint);
        Assert.Null(result.ServerEndPoint);
        Assert.Null(result.ServerSoftware);
        Assert.Null(result.OtherAddress);
        Assert.Null(result.ResponseOrigin);
        Assert.False(result.ChangedIp);
        Assert.False(result.ChangedPort);
    }

    [Fact]
    public void StunResult_CanBeInitialized()
    {
        // Arrange & Act
        var result = new StunResult
        {
            MappedEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.1"), 12345),
            LocalEndPoint = new IPEndPoint(IPAddress.Parse("192.168.1.100"), 54321),
            ServerEndPoint = new IPEndPoint(IPAddress.Parse("198.51.100.1"), 3478),
            ServerSoftware = "Test STUN Server",
            OtherAddress = new IPEndPoint(IPAddress.Parse("198.51.100.2"), 3479),
            ResponseOrigin = new IPEndPoint(IPAddress.Parse("198.51.100.1"), 3478),
            ChangedIp = true,
            ChangedPort = false
        };

        // Assert
        Assert.NotNull(result.MappedEndPoint);
        Assert.Equal("203.0.113.1", result.MappedEndPoint.Address.ToString());
        Assert.Equal(12345, result.MappedEndPoint.Port);
        Assert.True(result.ChangedIp);
        Assert.False(result.ChangedPort);
    }

    [Fact]
    public void NatType_HasExpectedValues()
    {
        // Assert
        Assert.Equal(0, (int)NatType.Unknown);
        Assert.Equal(1, (int)NatType.OpenInternet);
        Assert.Equal(2, (int)NatType.FullCone);
        Assert.Equal(3, (int)NatType.RestrictedCone);
        Assert.Equal(4, (int)NatType.PortRestrictedCone);
        Assert.Equal(5, (int)NatType.SymmetricNat);
    }

    [Fact]
    public void NatTypeResult_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var result = new NatTypeResult();

        // Assert
        Assert.Equal(NatType.Unknown, result.Type);
        Assert.Null(result.ExternalAddress);
        Assert.Equal(string.Empty, result.Description);
    }

    [Fact]
    public void NatTypeResult_CanBeInitialized()
    {
        // Arrange & Act
        var result = new NatTypeResult
        {
            Type = NatType.FullCone,
            ExternalAddress = IPAddress.Parse("203.0.113.1"),
            Description = "Full Cone NAT detected"
        };

        // Assert
        Assert.Equal(NatType.FullCone, result.Type);
        Assert.NotNull(result.ExternalAddress);
        Assert.Equal("203.0.113.1", result.ExternalAddress.ToString());
        Assert.Equal("Full Cone NAT detected", result.Description);
    }

    [Fact]
    public async Task BindingRequestAsync_ReturnsNull_WithInvalidServer()
    {
        // Act
        var result = await _client.BindingRequestAsync("invalid-server-that-does-not-exist.local", 3478, TimeSpan.FromSeconds(1));

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task BindingRequestWithChangeAsync_ReturnsNull_WithInvalidServer()
    {
        // Act
        var result = await _client.BindingRequestWithChangeAsync(
            "invalid-server-that-does-not-exist.local",
            3478,
            changeIp: true,
            changePort: true,
            TimeSpan.FromSeconds(1));

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task DetectNatTypeAsync_ReturnsUnknown_WhenNoServersRespond()
    {
        // Arrange
        var servers = new[]
        {
            "invalid-server1.local:3478",
            "invalid-server2.local:3478"
        };

        // Act
        var result = await _client.DetectNatTypeAsync(servers, TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal(NatType.Unknown, result.Type);
    }

    [Fact]
    public async Task DetectNatTypeBasicAsync_ReturnsUnknown_WhenNoServersRespond()
    {
        // Arrange
        var servers = new[]
        {
            "invalid-server1.local:3478",
            "invalid-server2.local:3478"
        };

        // Act
        var result = await _client.DetectNatTypeBasicAsync(servers, TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal(NatType.Unknown, result.Type);
    }

    [Fact]
    public async Task StunClient_ThrowsObjectDisposedException_AfterDispose()
    {
        // Arrange
        _client.Dispose();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            _client.BindingRequestAsync("stun.l.google.com", 19302));
    }

    [Fact]
    public async Task DetectNatTypeAsync_HandlesEmptyServerList()
    {
        // Arrange
        var servers = Array.Empty<string>();

        // Act
        var result = await _client.DetectNatTypeAsync(servers, TimeSpan.FromSeconds(1));

        // Assert
        Assert.Equal(NatType.Unknown, result.Type);
    }

    [Fact]
    public async Task BindingRequestAsync_HandlesCancellation()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var result = await _client.BindingRequestAsync("stun.l.google.com", 19302, TimeSpan.FromSeconds(5), cts.Token);

        // Assert
        Assert.Null(result);
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}

public class NatTypeResultTests
{
    [Theory]
    [InlineData(NatType.OpenInternet, "No NAT detected")]
    [InlineData(NatType.FullCone, "Full cone NAT - best for P2P")]
    [InlineData(NatType.RestrictedCone, "Restricted cone NAT")]
    [InlineData(NatType.PortRestrictedCone, "Port restricted cone NAT")]
    [InlineData(NatType.SymmetricNat, "Symmetric NAT - hardest for P2P")]
    [InlineData(NatType.Unknown, "NAT type unknown")]
    public void NatTypeResult_CanHaveVariousDescriptions(NatType type, string description)
    {
        // Arrange & Act
        var result = new NatTypeResult
        {
            Type = type,
            Description = description
        };

        // Assert
        Assert.Equal(type, result.Type);
        Assert.Equal(description, result.Description);
    }
}
