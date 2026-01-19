using System.Net;
using CreatioHelper.Infrastructure.Services.Sync.Network;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.Network;

public class AlwaysLocalNetsServiceTests
{
    private readonly Mock<ILogger<AlwaysLocalNetsService>> _loggerMock;
    private readonly AlwaysLocalNetsConfiguration _config;
    private readonly AlwaysLocalNetsService _service;

    public AlwaysLocalNetsServiceTests()
    {
        _loggerMock = new Mock<ILogger<AlwaysLocalNetsService>>();
        _config = new AlwaysLocalNetsConfiguration();
        _service = new AlwaysLocalNetsService(_loggerMock.Object, _config);
    }

    #region IsLocalAddress (IPAddress) Tests

    [Fact]
    public void IsLocalAddress_Loopback_ReturnsTrue()
    {
        Assert.True(_service.IsLocalAddress(IPAddress.Loopback));
        Assert.True(_service.IsLocalAddress(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void IsLocalAddress_Private10_ReturnsTrue()
    {
        Assert.True(_service.IsLocalAddress(IPAddress.Parse("10.0.0.1")));
        Assert.True(_service.IsLocalAddress(IPAddress.Parse("10.255.255.255")));
    }

    [Fact]
    public void IsLocalAddress_Private172_ReturnsTrue()
    {
        Assert.True(_service.IsLocalAddress(IPAddress.Parse("172.16.0.1")));
        Assert.True(_service.IsLocalAddress(IPAddress.Parse("172.31.255.255")));
    }

    [Fact]
    public void IsLocalAddress_Private192_ReturnsTrue()
    {
        Assert.True(_service.IsLocalAddress(IPAddress.Parse("192.168.0.1")));
        Assert.True(_service.IsLocalAddress(IPAddress.Parse("192.168.255.255")));
    }

    [Fact]
    public void IsLocalAddress_LinkLocal_ReturnsTrue()
    {
        Assert.True(_service.IsLocalAddress(IPAddress.Parse("169.254.1.1")));
    }

    [Fact]
    public void IsLocalAddress_Public_ReturnsFalse()
    {
        Assert.False(_service.IsLocalAddress(IPAddress.Parse("8.8.8.8")));
        Assert.False(_service.IsLocalAddress(IPAddress.Parse("1.1.1.1")));
    }

    [Fact]
    public void IsLocalAddress_AlwaysLocal_ReturnsTrue()
    {
        _service.AddLocalNetwork("203.0.113.0/24");

        Assert.True(_service.IsLocalAddress(IPAddress.Parse("203.0.113.1")));
        Assert.True(_service.IsLocalAddress(IPAddress.Parse("203.0.113.255")));
    }

    [Fact]
    public void IsLocalAddress_NotInAlwaysLocal_ReturnsFalse()
    {
        _service.AddLocalNetwork("203.0.113.0/24");

        Assert.False(_service.IsLocalAddress(IPAddress.Parse("203.0.114.1")));
    }

    [Fact]
    public void IsLocalAddress_NullAddress_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.IsLocalAddress((IPAddress)null!));
    }

    #endregion

    #region IsLocalAddress (string) Tests

    [Fact]
    public void IsLocalAddress_StringIP_ParsesCorrectly()
    {
        Assert.True(_service.IsLocalAddress("127.0.0.1"));
        Assert.True(_service.IsLocalAddress("192.168.1.1"));
        Assert.False(_service.IsLocalAddress("8.8.8.8"));
    }

    [Fact]
    public void IsLocalAddress_StringIPWithPort_ParsesCorrectly()
    {
        Assert.True(_service.IsLocalAddress("192.168.1.1:8080"));
        Assert.False(_service.IsLocalAddress("8.8.8.8:53"));
    }

    [Fact]
    public void IsLocalAddress_IPv6WithBrackets_ParsesCorrectly()
    {
        Assert.True(_service.IsLocalAddress("[::1]"));
        Assert.True(_service.IsLocalAddress("[::1]:8080"));
    }

    [Fact]
    public void IsLocalAddress_EmptyString_ReturnsFalse()
    {
        Assert.False(_service.IsLocalAddress(string.Empty));
        Assert.False(_service.IsLocalAddress((string)null!));
    }

    #endregion

    #region AddLocalNetwork Tests

    [Fact]
    public void AddLocalNetwork_ValidCIDR_Adds()
    {
        _service.AddLocalNetwork("203.0.113.0/24");

        var networks = _service.GetLocalNetworks();
        Assert.Contains("203.0.113.0/24", networks);
    }

    [Fact]
    public void AddLocalNetwork_Duplicate_DoesNotAddTwice()
    {
        _service.AddLocalNetwork("203.0.113.0/24");
        _service.AddLocalNetwork("203.0.113.0/24");

        var networks = _service.GetLocalNetworks();
        Assert.Single(networks);
    }

    [Fact]
    public void AddLocalNetwork_InvalidCIDR_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() =>
            _service.AddLocalNetwork("invalid"));
    }

    [Fact]
    public void AddLocalNetwork_NullCIDR_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.AddLocalNetwork(null!));
    }

    #endregion

    #region RemoveLocalNetwork Tests

    [Fact]
    public void RemoveLocalNetwork_Exists_RemovesAndReturnsTrue()
    {
        _service.AddLocalNetwork("203.0.113.0/24");

        var result = _service.RemoveLocalNetwork("203.0.113.0/24");

        Assert.True(result);
        Assert.DoesNotContain("203.0.113.0/24", _service.GetLocalNetworks());
    }

    [Fact]
    public void RemoveLocalNetwork_NotExists_ReturnsFalse()
    {
        var result = _service.RemoveLocalNetwork("203.0.113.0/24");

        Assert.False(result);
    }

    #endregion

    #region ClearLocalNetworks Tests

    [Fact]
    public void ClearLocalNetworks_RemovesAll()
    {
        _service.AddLocalNetwork("203.0.113.0/24");
        _service.AddLocalNetwork("198.51.100.0/24");

        _service.ClearLocalNetworks();

        Assert.Empty(_service.GetLocalNetworks());
    }

    #endregion

    #region IsPrivateAddress Tests

    [Fact]
    public void IsPrivateAddress_RFC1918_ReturnsTrue()
    {
        Assert.True(_service.IsPrivateAddress(IPAddress.Parse("10.0.0.1")));
        Assert.True(_service.IsPrivateAddress(IPAddress.Parse("172.16.0.1")));
        Assert.True(_service.IsPrivateAddress(IPAddress.Parse("192.168.0.1")));
    }

    [Fact]
    public void IsPrivateAddress_Public_ReturnsFalse()
    {
        Assert.False(_service.IsPrivateAddress(IPAddress.Parse("8.8.8.8")));
        Assert.False(_service.IsPrivateAddress(IPAddress.Parse("172.32.0.1"))); // Not in 172.16-31
    }

    [Fact]
    public void IsPrivateAddress_NullAddress_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.IsPrivateAddress(null!));
    }

    #endregion

    #region GetNetworkType Tests

    [Fact]
    public void GetNetworkType_Loopback_ReturnsLoopback()
    {
        Assert.Equal(NetworkType.Loopback, _service.GetNetworkType(IPAddress.Loopback));
    }

    [Fact]
    public void GetNetworkType_LinkLocal_ReturnsLinkLocal()
    {
        Assert.Equal(NetworkType.LinkLocal, _service.GetNetworkType(IPAddress.Parse("169.254.1.1")));
    }

    [Fact]
    public void GetNetworkType_Private_ReturnsPrivate()
    {
        Assert.Equal(NetworkType.Private, _service.GetNetworkType(IPAddress.Parse("192.168.1.1")));
    }

    [Fact]
    public void GetNetworkType_AlwaysLocal_ReturnsAlwaysLocal()
    {
        _service.AddLocalNetwork("203.0.113.0/24");

        Assert.Equal(NetworkType.AlwaysLocal, _service.GetNetworkType(IPAddress.Parse("203.0.113.1")));
    }

    [Fact]
    public void GetNetworkType_Public_ReturnsPublic()
    {
        Assert.Equal(NetworkType.Public, _service.GetNetworkType(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void GetNetworkType_NullAddress_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetNetworkType(null!));
    }

    #endregion

    #region Configuration Tests

    [Fact]
    public void TreatPrivateAsLocal_False_PrivateNotLocal()
    {
        var config = new AlwaysLocalNetsConfiguration { TreatPrivateAsLocal = false };
        var service = new AlwaysLocalNetsService(_loggerMock.Object, config);

        Assert.False(service.IsLocalAddress(IPAddress.Parse("192.168.1.1")));
    }

    [Fact]
    public void TreatLinkLocalAsLocal_False_LinkLocalNotLocal()
    {
        var config = new AlwaysLocalNetsConfiguration { TreatLinkLocalAsLocal = false };
        var service = new AlwaysLocalNetsService(_loggerMock.Object, config);

        Assert.False(service.IsLocalAddress(IPAddress.Parse("169.254.1.1")));
    }

    [Fact]
    public void InitialConfig_LoadsNetworks()
    {
        var config = new AlwaysLocalNetsConfiguration();
        config.AlwaysLocalNets.Add("203.0.113.0/24");
        var service = new AlwaysLocalNetsService(_loggerMock.Object, config);

        Assert.True(service.IsLocalAddress(IPAddress.Parse("203.0.113.1")));
    }

    #endregion
}

