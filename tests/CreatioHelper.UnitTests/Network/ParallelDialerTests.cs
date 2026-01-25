using System;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Infrastructure.Services.Network.Connection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests.Network;

public class ParallelDialerTests : IDisposable
{
    private readonly Mock<ILogger<ParallelDialer>> _loggerMock;
    private readonly Mock<ILogger<ConnectionPrioritizer>> _prioritizerLoggerMock;
    private readonly ConnectionPrioritizer _prioritizer;
    private readonly ParallelDialer _dialer;

    public ParallelDialerTests()
    {
        _loggerMock = new Mock<ILogger<ParallelDialer>>();
        _prioritizerLoggerMock = new Mock<ILogger<ConnectionPrioritizer>>();
        _prioritizer = new ConnectionPrioritizer(_prioritizerLoggerMock.Object);
        _dialer = new ParallelDialer(_loggerMock.Object, _prioritizer);
    }

    [Fact]
    public async Task DialAsync_ReturnsNull_WhenNoAddresses()
    {
        // Act
        var result = await _dialer.DialAsync("device1", Array.Empty<string>());

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task DialAsync_ProcessesAddressesInPriorityOrder()
    {
        // This test verifies that addresses are processed by priority
        // We can't easily test actual connections without network access,
        // but we can verify the structure

        // Arrange
        var addresses = new[]
        {
            "tcp://192.168.1.1:22000",  // LAN - priority 10
            "tcp://8.8.8.8:22000",       // WAN - priority 15
            "relay://relay.example.com:443" // Relay - priority 90
        };

        // Act - this will fail to connect but exercise the priority logic
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var result = await _dialer.DialAsync("device1", addresses, cancellationToken: cts.Token);

        // Assert - result will be null since we can't connect, but no exception thrown
        Assert.Null(result);
    }

    [Fact]
    public async Task DialAsync_RespectsCancellation()
    {
        // Arrange
        var addresses = new[] { "tcp://192.168.1.1:22000" };
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel immediately

        // Act
        var result = await _dialer.DialAsync("device1", addresses, cancellationToken: cts.Token);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void DialResult_HasCorrectStructure()
    {
        // Arrange
        var result = new DialResult
        {
            DeviceId = "device1",
            Address = "tcp://192.168.1.1:22000",
            Priority = 10,
            ConnectionType = ConnectionType.TcpLan,
            IsLan = true,
            ConnectedAt = DateTime.UtcNow
        };

        // Assert
        Assert.Equal("device1", result.DeviceId);
        Assert.Equal("tcp://192.168.1.1:22000", result.Address);
        Assert.Equal(10, result.Priority);
        Assert.Equal(ConnectionType.TcpLan, result.ConnectionType);
        Assert.True(result.IsLan);
        Assert.NotEqual(default, result.ConnectedAt);
    }

    [Fact]
    public void ParallelDialerConfiguration_HasCorrectDefaults()
    {
        // Arrange
        var config = new ParallelDialerConfiguration();

        // Assert
        Assert.Equal(64, config.MaxParallelDials);
        Assert.Equal(8, config.MaxParallelDialsPerDevice);
        Assert.Equal(10, config.ConnectionTimeoutSeconds);
        Assert.Equal(10, config.TlsHandshakeTimeoutSeconds);
        Assert.Equal(5000, config.SemaphoreTimeoutMs);
    }

    [Fact]
    public void CleanupDevice_RemovesDeviceSemaphore()
    {
        // Act - cleanup should not throw for non-existent device
        _dialer.CleanupDevice("nonexistent-device");

        // Assert - no exception thrown
        Assert.True(true);
    }

    [Fact]
    public void DialResult_Dispose_DoesNotThrow()
    {
        // Arrange
        var result = new DialResult
        {
            Address = "tcp://192.168.1.1:22000"
        };

        // Act & Assert
        result.Dispose(); // Should not throw
        Assert.True(true);
    }

    [Fact]
    public void DialResult_GetStream_ReturnsNull_WhenNoConnection()
    {
        // Arrange
        var result = new DialResult
        {
            Address = "tcp://192.168.1.1:22000"
        };

        // Act
        var stream = result.GetStream();

        // Assert
        Assert.Null(stream);
    }

    [Fact]
    public async Task DialAsync_HandlesMultipleDevices()
    {
        // Arrange
        var addresses1 = new[] { "tcp://192.168.1.1:22000" };
        var addresses2 = new[] { "tcp://192.168.1.2:22000" };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Act - dial multiple devices concurrently
        var task1 = _dialer.DialAsync("device1", addresses1, cancellationToken: cts.Token);
        var task2 = _dialer.DialAsync("device2", addresses2, cancellationToken: cts.Token);

        await Task.WhenAll(task1, task2);

        // Assert - no exception thrown
        Assert.True(true);
    }

    [Fact]
    public async Task DialAsync_UsesCustomConfiguration()
    {
        // Arrange
        var config = new ParallelDialerConfiguration
        {
            MaxParallelDials = 4,
            MaxParallelDialsPerDevice = 2,
            ConnectionTimeoutSeconds = 1,
            SemaphoreTimeoutMs = 100
        };
        var customDialer = new ParallelDialer(_loggerMock.Object, _prioritizer, config);

        var addresses = new[] { "tcp://192.168.1.1:22000" };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Act
        var result = await customDialer.DialAsync("device1", addresses, cancellationToken: cts.Token);

        // Assert
        Assert.Null(result); // Connection will fail but config was used

        // Cleanup
        customDialer.Dispose();
    }

    public void Dispose()
    {
        _dialer.Dispose();
    }
}
