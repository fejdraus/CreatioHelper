using System.Collections.Generic;
using CreatioHelper.Infrastructure.Services.Network.Connection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests.Network;

public class ConnectionPrioritizerTests
{
    private readonly Mock<ILogger<ConnectionPrioritizer>> _loggerMock;
    private readonly ConnectionPrioritizer _prioritizer;

    public ConnectionPrioritizerTests()
    {
        _loggerMock = new Mock<ILogger<ConnectionPrioritizer>>();
        _prioritizer = new ConnectionPrioritizer(_loggerMock.Object);
    }

    [Theory]
    [InlineData("quic://192.168.1.1:22000", 0)]  // QUIC LAN - highest priority
    [InlineData("quic://8.8.8.8:22000", 5)]       // QUIC WAN
    [InlineData("tcp://192.168.1.1:22000", 10)]   // TCP LAN
    [InlineData("tcp://8.8.8.8:22000", 15)]       // TCP WAN
    [InlineData("relay://relay.syncthing.net:443", 90)] // Relay - lowest priority
    public void CalculatePriority_ReturnsCorrectValue(string address, int expectedPriority)
    {
        // Act
        var priority = _prioritizer.CalculatePriority(address);

        // Assert
        Assert.Equal(expectedPriority, priority);
    }

    [Fact]
    public void GetConnectionType_IdentifiesQuicLan()
    {
        // Act
        var type = _prioritizer.GetConnectionType("quic://192.168.1.1:22000");

        // Assert
        Assert.Equal(ConnectionType.QuicLan, type);
    }

    [Fact]
    public void GetConnectionType_IdentifiesQuicWan()
    {
        // Act
        var type = _prioritizer.GetConnectionType("quic://8.8.8.8:22000");

        // Assert
        Assert.Equal(ConnectionType.QuicWan, type);
    }

    [Fact]
    public void GetConnectionType_IdentifiesTcpLan()
    {
        // Act
        var type = _prioritizer.GetConnectionType("tcp://10.0.0.1:22000");

        // Assert
        Assert.Equal(ConnectionType.TcpLan, type);
    }

    [Fact]
    public void GetConnectionType_IdentifiesTcpWan()
    {
        // Act
        var type = _prioritizer.GetConnectionType("tcp://203.0.113.1:22000");

        // Assert
        Assert.Equal(ConnectionType.TcpWan, type);
    }

    [Fact]
    public void GetConnectionType_IdentifiesRelay()
    {
        // Act
        var type = _prioritizer.GetConnectionType("relay://relay.syncthing.net:443");

        // Assert
        Assert.Equal(ConnectionType.Relay, type);
    }

    [Theory]
    [InlineData("tcp://192.168.1.1:22000", true)]    // 192.168.x.x
    [InlineData("tcp://10.0.0.1:22000", true)]       // 10.x.x.x
    [InlineData("tcp://172.16.1.1:22000", true)]     // 172.16-31.x.x
    [InlineData("tcp://172.31.255.255:22000", true)] // 172.16-31.x.x
    [InlineData("tcp://169.254.1.1:22000", true)]    // Link-local
    [InlineData("tcp://127.0.0.1:22000", true)]      // Loopback
    [InlineData("tcp://8.8.8.8:22000", false)]       // Public IP
    [InlineData("tcp://203.0.113.1:22000", false)]   // Public IP (TEST-NET-3)
    public void IsLanAddress_ReturnsCorrectValue(string address, bool expectedIsLan)
    {
        // Act
        var isLan = _prioritizer.IsLanAddress(address);

        // Assert
        Assert.Equal(expectedIsLan, isLan);
    }

    [Fact]
    public void PrioritizeAddresses_SortsCorrectly()
    {
        // Arrange
        var addresses = new[]
        {
            "relay://relay.syncthing.net:443",
            "tcp://8.8.8.8:22000",
            "quic://192.168.1.1:22000",
            "tcp://192.168.1.2:22000",
            "quic://8.8.4.4:22000"
        };

        // Act
        var prioritized = _prioritizer.PrioritizeAddresses(addresses).ToList();

        // Assert
        Assert.Equal(5, prioritized.Count);
        Assert.Equal("quic://192.168.1.1:22000", prioritized[0].Address); // QUIC LAN first
        Assert.Equal("quic://8.8.4.4:22000", prioritized[1].Address);      // QUIC WAN
        Assert.Equal("tcp://192.168.1.2:22000", prioritized[2].Address);   // TCP LAN
        Assert.Equal("tcp://8.8.8.8:22000", prioritized[3].Address);       // TCP WAN
        Assert.Equal("relay://relay.syncthing.net:443", prioritized[4].Address); // Relay last
    }