public class NetworkRangeTests
{
    [Fact]
    public void Contains_IPv4InRange_ReturnsTrue()
    {
        var range = new NetworkRange("192.168.1.0/24");

        Assert.True(range.Contains(IPAddress.Parse("192.168.1.1")));
        Assert.True(range.Contains(IPAddress.Parse("192.168.1.255")));
    }

    [Fact]
    public void Contains_IPv4OutOfRange_ReturnsFalse()
    {
        var range = new NetworkRange("192.168.1.0/24");

        Assert.False(range.Contains(IPAddress.Parse("192.168.2.1")));
    }

    [Fact]
    public void Contains_DifferentAddressFamily_ReturnsFalse()
    {
        var range = new NetworkRange("192.168.1.0/24");

        Assert.False(range.Contains(IPAddress.IPv6Loopback));
    }

    [Fact]
    public void Contains_IPv4HostAddress_WorksCorrectly()
    {
        var range = new NetworkRange("10.0.0.1/32");

        Assert.True(range.Contains(IPAddress.Parse("10.0.0.1")));
        Assert.False(range.Contains(IPAddress.Parse("10.0.0.2")));
    }

    [Fact]
    public void Contains_LargeCIDR_WorksCorrectly()
    {
        var range = new NetworkRange("10.0.0.0/8");

        Assert.True(range.Contains(IPAddress.Parse("10.0.0.1")));
        Assert.True(range.Contains(IPAddress.Parse("10.255.255.255")));
        Assert.False(range.Contains(IPAddress.Parse("11.0.0.1")));
    }

    [Fact]
    public void Parse_WithoutPrefixLength_DefaultsTo32()
    {
        var range = new NetworkRange("192.168.1.1");

        Assert.Equal(32, range.PrefixLength);
    }
}

public class AlwaysLocalNetsConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new AlwaysLocalNetsConfiguration();

        Assert.True(config.TreatPrivateAsLocal);
        Assert.True(config.TreatLinkLocalAsLocal);
        Assert.Empty(config.AlwaysLocalNets);
    }
}
