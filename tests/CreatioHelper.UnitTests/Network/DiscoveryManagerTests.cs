using CreatioHelper.Infrastructure.Services.Network.Discovery;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests.Network;

public class DiscoveryManagerTests : IDisposable
{
    private readonly Mock<ILogger<DiscoveryManager>> _loggerMock;
    private readonly Mock<ILogger<DiscoveryCache>> _cacheLoggerMock;
    private readonly DiscoveryCache _cache;
    private readonly DiscoveryManager _manager;

    public DiscoveryManagerTests()
    {
        _loggerMock = new Mock<ILogger<DiscoveryManager>>();
        _cacheLoggerMock = new Mock<ILogger<DiscoveryCache>>();
        _cache = new DiscoveryCache(_cacheLoggerMock.Object);
        _manager = new DiscoveryManager(_loggerMock.Object, _cache);
    }

    [Fact]
    public void LocalDiscoveryEnabled_ReturnsFalse_WhenNoLocalDiscovery()
    {
        // Assert
        Assert.False(_manager.LocalDiscoveryEnabled);
    }

    [Fact]
    public void GlobalDiscoveryEnabled_ReturnsFalse_WhenNoGlobalDiscovery()
    {
        // Assert
        Assert.False(_manager.GlobalDiscoveryEnabled);
    }

    [Fact]
    public async Task LookupAsync_ReturnsEmptyResult_WhenDeviceNotFound()
    {
        // Act
        var result = await _manager.LookupAsync("unknown-device");

        // Assert
        Assert.NotNull(result);
        Assert.False(result.Found);
        Assert.Empty(result.Addresses);
    }

    [Fact]
    public void AddStaticAddresses_AddsAddressesToCache()
    {
        // Arrange
        var deviceId = "device1";
        var addresses = new[] { "tcp://192.168.1.1:22000" };

        // Act
        _manager.AddStaticAddresses(deviceId, addresses);

        // Assert
        var stats = _manager.GetCacheStatistics();
        Assert.Equal(1, stats.StaticEntryCount);
    }

    [Fact]
    public async Task LookupAsync_ReturnsStaticAddresses()
    {
        // Arrange
        var deviceId = "device1";
        var addresses = new[] { "tcp://192.168.1.1:22000" };
        _manager.AddStaticAddresses(deviceId, addresses);

        // Act
        var result = await _manager.LookupAsync(deviceId);

        // Assert
        Assert.True(result.Found);
        Assert.Single(result.Addresses);
        Assert.Contains(DiscoveryCacheSource.Static, result.Sources);
    }

    [Fact]
    public void RemoveStaticAddresses_RemovesFromCache()
    {
        // Arrange
        var deviceId = "device1";
        _manager.AddStaticAddresses(deviceId, new[] { "tcp://192.168.1.1:22000" });

        // Act
        _manager.RemoveStaticAddresses(deviceId);

        // Assert - should still be in cache but not in static addresses
        // The cache entry remains until it expires
        var status = _manager.GetStatus();
        Assert.Equal(0, status.StaticAddresses.DeviceCount);
    }

    [Fact]
    public void GetStatus_ReturnsCorrectStatus()
    {
        // Act
        var status = _manager.GetStatus();

        // Assert
        Assert.NotNull(status);
        Assert.False(status.IsRunning);
        Assert.False(status.LocalDiscovery.Enabled);
        Assert.False(status.GlobalDiscovery.Enabled);
    }

    [Fact]
    public void GetCacheStatistics_ReturnsCorrectStats()
    {
        // Arrange
        _manager.AddStaticAddresses("device1", new[] { "tcp://192.168.1.1:22000" });
        _manager.AddStaticAddresses("device2", new[] { "tcp://192.168.1.2:22000", "tcp://10.0.0.1:22000" });

        // Act
        var stats = _manager.GetCacheStatistics();

        // Assert
        Assert.Equal(2, stats.PositiveEntryCount);
        Assert.Equal(2, stats.StaticEntryCount);
        Assert.Equal(3, stats.TotalAddressCount);
    }

