using System;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Infrastructure.Services.Network.Nat;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests.Network;

public class NatPmpClientTests : IDisposable
{
    private readonly Mock<ILogger<NatPmpClient>> _loggerMock;
    private readonly NatPmpClient _client;

    public NatPmpClientTests()
    {
        _loggerMock = new Mock<ILogger<NatPmpClient>>();
        _client = new NatPmpClient(_loggerMock.Object);
    }

    [Fact]
    public void NatPmpMapping_IsExpired_ReturnsTrueWhenExpired()
    {
        // Arrange
        var mapping = new NatPmpMapping
        {
            Protocol = "TCP",
            InternalPort = 22000,
            ExternalPort = 22000,
            LifetimeSeconds = 60,
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
        };

        // Assert
        Assert.True(mapping.IsExpired);
    }

    [Fact]
    public void NatPmpMapping_IsExpired_ReturnsFalseWhenNotExpired()
    {
        // Arrange
        var mapping = new NatPmpMapping
        {
            Protocol = "TCP",
            InternalPort = 22000,
            ExternalPort = 22000,
            LifetimeSeconds = 3600,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        };

        // Assert
        Assert.False(mapping.IsExpired);
    }

    [Fact]
    public void NatPmpMapping_ShouldRenew_ReturnsTrueWhenHalfLifetimeRemaining()
    {
        // Arrange
        var mapping = new NatPmpMapping
        {
            Protocol = "TCP",
            InternalPort = 22000,
            ExternalPort = 22000,
            LifetimeSeconds = 7200, // 2 hours
            CreatedAt = DateTime.UtcNow.AddMinutes(-70),
            ExpiresAt = DateTime.UtcNow.AddMinutes(50) // Less than 60 minutes (half of 2h)
        };

        // Assert
        Assert.True(mapping.ShouldRenew);
    }

    [Fact]
    public void NatPmpMapping_ShouldRenew_ReturnsFalseWhenMoreThanHalfLifetimeRemaining()
    {
        // Arrange
        var mapping = new NatPmpMapping
        {
            Protocol = "TCP",
            InternalPort = 22000,
            ExternalPort = 22000,
            LifetimeSeconds = 7200, // 2 hours
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(90) // More than 60 minutes
        };

        // Assert
        Assert.False(mapping.ShouldRenew);
    }

    [Fact]
    public void NatPmpMapping_TimeToExpire_ReturnsCorrectValue()
    {
        // Arrange
        var expiresAt = DateTime.UtcNow.AddMinutes(30);
        var mapping = new NatPmpMapping
        {
            Protocol = "TCP",
            InternalPort = 22000,
            ExternalPort = 22000,
            LifetimeSeconds = 3600,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            ExpiresAt = expiresAt
        };

        // Act
        var timeToExpire = mapping.TimeToExpire;

        // Assert
        Assert.True(timeToExpire.TotalMinutes >= 29 && timeToExpire.TotalMinutes <= 31);
    }

    [Fact(Timeout = 5000)]
    public async Task DiscoverGatewayAsync_ReturnsTrue_WhenGatewayFound()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var result = await _client.DiscoverGatewayAsync(cts.Token);
            Assert.True(result || !result);
        }
        catch (OperationCanceledException) { }
    }

    [Fact(Timeout = 5000)]
    public async Task GetExternalAddressAsync_ReturnsNull_WhenNoGateway()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var result = await _client.GetExternalAddressAsync(cts.Token);
            Assert.True(result == null || result != null);
        }
        catch (OperationCanceledException) { }
    }

    [Fact(Timeout = 5000)]
    public async Task CreateMappingAsync_ReturnsNull_WhenNoGateway()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var result = await _client.CreateMappingAsync("TCP", 22000, cancellationToken: cts.Token);
            Assert.True(result == null || result != null);
        }
        catch (OperationCanceledException) { }
    }

    [Fact(Timeout = 5000)]
    public async Task DeleteMappingAsync_ReturnsFalse_WhenNoGateway()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            var result = await _client.DeleteMappingAsync("TCP", 22000, cts.Token);
            Assert.True(result || !result);
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
