using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Infrastructure.Services.Network.Stun;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests.Network;

public class StunKeepaliveServiceTests : IDisposable
{
    private readonly Mock<ILogger<StunKeepaliveService>> _loggerMock;
    private readonly Mock<ILogger<StunClient>> _stunClientLoggerMock;
    private readonly StunClient _stunClient;
    private readonly StunKeepaliveService _service;

    public StunKeepaliveServiceTests()
    {
        _loggerMock = new Mock<ILogger<StunKeepaliveService>>();
        _stunClientLoggerMock = new Mock<ILogger<StunClient>>();
        _stunClient = new StunClient(_stunClientLoggerMock.Object);

        var config = new StunKeepaliveConfiguration
        {
            Enabled = true,
            KeepaliveIntervalSeconds = 60,
            RequestTimeoutSeconds = 5,
            Servers = new List<string> { "stun.l.google.com:19302" }
        };

        _service = new StunKeepaliveService(_loggerMock.Object, _stunClient, config);
    }

    [Fact]
    public void IsRunning_ReturnsFalse_WhenNotStarted()
    {
        // Assert
        Assert.False(_service.IsRunning);
    }

    [Fact]
    public async Task StartAsync_SetsIsRunning()
    {
        // Act
        await _service.StartAsync(CancellationToken.None);

        // Assert
        Assert.True(_service.IsRunning);

        // Cleanup
        await _service.StopAsync();
    }

    [Fact]
    public async Task StopAsync_ClearsIsRunning()
    {
        // Arrange
        await _service.StartAsync(CancellationToken.None);

        // Act
        await _service.StopAsync();

        // Assert
        Assert.False(_service.IsRunning);
    }

    [Fact]
    public void GetStatus_ReturnsCorrectStatus()
    {
        // Act
        var status = _service.GetStatus();

        // Assert
        Assert.False(status.IsRunning);
        Assert.True(status.Enabled);
        Assert.Null(status.ExternalEndpoint);
        Assert.Single(status.Servers);
    }

    [Fact]
    public async Task CheckNowAsync_ReturnsResult()
    {
        // Act
        var result = await _service.CheckNowAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.NotEqual(default, result.CheckTime);
        // Success depends on network, so we just verify result structure
    }

    [Fact]
    public async Task StartAsync_DoesNotStart_WhenDisabled()
    {
        // Arrange
        var disabledConfig = new StunKeepaliveConfiguration { Enabled = false };
        var service = new StunKeepaliveService(_loggerMock.Object, _stunClient, disabledConfig);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task StartAsync_DoesNotStart_WhenNoServers()
    {
        // Arrange
        var noServersConfig = new StunKeepaliveConfiguration
        {
            Enabled = true,
            Servers = new List<string>()
        };
        var service = new StunKeepaliveService(_loggerMock.Object, _stunClient, noServersConfig);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        Assert.False(service.IsRunning);
    }

    [Fact]
    public void ExternalEndpoint_IsNull_BeforeAnyCheck()
    {
        // Assert
        Assert.Null(_service.ExternalEndpoint);
    }

    [Fact]
    public void DetectedNatType_IsNull_BeforeAnyCheck()
    {
        // Assert
        Assert.Null(_service.DetectedNatType);
    }

    [Fact]
    public void StunKeepaliveResult_HasCorrectStructure()
    {
        // Arrange
        var result = new StunKeepaliveResult
        {
            Success = true,
            Server = "stun.l.google.com:19302",
            CheckTime = DateTime.UtcNow,
            Duration = TimeSpan.FromMilliseconds(50)
        };

        // Assert
        Assert.True(result.Success);
        Assert.Equal("stun.l.google.com:19302", result.Server);
        Assert.False(result.AddressChanged);
        Assert.Null(result.Error);
    }

    [Fact]
    public void StunKeepaliveStatus_HasCorrectDefaults()
    {
        // Arrange
        var status = new StunKeepaliveStatus();

        // Assert
        Assert.False(status.IsRunning);
        Assert.False(status.Enabled);
        Assert.Null(status.ExternalEndpoint);
        Assert.Null(status.DetectedNatType);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Empty(status.Servers);
    }

    [Fact]
    public void StunKeepaliveConfiguration_HasCorrectDefaults()
    {
        // Arrange
        var config = new StunKeepaliveConfiguration();

        // Assert
        Assert.True(config.Enabled);
        Assert.Equal(30, config.KeepaliveIntervalSeconds);
        Assert.Equal(5, config.RequestTimeoutSeconds);
        Assert.Equal(3, config.Servers.Count);
        Assert.Contains("stun.syncthing.net:3478", config.Servers);
    }

    public void Dispose()
    {
        _service.Dispose();
        _stunClient.Dispose();
    }
}
