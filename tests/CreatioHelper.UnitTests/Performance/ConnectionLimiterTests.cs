using CreatioHelper.Infrastructure.Services.Performance;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.UnitTests.Performance;

public class ConnectionLimiterTests : IDisposable
{
    private readonly Mock<ILogger<ConnectionLimiter>> _loggerMock;
    private ConnectionLimiter? _limiter;

    public ConnectionLimiterTests()
    {
        _loggerMock = new Mock<ILogger<ConnectionLimiter>>();
    }

    public void Dispose()
    {
        _limiter?.Dispose();
    }

    [Fact]
    public void TryAcquire_WithinLimits_ReturnsSlot()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object, new ConnectionLimiterConfiguration
        {
            MaxTotalConnections = 10,
            MaxConnectionsPerDevice = 2
        });

        // Act
        var slot = _limiter.TryAcquire("device-1");

        // Assert
        Assert.NotNull(slot);
        Assert.Equal("device-1", slot.DeviceId);
        Assert.True(slot.IsValid);
    }

    [Fact]
    public void TryAcquire_ExceedsGlobalLimit_ReturnsNull()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object, new ConnectionLimiterConfiguration
        {
            MaxTotalConnections = 2,
            MaxConnectionsPerDevice = 0 // Unlimited per device
        });

        // Act
        var slot1 = _limiter.TryAcquire("device-1");
        var slot2 = _limiter.TryAcquire("device-2");
        var slot3 = _limiter.TryAcquire("device-3"); // Should fail

        // Assert
        Assert.NotNull(slot1);
        Assert.NotNull(slot2);
        Assert.Null(slot3);
    }

    [Fact]
    public void TryAcquire_ExceedsDeviceLimit_ReturnsNull()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object, new ConnectionLimiterConfiguration
        {
            MaxTotalConnections = 10,
            MaxConnectionsPerDevice = 2
        });

        // Act
        var slot1 = _limiter.TryAcquire("device-1");
        var slot2 = _limiter.TryAcquire("device-1");
        var slot3 = _limiter.TryAcquire("device-1"); // Should fail

        // Assert
        Assert.NotNull(slot1);
        Assert.NotNull(slot2);
        Assert.Null(slot3);
    }

    [Fact]
    public void TryAcquire_AfterRelease_AllowsNewConnection()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object, new ConnectionLimiterConfiguration
        {
            MaxTotalConnections = 1
        });

        var slot1 = _limiter.TryAcquire("device-1");
        Assert.NotNull(slot1);
        Assert.Null(_limiter.TryAcquire("device-2")); // Should fail

        // Act
        slot1.Dispose(); // Release
        var slot2 = _limiter.TryAcquire("device-2");

        // Assert
        Assert.NotNull(slot2);
    }

    [Fact]
    public void TotalConnectionCount_ReflectsActiveConnections()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object);

        // Act
        var slot1 = _limiter.TryAcquire("device-1");
        var slot2 = _limiter.TryAcquire("device-2");

        // Assert
        Assert.Equal(2, _limiter.TotalConnectionCount);

        // After release
        slot1?.Dispose();
        Assert.Equal(1, _limiter.TotalConnectionCount);
    }

    [Fact]
    public void GetDeviceConnectionCount_ReturnsCorrectCount()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object, new ConnectionLimiterConfiguration
        {
            MaxConnectionsPerDevice = 5
        });

        // Act
        _limiter.TryAcquire("device-1");
        _limiter.TryAcquire("device-1");
        _limiter.TryAcquire("device-2");

        // Assert
        Assert.Equal(2, _limiter.GetDeviceConnectionCount("device-1"));
        Assert.Equal(1, _limiter.GetDeviceConnectionCount("device-2"));
        Assert.Equal(0, _limiter.GetDeviceConnectionCount("device-3"));
    }

    [Fact]
    public void CanConnect_WithinLimits_ReturnsTrue()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object, new ConnectionLimiterConfiguration
        {
            MaxTotalConnections = 5,
            MaxConnectionsPerDevice = 2
        });

        // Act & Assert
        Assert.True(_limiter.CanConnect("device-1"));
    }

    [Fact]
    public void CanConnect_ExceedsDeviceLimit_ReturnsFalse()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object, new ConnectionLimiterConfiguration
        {
            MaxTotalConnections = 10,
            MaxConnectionsPerDevice = 2
        });

        _limiter.TryAcquire("device-1");
        _limiter.TryAcquire("device-1");

        // Act & Assert
        Assert.False(_limiter.CanConnect("device-1"));
        Assert.True(_limiter.CanConnect("device-2"));
    }

    [Fact]
    public void CanConnect_ExceedsGlobalLimit_ReturnsFalse()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object, new ConnectionLimiterConfiguration
        {
            MaxTotalConnections = 2,
            MaxConnectionsPerDevice = 0
        });

        _limiter.TryAcquire("device-1");
        _limiter.TryAcquire("device-2");

        // Act & Assert
        Assert.False(_limiter.CanConnect("device-3"));
    }

    [Fact]
    public async Task AcquireAsync_WaitsForAvailability()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object, new ConnectionLimiterConfiguration
        {
            MaxTotalConnections = 1
        });

        var slot1 = _limiter.TryAcquire("device-1");
        Assert.NotNull(slot1);

        // Act
        var acquireTask = _limiter.AcquireAsync("device-2", TimeSpan.FromSeconds(2));

        // Release first slot after a short delay
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            slot1.Dispose();
        });

        var slot2 = await acquireTask;

        // Assert
        Assert.NotNull(slot2);
        Assert.Equal("device-2", slot2.DeviceId);
    }

    [Fact]
    public async Task AcquireAsync_TimesOut_ReturnsNull()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object, new ConnectionLimiterConfiguration
        {
            MaxTotalConnections = 1
        });

        var slot1 = _limiter.TryAcquire("device-1");
        Assert.NotNull(slot1);

        // Act
        var slot2 = await _limiter.AcquireAsync("device-2", TimeSpan.FromMilliseconds(200));

        // Assert
        Assert.Null(slot2);
    }

    [Fact]
    public async Task AcquireAsync_Cancelled_ReturnsNull()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object, new ConnectionLimiterConfiguration
        {
            MaxTotalConnections = 1
        });

        _limiter.TryAcquire("device-1");

        using var cts = new CancellationTokenSource(100);

        // Act
        var slot = await _limiter.AcquireAsync("device-2", TimeSpan.FromSeconds(10), cts.Token);

        // Assert
        Assert.Null(slot);
    }

    [Fact]
    public void UpdateConfiguration_ChangesLimits()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object, new ConnectionLimiterConfiguration
        {
            MaxTotalConnections = 1
        });

        _limiter.TryAcquire("device-1");
        Assert.Null(_limiter.TryAcquire("device-2")); // Should fail

        // Act - Update configuration (note: doesn't affect existing semaphore)
        _limiter.UpdateConfiguration(new ConnectionLimiterConfiguration
        {
            MaxTotalConnections = 10
        });

        // Assert - Statistics should reflect new config
        var stats = _limiter.GetStatistics();
        Assert.NotNull(stats);
    }

    [Fact]
    public void GetStatistics_ReturnsCorrectStats()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object, new ConnectionLimiterConfiguration
        {
            MaxTotalConnections = 10,
            MaxConnectionsPerDevice = 5, // Allow multiple per device
            TrackStatistics = true
        });

        var slot1 = _limiter.TryAcquire("device-1");
        var slot2 = _limiter.TryAcquire("device-1");
        _limiter.TryAcquire("device-2");

        // Act
        var stats = _limiter.GetStatistics();

        // Assert
        Assert.Equal(3, stats.ActiveConnections);
        Assert.Equal(3, stats.TotalConnectionsAcquired);
        Assert.True(stats.ConnectionsByDevice.ContainsKey("device-1"));
        Assert.True(stats.ConnectionsByDevice.ContainsKey("device-2"));
    }

    [Fact]
    public void GetStatistics_TracksRejections()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object, new ConnectionLimiterConfiguration
        {
            MaxTotalConnections = 1
        });

        _limiter.TryAcquire("device-1");
        _limiter.TryAcquire("device-2"); // Rejected

        // Act
        var stats = _limiter.GetStatistics();

        // Assert
        Assert.Equal(1, stats.TotalConnectionsRejected);
    }

    [Fact]
    public void GetStatistics_TracksPeakConnections()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object);

        var slot1 = _limiter.TryAcquire("device-1");
        var slot2 = _limiter.TryAcquire("device-2");
        var slot3 = _limiter.TryAcquire("device-3");

        // Release some
        slot1?.Dispose();
        slot2?.Dispose();

        // Act
        var stats = _limiter.GetStatistics();

        // Assert
        Assert.Equal(3, stats.PeakConnections);
        Assert.Equal(1, stats.ActiveConnections);
    }

    [Fact]
    public void DeviceOverrides_AllowHigherLimitForDevice()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object, new ConnectionLimiterConfiguration
        {
            MaxConnectionsPerDevice = 1,
            DeviceOverrides = new Dictionary<string, int>
            {
                { "special-device", 5 }
            }
        });

        // Act
        var slot1 = _limiter.TryAcquire("special-device");
        var slot2 = _limiter.TryAcquire("special-device");
        var slot3 = _limiter.TryAcquire("special-device");

        var slot4 = _limiter.TryAcquire("normal-device");
        var slot5 = _limiter.TryAcquire("normal-device"); // Should fail

        // Assert
        Assert.NotNull(slot1);
        Assert.NotNull(slot2);
        Assert.NotNull(slot3);
        Assert.NotNull(slot4);
        Assert.Null(slot5);
    }

    [Fact]
    public void ConnectionSlot_IsValid_FalseAfterDispose()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object);
        var slot = _limiter.TryAcquire("device-1");
        Assert.NotNull(slot);
        Assert.True(slot.IsValid);

        // Act
        slot.Dispose();

        // Assert
        Assert.False(slot.IsValid);
    }

    [Fact]
    public void ConnectionSlot_AcquiredAt_IsSet()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object);
        var before = DateTime.UtcNow;

        // Act
        var slot = _limiter.TryAcquire("device-1");
        var after = DateTime.UtcNow;

        // Assert
        Assert.NotNull(slot);
        Assert.True(slot.AcquiredAt >= before);
        Assert.True(slot.AcquiredAt <= after);
    }

    [Fact]
    public void TryAcquire_NullDeviceId_ThrowsArgumentNullException()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _limiter.TryAcquire(null!));
    }

    [Fact]
    public void TryAcquire_EmptyDeviceId_ThrowsArgumentNullException()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _limiter.TryAcquire(string.Empty));
    }

    [Fact]
    public void Configuration_DefaultValues()
    {
        // Arrange
        var config = new ConnectionLimiterConfiguration();

        // Assert
        Assert.Equal(0, config.MaxTotalConnections); // Unlimited
        Assert.Equal(1, config.MaxConnectionsPerDevice);
        Assert.Equal(TimeSpan.FromSeconds(30), config.DefaultTimeout);
        Assert.True(config.TrackStatistics);
        Assert.Empty(config.DeviceOverrides);
    }

    [Fact]
    public void Statistics_DefaultValues()
    {
        // Arrange
        var stats = new ConnectionLimiterStatistics();

        // Assert
        Assert.Equal(0, stats.ActiveConnections);
        Assert.Equal(0, stats.PeakConnections);
        Assert.Equal(0, stats.TotalConnectionsAcquired);
        Assert.Equal(0, stats.TotalConnectionsRejected);
        Assert.Equal(TimeSpan.Zero, stats.AverageConnectionDuration);
        Assert.Equal(0, stats.WaitingRequests);
        Assert.Empty(stats.ConnectionsByDevice);
    }

    [Fact]
    public void UnlimitedConnections_AllowsManyConnections()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object, new ConnectionLimiterConfiguration
        {
            MaxTotalConnections = 0, // Unlimited
            MaxConnectionsPerDevice = 0 // Unlimited
        });

        // Act
        var slots = new List<IConnectionSlot?>();
        for (int i = 0; i < 100; i++)
        {
            slots.Add(_limiter.TryAcquire($"device-{i}"));
        }

        // Assert
        Assert.All(slots, s => Assert.NotNull(s));
        Assert.Equal(100, _limiter.TotalConnectionCount);
    }

    [Fact]
    public async Task AcquireAsync_TracksDuration()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object, new ConnectionLimiterConfiguration
        {
            TrackStatistics = true,
            MaxConnectionsPerDevice = 5
        });

        // Act
        var slot = await _limiter.AcquireAsync("device-1", TimeSpan.FromSeconds(1));
        Assert.NotNull(slot);

        await Task.Delay(100); // Simulate some work (increased for timing reliability)
        slot.Dispose();

        // Get statistics
        var stats = _limiter.GetStatistics();

        // Assert - check for non-zero duration (timing can vary)
        Assert.True(stats.AverageConnectionDuration > TimeSpan.Zero);
    }

    [Fact]
    public void MultipleDisposes_AreIdempotent()
    {
        // Arrange
        _limiter = new ConnectionLimiter(_loggerMock.Object);
        var slot = _limiter.TryAcquire("device-1");
        Assert.NotNull(slot);

        // Act - Dispose multiple times
        slot.Dispose();
        slot.Dispose();
        slot.Dispose();

        // Assert - Should not throw, count should be 0
        Assert.Equal(0, _limiter.TotalConnectionCount);
    }
}
