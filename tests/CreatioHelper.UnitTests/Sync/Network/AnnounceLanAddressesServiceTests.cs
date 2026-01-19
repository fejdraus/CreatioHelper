using System.Net;
using CreatioHelper.Infrastructure.Services.Sync.Network;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.Network;

public class AnnounceLanAddressesServiceTests
{
    private readonly Mock<ILogger<AnnounceLanAddressesService>> _loggerMock;
    private readonly AnnounceLanAddressesConfiguration _config;
    private readonly AnnounceLanAddressesService _service;

    public AnnounceLanAddressesServiceTests()
    {
        _loggerMock = new Mock<ILogger<AnnounceLanAddressesService>>();
        _config = new AnnounceLanAddressesConfiguration();
        _service = new AnnounceLanAddressesService(_loggerMock.Object, _config);
    }

    #region IsEnabled Tests

    [Fact]
    public void IsEnabled_Default_ReturnsTrue()
    {
        Assert.True(_service.IsEnabled);
    }

    [Fact]
    public void IsEnabled_AfterDisabled_ReturnsFalse()
    {
        _service.SetEnabled(false);

        Assert.False(_service.IsEnabled);
    }

    #endregion

    #region SetEnabled Tests

    [Fact]
    public void SetEnabled_False_DisablesAnnouncement()
    {
        _service.SetEnabled(false);

        Assert.False(_service.IsEnabled);
    }

    [Fact]
    public void SetEnabled_True_EnablesAnnouncement()
    {
        _service.SetEnabled(false);
        _service.SetEnabled(true);

        Assert.True(_service.IsEnabled);
    }

    #endregion

    #region GetLanAddresses Tests

    [Fact]
    public void GetLanAddresses_Enabled_ReturnsAddresses()
    {
        var addresses = _service.GetLanAddresses();

        Assert.NotNull(addresses);
        // May be empty on some systems, but should not throw
    }

    [Fact]
    public void GetLanAddresses_Disabled_ReturnsEmpty()
    {
        _service.SetEnabled(false);

        var addresses = _service.GetLanAddresses();

        Assert.Empty(addresses);
    }

    #endregion

    #region GetAddressesForInterface Tests

    [Fact]
    public void GetAddressesForInterface_ValidInterface_ReturnsAddresses()
    {
        var addresses = _service.GetAddressesForInterface("eth0");

        Assert.NotNull(addresses);
    }