    [Fact]
    public async Task LookupAsync_SortsAddressesByPriority()
    {
        // Arrange
        var deviceId = "device1";
        var addresses = new[]
        {
            "relay://relay.syncthing.net:443",
            "tcp://8.8.8.8:22000",
            "tcp://192.168.1.1:22000"
        };
        _manager.AddStaticAddresses(deviceId, addresses);

        // Act
        var result = await _manager.LookupAsync(deviceId);

        // Assert
        Assert.True(result.Found);
        Assert.Equal(3, result.Addresses.Count);
        // LAN address should be first (lower priority value)
        Assert.Contains("192.168.1.1", result.Addresses[0].Address);
    }

    [Fact]
    public async Task LookupAsync_CachesResult()
    {
        // Arrange
        var deviceId = "device1";
        _manager.AddStaticAddresses(deviceId, new[] { "tcp://192.168.1.1:22000" });

        // First lookup
        await _manager.LookupAsync(deviceId);

        // Second lookup should hit cache
        var result = await _manager.LookupAsync(deviceId);

        // Assert
        Assert.True(result.FromCache);
    }

    [Fact]
    public async Task LookupAsync_RecordsDuration()
    {
        // Arrange
        var deviceId = "device1";
        _manager.AddStaticAddresses(deviceId, new[] { "tcp://192.168.1.1:22000" });

        // Act
        var result = await _manager.LookupAsync(deviceId);

        // Assert
        Assert.True(result.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public void DiscoveredAddress_HasCorrectProperties()
    {
        // Arrange
        var addr = new DiscoveredAddress
        {
            Address = "tcp://192.168.1.1:22000",
            Source = DiscoveryCacheSource.Local,
            Priority = 10,
            IsLan = true
        };

        // Assert
        Assert.Equal("tcp://192.168.1.1:22000", addr.Address);
        Assert.Equal(DiscoveryCacheSource.Local, addr.Source);
        Assert.Equal(10, addr.Priority);
        Assert.True(addr.IsLan);
        Assert.NotEqual(default, addr.DiscoveredAt);
    }

    [Fact]
    public void DiscoveryResult_GetAddressStrings_ReturnsAddresses()
    {
        // Arrange
        var result = new DiscoveryResult
        {
            DeviceId = "device1",
            Addresses = new List<DiscoveredAddress>
            {
                new() { Address = "tcp://192.168.1.1:22000" },
                new() { Address = "tcp://10.0.0.1:22000" }
            }
        };

        // Act
        var addressStrings = result.GetAddressStrings().ToList();

        // Assert
        Assert.Equal(2, addressStrings.Count);
        Assert.Contains("tcp://192.168.1.1:22000", addressStrings);
        Assert.Contains("tcp://10.0.0.1:22000", addressStrings);
    }

    [Theory]
    [InlineData("tcp://192.168.1.1:22000", true)]   // 192.168.x.x
    [InlineData("tcp://10.0.0.1:22000", true)]      // 10.x.x.x
    [InlineData("tcp://172.16.1.1:22000", true)]    // 172.16-31.x.x
    [InlineData("tcp://127.0.0.1:22000", true)]     // Loopback
    [InlineData("tcp://8.8.8.8:22000", false)]      // Public IP
    [InlineData("tcp://203.0.113.1:22000", false)]  // Public IP
    public void IsLanIpAddress_ReturnsCorrectValue(string address, bool expectedIsLan)
    {
        // This tests the static IsLanIpAddress method
        var uri = new Uri(address);
        var ip = System.Net.IPAddress.Parse(uri.Host);

        // Act
        var isLan = DiscoveryManager.IsLanIpAddress(ip);

        // Assert
        Assert.Equal(expectedIsLan, isLan);
    }

    public void Dispose()
    {
        _manager.Dispose();
        _cache.Dispose();
    }
}
