using CreatioHelper.Infrastructure.Services.Network.Discovery;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests.Network;

public class DiscoveryCacheTests : IDisposable
{
    private readonly Mock<ILogger<DiscoveryCache>> _loggerMock;
    private readonly DiscoveryCache _cache;

    public DiscoveryCacheTests()
    {
        _loggerMock = new Mock<ILogger<DiscoveryCache>>();
        _cache = new DiscoveryCache(_loggerMock.Object);
    }

    [Fact]
    public void AddPositive_AddsEntryToCache()
    {
        // Arrange
        var deviceId = "device1";
        var addresses = new List<string> { "tcp://192.168.1.1:22000" };

        // Act
        _cache.AddPositive(deviceId, addresses, DiscoveryCacheSource.Local);

        // Assert
        Assert.True(_cache.TryGet(deviceId, out var entry));
        Assert.NotNull(entry);
        Assert.Equal(deviceId, entry.DeviceId);
        Assert.Single(entry.Addresses);
        Assert.False(entry.IsNegative);
    }

    [Fact]
    public void AddPositive_MergesAddressesFromMultipleSources()
    {
        // Arrange
        var deviceId = "device1";
        var addresses1 = new List<string> { "tcp://192.168.1.1:22000" };
        var addresses2 = new List<string> { "tcp://10.0.0.1:22000" };

        // Act
        _cache.AddPositive(deviceId, addresses1, DiscoveryCacheSource.Local);
        _cache.AddPositive(deviceId, addresses2, DiscoveryCacheSource.Global);

        // Assert
        Assert.True(_cache.TryGet(deviceId, out var entry));
        Assert.NotNull(entry);
        Assert.Equal(2, entry.Addresses.Count);
    }

    [Fact]
    public void AddNegative_AddsNegativeEntry()
    {
        // Arrange
        var deviceId = "device1";

        // Act
        _cache.AddNegative(deviceId, DiscoveryCacheSource.Global);

        // Assert
        Assert.True(_cache.TryGet(deviceId, out var entry));
        Assert.NotNull(entry);
        Assert.True(entry.IsNegative);
        Assert.Empty(entry.Addresses);
    }

    [Fact]
    public void AddNegative_DoesNotOverridePositive()
    {
        // Arrange
        var deviceId = "device1";
        var addresses = new List<string> { "tcp://192.168.1.1:22000" };

        // Act
        _cache.AddPositive(deviceId, addresses, DiscoveryCacheSource.Local);
        _cache.AddNegative(deviceId, DiscoveryCacheSource.Global);

        // Assert
        Assert.True(_cache.TryGet(deviceId, out var entry));
        Assert.NotNull(entry);
        Assert.False(entry.IsNegative);
        Assert.Single(entry.Addresses);
    }

    [Fact]
    public void TryGet_ReturnsFalse_WhenNotFound()
    {
        // Act
        var result = _cache.TryGet("nonexistent", out var entry);

        // Assert
        Assert.False(result);
        Assert.Null(entry);
    }

    [Fact]
    public void Remove_RemovesEntry()
    {
        // Arrange
        var deviceId = "device1";
        _cache.AddPositive(deviceId, new List<string> { "tcp://192.168.1.1:22000" }, DiscoveryCacheSource.Local);

        // Act
        _cache.Remove(deviceId);

        // Assert
        Assert.False(_cache.TryGet(deviceId, out _));
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        // Arrange
        _cache.AddPositive("device1", new List<string> { "tcp://192.168.1.1:22000" }, DiscoveryCacheSource.Local);
        _cache.AddPositive("device2", new List<string> { "tcp://192.168.1.2:22000" }, DiscoveryCacheSource.Global);

        // Act
        _cache.Clear();

        // Assert
        Assert.False(_cache.TryGet("device1", out _));
        Assert.False(_cache.TryGet("device2", out _));
    }

    [Fact]
    public void GetAll_ReturnsValidEntries()
    {
        // Arrange
        _cache.AddPositive("device1", new List<string> { "tcp://192.168.1.1:22000" }, DiscoveryCacheSource.Local);
        _cache.AddPositive("device2", new List<string> { "tcp://192.168.1.2:22000" }, DiscoveryCacheSource.Global);

        // Act
        var entries = _cache.GetAll().ToList();

        // Assert
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void GetStatistics_ReturnsCorrectCounts()
    {
        // Arrange
        _cache.AddPositive("device1", new List<string> { "tcp://192.168.1.1:22000", "tcp://192.168.1.2:22000" }, DiscoveryCacheSource.Local);
        _cache.AddPositive("device2", new List<string> { "tcp://10.0.0.1:22000" }, DiscoveryCacheSource.Global);
        _cache.AddNegative("device3", DiscoveryCacheSource.Global);

        // Act
        var stats = _cache.GetStatistics();

        // Assert
        Assert.Equal(2, stats.PositiveEntryCount);
        Assert.Equal(1, stats.NegativeEntryCount);
        Assert.Equal(3, stats.TotalAddressCount);
        Assert.Equal(1, stats.LocalEntryCount);
        Assert.Equal(1, stats.GlobalEntryCount);
    }

    [Fact]
    public void Entry_IsValid_ReturnsTrueWhenNotExpired()
    {
        // Arrange
        _cache.AddPositive("device1", new List<string> { "tcp://192.168.1.1:22000" }, DiscoveryCacheSource.Local);

        // Act
        _cache.TryGet("device1", out var entry);

        // Assert
        Assert.NotNull(entry);
        Assert.True(entry.IsValid);
        Assert.True(entry.TimeToLive > TimeSpan.Zero);
    }

    [Fact]
    public void PositiveTtl_CanBeCustomized()
    {
        // Arrange
        _cache.PositiveTtl = TimeSpan.FromSeconds(1);
        _cache.AddPositive("device1", new List<string> { "tcp://192.168.1.1:22000" }, DiscoveryCacheSource.Global);

        // Act & Assert - entry should be valid immediately
        Assert.True(_cache.TryGet("device1", out var entry));
        Assert.NotNull(entry);
        Assert.True(entry.TimeToLive.TotalSeconds <= 1);
    }

    [Fact]
    public void LocalDiscoveryTtl_UsedForLocalSource()
    {
        // Arrange
        _cache.LocalDiscoveryTtl = TimeSpan.FromSeconds(90);
        _cache.AddPositive("device1", new List<string> { "tcp://192.168.1.1:22000" }, DiscoveryCacheSource.Local);

        // Act
        _cache.TryGet("device1", out var entry);

        // Assert
        Assert.NotNull(entry);
        Assert.True(entry.TimeToLive.TotalSeconds <= 90);
        Assert.True(entry.TimeToLive.TotalSeconds > 0);
    }

    public void Dispose()
    {
        _cache.Dispose();
    }
}