    [Fact]
    public void GetAddressesForInterface_NullInterface_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetAddressesForInterface(null!));
    }

    #endregion

    #region IsLanAddress Tests

    [Theory]
    [InlineData("10.0.0.1", true)]
    [InlineData("10.255.255.255", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("192.168.255.255", true)]
    [InlineData("169.254.1.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("172.32.0.1", false)]
    public void IsLanAddress_IPv4_ReturnsExpected(string ip, bool expected)
    {
        var address = IPAddress.Parse(ip);

        var result = _service.IsLanAddress(address);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsLanAddress_NullAddress_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.IsLanAddress(null!));
    }

    #endregion

    #region IsLinkLocalAddress Tests

    [Theory]
    [InlineData("169.254.0.1", true)]
    [InlineData("169.254.255.255", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("10.0.0.1", false)]
    public void IsLinkLocalAddress_IPv4_ReturnsExpected(string ip, bool expected)
    {
        var address = IPAddress.Parse(ip);

        var result = _service.IsLinkLocalAddress(address);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsLinkLocalAddress_NullAddress_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.IsLinkLocalAddress(null!));
    }

    #endregion

    #region AddCustomAddress Tests

    [Fact]
    public void AddCustomAddress_ValidAddress_AddsToList()
    {
        var ip = IPAddress.Parse("192.168.1.100");

        _service.AddCustomAddress(ip, 22000);

        var addresses = _service.GetLanAddresses();
        Assert.Contains(addresses, a => a.Address.Equals(ip) && a.Port == 22000);
    }

    [Fact]
    public void AddCustomAddress_MarkedAsCustom()
    {
        var ip = IPAddress.Parse("192.168.1.100");

        _service.AddCustomAddress(ip, 22000);

        var addresses = _service.GetLanAddresses();
        var custom = addresses.FirstOrDefault(a => a.Address.Equals(ip));
        Assert.NotNull(custom);
        Assert.True(custom.IsCustom);
        Assert.Equal(AddressType.Custom, custom.Type);
    }

    [Fact]
    public void AddCustomAddress_NullAddress_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.AddCustomAddress(null!, 22000));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void AddCustomAddress_InvalidPort_ThrowsArgumentOutOfRange(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _service.AddCustomAddress(IPAddress.Loopback, port));
    }

    #endregion

    #region RemoveCustomAddress Tests

    [Fact]
    public void RemoveCustomAddress_Exists_ReturnsTrue()
    {
        var ip = IPAddress.Parse("192.168.1.100");
        _service.AddCustomAddress(ip, 22000);

        var result = _service.RemoveCustomAddress(ip);

        Assert.True(result);
    }

    [Fact]
    public void RemoveCustomAddress_NotExists_ReturnsFalse()
    {
        var result = _service.RemoveCustomAddress(IPAddress.Parse("192.168.1.100"));

        Assert.False(result);
    }

    [Fact]
    public void RemoveCustomAddress_NullAddress_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.RemoveCustomAddress(null!));
    }

    #endregion

    #region GetNetworkInterfaces Tests

    [Fact]
    public void GetNetworkInterfaces_ReturnsInterfaces()
    {
        var interfaces = _service.GetNetworkInterfaces();

        Assert.NotNull(interfaces);
        // May be empty on some systems
    }

    #endregion

    #region FilterForAnnouncement Tests

    [Fact]
    public void FilterForAnnouncement_ExcludesLoopback()
    {
        var addresses = new List<LanAddress>
        {
            new() { Address = IPAddress.Loopback, Port = 22000, Type = AddressType.Loopback },
            new() { Address = IPAddress.Parse("192.168.1.1"), Port = 22000, Type = AddressType.Private }
        };

        var filtered = _service.FilterForAnnouncement(addresses);

        Assert.DoesNotContain(filtered, a => a.Type == AddressType.Loopback);
        Assert.Contains(filtered, a => a.Type == AddressType.Private);
    }

    [Fact]
    public void FilterForAnnouncement_NullAddresses_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.FilterForAnnouncement(null!));
    }

    #endregion

    #region GetStats Tests

    [Fact]
    public void GetStats_ReturnsValidStats()
    {
        var stats = _service.GetStats();

        Assert.NotNull(stats);
        Assert.True(stats.RefreshCount >= 1);
    }

    [Fact]
    public void GetStats_CountsCustomAddresses()
    {
        _service.AddCustomAddress(IPAddress.Parse("192.168.1.100"), 22000);

        var stats = _service.GetStats();

        Assert.True(stats.CustomAddresses >= 1);
    }

    #endregion

    #region RefreshAddresses Tests

    [Fact]
    public void RefreshAddresses_IncrementsRefreshCount()
    {
        var initialStats = _service.GetStats();
        var initialCount = initialStats.RefreshCount;

        _service.RefreshAddresses();

        var stats = _service.GetStats();
        Assert.Equal(initialCount + 1, stats.RefreshCount);
    }

    [Fact]
    public void RefreshAddresses_UpdatesLastRefresh()
    {
        var before = DateTime.UtcNow;

        _service.RefreshAddresses();

        var stats = _service.GetStats();
        Assert.True(stats.LastRefresh >= before);
    }

    #endregion
}

public class LanAddressTests
{
    [Fact]
    public void FullAddress_ReturnsCorrectFormat()
    {
        var address = new LanAddress
        {
            Address = IPAddress.Parse("192.168.1.1"),
            Port = 22000
        };

        Assert.Equal("192.168.1.1:22000", address.FullAddress);
    }

    [Fact]
    public void ToString_ReturnsFullAddress()
    {
        var address = new LanAddress
        {
            Address = IPAddress.Parse("192.168.1.1"),
            Port = 22000
        };

        Assert.Equal("192.168.1.1:22000", address.ToString());
    }
}

public class AnnounceLanAddressesConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new AnnounceLanAddressesConfiguration();

        Assert.True(config.Enabled);
        Assert.Equal(22000, config.DefaultPort);
        Assert.True(config.IncludeLinkLocal);
        Assert.True(config.IncludeIPv6);
        Assert.NotEmpty(config.ExcludedInterfaces);
    }
}