    [Fact]
    public void GetPriorityBuckets_GroupsCorrectly()
    {
        // Arrange
        var addresses = new[]
        {
            "tcp://192.168.1.1:22000",  // TCP LAN (10)
            "tcp://192.168.1.2:22000",  // TCP LAN (10)
            "tcp://8.8.8.8:22000",      // TCP WAN (15)
            "relay://relay.syncthing.net:443" // Relay (90)
        };

        // Act
        var buckets = _prioritizer.GetPriorityBuckets(addresses).ToList();

        // Assert
        Assert.Equal(3, buckets.Count);
        Assert.Equal(10, buckets[0].Key); // TCP LAN bucket
        Assert.Equal(2, buckets[0].Count()); // 2 TCP LAN addresses
        Assert.Equal(15, buckets[1].Key); // TCP WAN bucket
        Assert.Single(buckets[1]); // 1 TCP WAN address
        Assert.Equal(90, buckets[2].Key); // Relay bucket
        Assert.Single(buckets[2]); // 1 Relay address
    }

    [Theory]
    [InlineData(90, 10, 5, true)]   // Current 90, new 10, threshold 5 → upgrade (90-10=80 >= 5)
    [InlineData(15, 10, 5, true)]   // Current 15, new 10, threshold 5 → upgrade (15-10=5 >= 5)
    [InlineData(15, 10, 10, false)] // Current 15, new 10, threshold 10 → no upgrade (15-10=5 < 10)
    [InlineData(10, 10, 5, false)]  // Same priority → no upgrade
    [InlineData(10, 15, 5, false)]  // New is worse → no upgrade
    public void ShouldUpgrade_ReturnsCorrectValue(int currentPriority, int newPriority, int threshold, bool expectedShouldUpgrade)
    {
        // Act
        var shouldUpgrade = _prioritizer.ShouldUpgrade(currentPriority, newPriority, threshold);

        // Assert
        Assert.Equal(expectedShouldUpgrade, shouldUpgrade);
    }

    [Fact]
    public void Configuration_HasCorrectDefaults()
    {
        // Assert
        Assert.Equal(0, _prioritizer.Configuration.QuicLanPriority);
        Assert.Equal(5, _prioritizer.Configuration.QuicWanPriority);
        Assert.Equal(10, _prioritizer.Configuration.TcpLanPriority);
        Assert.Equal(15, _prioritizer.Configuration.TcpWanPriority);
        Assert.Equal(90, _prioritizer.Configuration.RelayPriority);
        Assert.Equal(5, _prioritizer.Configuration.UpgradeThreshold);
        Assert.Equal(64, _prioritizer.Configuration.MaxParallelDials);
        Assert.Equal(8, _prioritizer.Configuration.MaxParallelDialsPerDevice);
    }

    [Fact]
    public void CustomConfiguration_IsRespected()
    {
        // Arrange
        var config = new ConnectionPriorityConfiguration
        {
            QuicLanPriority = 1,
            TcpLanPriority = 5,
            RelayPriority = 100
        };
        var customPrioritizer = new ConnectionPrioritizer(_loggerMock.Object, config);

        // Act & Assert
        Assert.Equal(1, customPrioritizer.CalculatePriority("quic://192.168.1.1:22000"));
        Assert.Equal(5, customPrioritizer.CalculatePriority("tcp://192.168.1.1:22000"));
        Assert.Equal(100, customPrioritizer.CalculatePriority("relay://relay.syncthing.net:443"));
    }

    [Fact]
    public void AlwaysLocalNetworks_AreRespected()
    {
        // Arrange
        var config = new ConnectionPriorityConfiguration
        {
            AlwaysLocalNetworks = new List<string> { "100.64.0.0/10" } // CGNAT range
        };
        var customPrioritizer = new ConnectionPrioritizer(_loggerMock.Object, config);

        // Act
        var isLan = customPrioritizer.IsLanAddress("tcp://100.64.1.1:22000");

        // Assert
        Assert.True(isLan);
    }

    [Fact]
    public void GetConnectionType_HandlesEmptyAddress()
    {
        // Act
        var type = _prioritizer.GetConnectionType(string.Empty);

        // Assert
        Assert.Equal(ConnectionType.Unknown, type);
    }

    [Fact]
    public void GetConnectionType_HandlesNullAddress()
    {
        // Act
        var type = _prioritizer.GetConnectionType(null!);

        // Assert
        Assert.Equal(ConnectionType.Unknown, type);
    }

    [Fact]
    public void IsLanAddress_HandlesInvalidAddress()
    {
        // Act
        var isLan = _prioritizer.IsLanAddress("invalid-address");

        // Assert
        Assert.False(isLan);
    }

    [Fact]
    public void PrioritizedAddress_HasCorrectProperties()
    {
        // Arrange
        var addresses = new[] { "tcp://192.168.1.1:22000" };

        // Act
        var prioritized = _prioritizer.PrioritizeAddresses(addresses).First();

        // Assert
        Assert.Equal("tcp://192.168.1.1:22000", prioritized.Address);
        Assert.Equal(10, prioritized.Priority);
        Assert.Equal(ConnectionType.TcpLan, prioritized.Type);
        Assert.True(prioritized.IsLan);
        Assert.NotNull(prioritized.IpAddress);
        Assert.Equal(22000, prioritized.Port);
    }
}
