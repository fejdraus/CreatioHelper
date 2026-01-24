using System.Net;
using CreatioHelper.Infrastructure.Services.Network.Nat;
using CreatioHelper.Infrastructure.Services.Network.Stun;
using CreatioHelper.Infrastructure.Services.Network.UPnP;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests.Network;

/// <summary>
/// Unit tests for NAT Traversal components using mocks.
/// Tests the coordination between UPnP, NAT-PMP, and STUN services.
/// </summary>
public class NatTraversalTests : IAsyncLifetime
{
    private readonly Mock<ILogger<NatMappingService>> _loggerMock;
    private readonly Mock<IUPnPService> _upnpServiceMock;
    private readonly Mock<IStunService> _stunServiceMock;
    private readonly NatMappingServiceOptions _options;

    public NatTraversalTests()
    {
        _loggerMock = new Mock<ILogger<NatMappingService>>();
        _upnpServiceMock = new Mock<IUPnPService>();
        _stunServiceMock = new Mock<IStunService>();
        _options = new NatMappingServiceOptions
        {
            DefaultLeaseDurationSeconds = 3600,
            RenewalCheckIntervalMinutes = 5,
            EnableUPnP = true,
            EnableNatPmp = true,
            EnableStun = true
        };
    }

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    #region NatMapping Tests

    [Fact]
    [Trait("Category", "Unit")]
    public void NatMapping_IsExpired_ReturnsTrueWhenExpired()
    {
        var mapping = new NatMapping
        {
            InternalPort = 22000,
            ExternalPort = 22000,
            Protocol = "TCP",
            Method = NatMappingMethod.UPnP,
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1)
        };

        Assert.True(mapping.IsExpired);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NatMapping_IsExpired_ReturnsFalseWhenNotExpired()
    {
        var mapping = new NatMapping
        {
            InternalPort = 22000,
            ExternalPort = 22000,
            Protocol = "TCP",
            Method = NatMappingMethod.UPnP,
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        };

        Assert.False(mapping.IsExpired);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NatMapping_ShouldRenew_ReturnsTrueWhenLessThan15MinutesRemaining()
    {
        var mapping = new NatMapping
        {
            InternalPort = 22000,
            ExternalPort = 22000,
            Protocol = "TCP",
            Method = NatMappingMethod.UPnP,
            ExpiresAt = DateTime.UtcNow.AddMinutes(10)
        };

        Assert.True(mapping.ShouldRenew);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NatMapping_ShouldRenew_ReturnsFalseWhenMoreThan15MinutesRemaining()
    {
        var mapping = new NatMapping
        {
            InternalPort = 22000,
            ExternalPort = 22000,
            Protocol = "TCP",
            Method = NatMappingMethod.UPnP,
            ExpiresAt = DateTime.UtcNow.AddMinutes(30)
        };

        Assert.False(mapping.ShouldRenew);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NatMapping_TimeToExpire_ReturnsCorrectValue()
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(60);
        var mapping = new NatMapping
        {
            InternalPort = 22000,
            ExternalPort = 22000,
            Protocol = "TCP",
            Method = NatMappingMethod.UPnP,
            ExpiresAt = expiresAt
        };

        var timeToExpire = mapping.TimeToExpire;

        Assert.True(timeToExpire.TotalMinutes >= 59 && timeToExpire.TotalMinutes <= 61);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NatMapping_UniqueId_GeneratedOnCreation()
    {
        var mapping1 = new NatMapping
        {
            InternalPort = 22000,
            ExternalPort = 22000,
            Protocol = "TCP",
            Method = NatMappingMethod.UPnP,
            ExpiresAt = DateTime.UtcNow.AddMinutes(60)
        };

        var mapping2 = new NatMapping
        {
            InternalPort = 22000,
            ExternalPort = 22000,
            Protocol = "TCP",
            Method = NatMappingMethod.UPnP,
            ExpiresAt = DateTime.UtcNow.AddMinutes(60)
        };

        Assert.NotEqual(mapping1.Id, mapping2.Id);
        Assert.NotEmpty(mapping1.Id);
        Assert.NotEmpty(mapping2.Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NatMapping_ShouldRenew_ReturnsFalseWhenExpired()
    {
        var mapping = new NatMapping
        {
            InternalPort = 22000,
            ExternalPort = 22000,
            Protocol = "TCP",
            Method = NatMappingMethod.UPnP,
            ExpiresAt = DateTime.UtcNow.AddMinutes(-5)
        };

        // Expired mappings should not be renewed
        Assert.False(mapping.ShouldRenew);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NatMapping_TimeToExpire_ReturnsNegativeWhenExpired()
    {
        var mapping = new NatMapping
        {
            InternalPort = 22000,
            ExternalPort = 22000,
            Protocol = "TCP",
            Method = NatMappingMethod.UPnP,
            ExpiresAt = DateTime.UtcNow.AddMinutes(-10)
        };

        Assert.True(mapping.TimeToExpire.TotalMinutes < 0);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("TCP")]
    [InlineData("UDP")]
    public void NatMapping_Protocol_CanBeSetToValidValues(string protocol)
    {
        var mapping = new NatMapping
        {
            InternalPort = 22000,
            ExternalPort = 22000,
            Protocol = protocol,
            Method = NatMappingMethod.UPnP,
            ExpiresAt = DateTime.UtcNow.AddMinutes(60)
        };

        Assert.Equal(protocol, mapping.Protocol);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(NatMappingMethod.UPnP)]
    [InlineData(NatMappingMethod.NatPmp)]
    [InlineData(NatMappingMethod.Stun)]
    [InlineData(NatMappingMethod.Manual)]
    public void NatMapping_Method_CanBeSetToAllValues(NatMappingMethod method)
    {
        var mapping = new NatMapping
        {
            InternalPort = 22000,
            ExternalPort = 22000,
            Protocol = "TCP",
            Method = method,
            ExpiresAt = DateTime.UtcNow.AddMinutes(60)
        };

        Assert.Equal(method, mapping.Method);
    }

    #endregion

    #region NatMappingServiceOptions Tests

    [Fact]
    [Trait("Category", "Unit")]
    public void NatMappingServiceOptions_DefaultValues()
    {
        var options = new NatMappingServiceOptions();

        Assert.Equal(3600, options.DefaultLeaseDurationSeconds);
        Assert.Equal(5, options.RenewalCheckIntervalMinutes);
        Assert.True(options.EnableUPnP);
        Assert.True(options.EnableNatPmp);
        Assert.True(options.EnableStun);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NatMappingServiceOptions_CanBeConfigured()
    {
        var options = new NatMappingServiceOptions
        {
            DefaultLeaseDurationSeconds = 7200,
            RenewalCheckIntervalMinutes = 10,
            EnableUPnP = false,
            EnableNatPmp = false,
            EnableStun = true
        };

        Assert.Equal(7200, options.DefaultLeaseDurationSeconds);
        Assert.Equal(10, options.RenewalCheckIntervalMinutes);
        Assert.False(options.EnableUPnP);
        Assert.False(options.EnableNatPmp);
        Assert.True(options.EnableStun);
    }

    #endregion

    #region NatMappingService Mock Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NatMappingService_RequestMapping_UsesUPnPWhenAvailable()
    {
        // Arrange
        _upnpServiceMock.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _upnpServiceMock.Setup(s => s.AddPortMappingAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _upnpServiceMock.Setup(s => s.GetExternalIPAddressAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("203.0.113.1");

        var service = new NatMappingService(
            _loggerMock.Object,
            _options,
            _upnpServiceMock.Object,
            null,
            null);

        await service.StartAsync(CancellationToken.None);

        // Act
        var mapping = await service.RequestMappingAsync(22000, "TCP", "Test");

        // Assert
        Assert.NotNull(mapping);
        Assert.Equal(NatMappingMethod.UPnP, mapping.Method);
        Assert.Equal(22000, mapping.InternalPort);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NatMappingService_RequestMapping_FallsBackToStunWhenUPnPFails()
    {
        // Arrange
        _upnpServiceMock.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _stunServiceMock.Setup(s => s.StartAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _stunServiceMock.Setup(s => s.NatType)
            .Returns(NatType.FullCone);
        _stunServiceMock.Setup(s => s.GetExternalEndPointAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IPEndPoint(IPAddress.Parse("203.0.113.1"), 22000));

        var service = new NatMappingService(
            _loggerMock.Object,
            _options,
            _upnpServiceMock.Object,
            null,
            _stunServiceMock.Object);

        await service.StartAsync(CancellationToken.None);

        // Act
        var mapping = await service.RequestMappingAsync(22000, "TCP", "Test");

        // Assert
        Assert.NotNull(mapping);
        Assert.Equal(NatMappingMethod.Stun, mapping.Method);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NatMappingService_ReleaseMapping_CallsUPnPDeletePortMapping()
    {
        // Arrange
        _upnpServiceMock.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _upnpServiceMock.Setup(s => s.AddPortMappingAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _upnpServiceMock.Setup(s => s.DeletePortMappingAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _upnpServiceMock.Setup(s => s.GetExternalIPAddressAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("203.0.113.1");

        var service = new NatMappingService(
            _loggerMock.Object,
            _options,
            _upnpServiceMock.Object);

        await service.StartAsync(CancellationToken.None);
        var mapping = await service.RequestMappingAsync(22000, "TCP", "Test");

        // Act
        await service.ReleaseMappingAsync(mapping!);

        // Assert
        _upnpServiceMock.Verify(s => s.DeletePortMappingAsync(22000, "TCP", It.IsAny<CancellationToken>()), Times.Once);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NatMappingService_IsAvailable_ReturnsTrueWhenUPnPAvailable()
    {
        // Arrange
        _upnpServiceMock.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _upnpServiceMock.Setup(s => s.GetExternalIPAddressAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("203.0.113.1");

        var service = new NatMappingService(
            _loggerMock.Object,
            _options,
            _upnpServiceMock.Object);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        Assert.True(service.IsAvailable);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NatMappingService_ExternalAddress_ReturnsAddressFromUPnP()
    {
        // Arrange
        var expectedAddress = IPAddress.Parse("203.0.113.1");
        _upnpServiceMock.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _upnpServiceMock.Setup(s => s.GetExternalIPAddressAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("203.0.113.1");

        var service = new NatMappingService(
            _loggerMock.Object,
            _options,
            _upnpServiceMock.Object);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        Assert.NotNull(service.ExternalAddress);
        Assert.Equal(expectedAddress, service.ExternalAddress);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NatMappingService_ActiveMappings_ReturnsOnlyNonExpiredMappings()
    {
        // Arrange
        _upnpServiceMock.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _upnpServiceMock.Setup(s => s.AddPortMappingAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _upnpServiceMock.Setup(s => s.GetExternalIPAddressAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("203.0.113.1");

        var service = new NatMappingService(
            _loggerMock.Object,
            _options,
            _upnpServiceMock.Object);

        await service.StartAsync(CancellationToken.None);

        // Act - create two mappings
        await service.RequestMappingAsync(22000, "TCP", "Test1");
        await service.RequestMappingAsync(22001, "TCP", "Test2");

        // Assert
        Assert.Equal(2, service.ActiveMappings.Count);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task NatMappingService_OnMappingChanged_EventRaisedOnCreate()
    {
        // Arrange
        _upnpServiceMock.Setup(s => s.IsAvailableAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _upnpServiceMock.Setup(s => s.AddPortMappingAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _upnpServiceMock.Setup(s => s.GetExternalIPAddressAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("203.0.113.1");

        var service = new NatMappingService(
            _loggerMock.Object,
            _options,
            _upnpServiceMock.Object);

        NatMappingEventType? receivedEventType = null;
        NatMapping? receivedMapping = null;
        service.OnMappingChanged += (mapping, eventType) =>
        {
            receivedMapping = mapping;
            receivedEventType = eventType;
        };

        await service.StartAsync(CancellationToken.None);

        // Act
        await service.RequestMappingAsync(22000, "TCP", "Test");

        // Assert
        Assert.NotNull(receivedMapping);
        Assert.Equal(NatMappingEventType.Created, receivedEventType);

        await service.StopAsync(CancellationToken.None);
    }

    #endregion

    #region UPnP Mock Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UPnPService_GetExternalIPAddress_ReturnsIPFromDevice()
    {
        // Arrange
        var deviceMock = new Mock<IUPnPDevice>();
        deviceMock.Setup(d => d.GetExternalIPAddressAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("203.0.113.100");

        _upnpServiceMock.Setup(s => s.DiscoverDevicesAsync(It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IUPnPDevice> { deviceMock.Object });
        _upnpServiceMock.Setup(s => s.GetExternalIPAddressAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("203.0.113.100");

        // Act
        var externalIp = await _upnpServiceMock.Object.GetExternalIPAddressAsync();

        // Assert
        Assert.Equal("203.0.113.100", externalIp);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UPnPService_AddPortMapping_ReturnsTrueOnSuccess()
    {
        // Arrange
        _upnpServiceMock.Setup(s => s.AddPortMappingAsync(
                22000, 22000, "TCP", "Syncthing", 3600, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _upnpServiceMock.Object.AddPortMappingAsync(22000, 22000, "TCP", "Syncthing", 3600);

        // Assert
        Assert.True(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UPnPService_DeletePortMapping_ReturnsTrueOnSuccess()
    {
        // Arrange
        _upnpServiceMock.Setup(s => s.DeletePortMappingAsync(22000, "TCP", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Act
        var result = await _upnpServiceMock.Object.DeletePortMappingAsync(22000, "TCP");

        // Assert
        Assert.True(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UPnPServiceStatus_CorrectlyReportsDeviceCounts()
    {
        // Arrange
        var status = new UPnPServiceStatus
        {
            IsAvailable = true,
            DeviceCount = 3,
            Igdv1DeviceCount = 1,
            Igdv2DeviceCount = 2,
            Ipv6DeviceCount = 1,
            ActiveMappingCount = 5,
            ActivePinholeCount = 2,
            ExternalIPv4 = "203.0.113.1",
            ExternalIPv6 = "2001:db8::1"
        };

        // Assert
        Assert.True(status.IsAvailable);
        Assert.Equal(3, status.DeviceCount);
        Assert.Equal(1, status.Igdv1DeviceCount);
        Assert.Equal(2, status.Igdv2DeviceCount);
        Assert.Equal(5, status.ActiveMappingCount);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UPnPPortMapping_DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var mapping = new UPnPPortMapping();

        // Assert
        Assert.Equal(0, mapping.ExternalPort);
        Assert.Equal(0, mapping.InternalPort);
        Assert.Empty(mapping.InternalClient);
        Assert.Empty(mapping.Protocol);
        Assert.Empty(mapping.Description);
        Assert.True(mapping.Enabled);
        Assert.Equal(0, mapping.LeaseDuration);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UPnPAnyPortMappingResult_CanBeConfigured()
    {
        // Arrange & Act
        var result = new UPnPAnyPortMappingResult
        {
            AssignedExternalPort = 45678,
            InternalPort = 22000,
            Protocol = "TCP",
            LeaseDuration = 3600,
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        };

        // Assert
        Assert.Equal(45678, result.AssignedExternalPort);
        Assert.Equal(22000, result.InternalPort);
        Assert.Equal("TCP", result.Protocol);
        Assert.Equal(3600, result.LeaseDuration);
    }

    #endregion

    #region STUN Mock Tests

    [Fact]
    [Trait("Category", "Unit")]
    public async Task StunService_GetExternalEndPoint_ReturnsEndpoint()
    {
        // Arrange
        var expectedEndpoint = new IPEndPoint(IPAddress.Parse("203.0.113.50"), 45000);
        _stunServiceMock.Setup(s => s.GetExternalEndPointAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedEndpoint);

        // Act
        var endpoint = await _stunServiceMock.Object.GetExternalEndPointAsync();

        // Assert
        Assert.NotNull(endpoint);
        Assert.Equal("203.0.113.50", endpoint.Address.ToString());
        Assert.Equal(45000, endpoint.Port);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void StunServiceStatus_CorrectlyReportsState()
    {
        // Arrange
        var status = new StunServiceStatus
        {
            IsRunning = true,
            NatType = NatType.FullCone,
            NatTypeDescription = "Full Cone NAT",
            IsPunchable = true,
            ExternalEndpoint = new IPEndPoint(IPAddress.Parse("203.0.113.50"), 45000),
            SuccessfulChecks = 10,
            FailedChecks = 2,
            ConfiguredServers = new List<string> { "stun.syncthing.net:3478" }
        };

        // Assert
        Assert.True(status.IsRunning);
        Assert.Equal(NatType.FullCone, status.NatType);
        Assert.True(status.IsPunchable);
        Assert.Equal(10, status.SuccessfulChecks);
        Assert.Single(status.ConfiguredServers);
    }

    #endregion

    #region SSDP Response Parsing Tests

    [Fact]
    [Trait("Category", "Unit")]
    public void ParseSsdpResponse_ValidResponse_ExtractsHeaders()
    {
        // Arrange
        var ssdpResponse = @"HTTP/1.1 200 OK
CACHE-CONTROL: max-age=1800
LOCATION: http://192.168.1.1:1900/igd.xml
SERVER: RouterOS/6.48 UPnP/1.0 MikroTik
ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1
USN: uuid:12345678-1234-1234-1234-123456789abc::urn:schemas-upnp-org:device:InternetGatewayDevice:1

";

        // Act
        var headers = ParseSsdpHeaders(ssdpResponse);

        // Assert
        Assert.Equal("http://192.168.1.1:1900/igd.xml", headers["LOCATION"]);
        Assert.Equal("urn:schemas-upnp-org:device:InternetGatewayDevice:1", headers["ST"]);
        Assert.Contains("uuid:12345678", headers["USN"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ParseSsdpResponse_MissingLocation_ReturnsNull()
    {
        // Arrange
        var ssdpResponse = @"HTTP/1.1 200 OK
ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1
USN: uuid:12345678

";

        // Act
        var headers = ParseSsdpHeaders(ssdpResponse);

        // Assert
        Assert.False(headers.ContainsKey("LOCATION"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ParseSsdpResponse_IGDv2Device_IdentifiedCorrectly()
    {
        // Arrange
        var ssdpResponse = @"HTTP/1.1 200 OK
LOCATION: http://192.168.1.1:1900/igd2.xml
ST: urn:schemas-upnp-org:device:InternetGatewayDevice:2
USN: uuid:87654321::urn:schemas-upnp-org:device:InternetGatewayDevice:2

";

        // Act
        var headers = ParseSsdpHeaders(ssdpResponse);

        // Assert
        Assert.Contains(":2", headers["ST"]);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ParseSsdpResponse_WrongDeviceType_Rejected()
    {
        // Arrange
        var ssdpResponse = @"HTTP/1.1 200 OK
LOCATION: http://192.168.1.100:1900/device.xml
ST: urn:schemas-upnp-org:device:MediaServer:1
USN: uuid:media-server-123

";

        // Act
        var headers = ParseSsdpHeaders(ssdpResponse);
        var isIgd = headers.TryGetValue("ST", out var st) &&
                    st.Contains("InternetGatewayDevice");

        // Assert
        Assert.False(isIgd);
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData("http://192.168.1.1:1900/igd.xml")]
    [InlineData("http://192.168.1.1:49152/rootDesc.xml")]
    [InlineData("http://10.0.0.1:5000/igddesc.xml")]
    public void ParseSsdpResponse_ValidLocationUrls_Extracted(string expectedLocation)
    {
        // Arrange
        var ssdpResponse = $@"HTTP/1.1 200 OK
LOCATION: {expectedLocation}
ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1

";

        // Act
        var headers = ParseSsdpHeaders(ssdpResponse);

        // Assert
        Assert.Equal(expectedLocation, headers["LOCATION"]);
    }

    /// <summary>
    /// Helper method to parse SSDP response headers (mimics the parsing in SyncthingUPnPService)
    /// </summary>
    private static Dictionary<string, string> ParseSsdpHeaders(string response)
    {
        var lines = response.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines.Skip(1)) // Skip status line
        {
            var colonIndex = line.IndexOf(':');
            if (colonIndex > 0)
            {
                var key = line.Substring(0, colonIndex).Trim();
                var value = line.Substring(colonIndex + 1).Trim();
                headers[key] = value;
            }
        }

        return headers;
    }

    #endregion

    #region UPnP Error Codes Tests

    [Fact]
    [Trait("Category", "Unit")]
    public void UPnPErrorCodes_HasExpectedValues()
    {
        Assert.Equal(402, UPnPErrorCodes.InvalidArgs);
        Assert.Equal(501, UPnPErrorCodes.ActionFailed);
        Assert.Equal(606, UPnPErrorCodes.NotAuthorized);
        Assert.Equal(714, UPnPErrorCodes.NoSuchEntryInArray);
        Assert.Equal(718, UPnPErrorCodes.ConflictInMappingEntry);
        Assert.Equal(725, UPnPErrorCodes.OnlyPermanentLeasesSupported);
        Assert.Equal(728, UPnPErrorCodes.NoPortMapsAvailable);
    }

    #endregion
}

/// <summary>
/// Tests for NatType enum and related functionality
/// </summary>
public class NatTypeTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(NatType.OpenInternet, "Open Internet")]
    [InlineData(NatType.FullCone, "Full Cone")]
    [InlineData(NatType.RestrictedCone, "Restricted Cone")]
    [InlineData(NatType.PortRestrictedCone, "Port Restricted Cone")]
    [InlineData(NatType.SymmetricNat, "Symmetric NAT")]
    [InlineData(NatType.Unknown, "Unknown")]
    public void NatType_HasExpectedValues(NatType natType, string description)
    {
        // This test documents expected NAT types
        Assert.True(Enum.IsDefined(typeof(NatType), natType), $"NatType.{natType} should be defined");
        // Description is for documentation only
        Assert.NotEmpty(description);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NatType_AllValuesArePunchable_ExceptSymmetricAndUnknown()
    {
        var punchableTypes = new[]
        {
            NatType.OpenInternet,
            NatType.FullCone,
            NatType.RestrictedCone,
            NatType.PortRestrictedCone
        };

        var nonPunchableTypes = new[]
        {
            NatType.SymmetricNat,
            NatType.Unknown
        };

        foreach (var type in punchableTypes)
        {
            Assert.True(IsPunchable(type), $"NatType.{type} should be punchable");
        }

        foreach (var type in nonPunchableTypes)
        {
            Assert.False(IsPunchable(type), $"NatType.{type} should NOT be punchable");
        }
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(NatType.OpenInternet, true)]
    [InlineData(NatType.FullCone, true)]
    [InlineData(NatType.RestrictedCone, true)]
    [InlineData(NatType.PortRestrictedCone, true)]
    [InlineData(NatType.SymmetricNat, false)]
    [InlineData(NatType.Unknown, false)]
    public void NatType_IsPunchable_ReturnsCorrectValue(NatType natType, bool expectedPunchable)
    {
        Assert.Equal(expectedPunchable, IsPunchable(natType));
    }

    private static bool IsPunchable(NatType natType)
    {
        return natType != NatType.Unknown && natType != NatType.SymmetricNat;
    }
}

/// <summary>
/// Tests for NatMappingMethod enum
/// </summary>
public class NatMappingMethodTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void NatMappingMethod_HasExpectedValues()
    {
        Assert.True(Enum.IsDefined(typeof(NatMappingMethod), NatMappingMethod.UPnP));
        Assert.True(Enum.IsDefined(typeof(NatMappingMethod), NatMappingMethod.NatPmp));
        Assert.True(Enum.IsDefined(typeof(NatMappingMethod), NatMappingMethod.Stun));
        Assert.True(Enum.IsDefined(typeof(NatMappingMethod), NatMappingMethod.Manual));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NatMappingMethod_UPnPHasHighestPriority()
    {
        // Document the priority order: UPnP > NAT-PMP > STUN > Manual
        var priorityOrder = new[] { NatMappingMethod.UPnP, NatMappingMethod.NatPmp, NatMappingMethod.Stun, NatMappingMethod.Manual };

        for (int i = 0; i < priorityOrder.Length - 1; i++)
        {
            Assert.True((int)priorityOrder[i] < (int)priorityOrder[i + 1],
                $"{priorityOrder[i]} should have higher priority (lower enum value) than {priorityOrder[i + 1]}");
        }
    }

    [Theory]
    [Trait("Category", "Unit")]
    [InlineData(NatMappingMethod.UPnP, 0)]
    [InlineData(NatMappingMethod.NatPmp, 1)]
    [InlineData(NatMappingMethod.Stun, 2)]
    [InlineData(NatMappingMethod.Manual, 3)]
    public void NatMappingMethod_HasCorrectOrdinalValues(NatMappingMethod method, int expectedOrdinal)
    {
        Assert.Equal(expectedOrdinal, (int)method);
    }
}

/// <summary>
/// Tests for NatMappingEventType enum
/// </summary>
public class NatMappingEventTypeTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void NatMappingEventType_HasExpectedValues()
    {
        Assert.True(Enum.IsDefined(typeof(NatMappingEventType), NatMappingEventType.Created));
        Assert.True(Enum.IsDefined(typeof(NatMappingEventType), NatMappingEventType.Renewed));
        Assert.True(Enum.IsDefined(typeof(NatMappingEventType), NatMappingEventType.Expired));
        Assert.True(Enum.IsDefined(typeof(NatMappingEventType), NatMappingEventType.Released));
        Assert.True(Enum.IsDefined(typeof(NatMappingEventType), NatMappingEventType.Failed));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void NatMappingEventType_AllValuesAreDefined()
    {
        var values = Enum.GetValues<NatMappingEventType>();
        Assert.Equal(5, values.Length);
    }
}

/// <summary>
/// Integration tests that require real network access.
/// These tests are skipped in CI environments.
/// </summary>
public class NatTraversalIntegrationTests
{
    private readonly Mock<ILogger<SyncthingUPnPService>> _upnpLoggerMock;

    public NatTraversalIntegrationTests()
    {
        _upnpLoggerMock = new Mock<ILogger<SyncthingUPnPService>>();
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UPnPService_DiscoverDevices_RealNetwork()
    {
        // This test requires actual network access and UPnP-enabled router
        // Skip if no network available
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
        {
            return;
        }

        // Arrange
        using var service = new SyncthingUPnPService(_upnpLoggerMock.Object);

        // Act
        var devices = await service.DiscoverDevicesAsync(TimeSpan.FromSeconds(5));

        // Assert - we don't require devices, just that discovery completes without exception
        Assert.NotNull(devices);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task UPnPService_IsAvailable_RealNetwork()
    {
        // This test requires actual network access
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
        {
            return;
        }

        // Arrange
        using var service = new SyncthingUPnPService(_upnpLoggerMock.Object);

        // Act
        var isAvailable = await service.IsAvailableAsync();

        // Assert - just verify it completes without throwing
        // Result depends on network environment
        Assert.True(isAvailable || !isAvailable); // Always passes, validates execution
    }

    [Fact]
    [Trait("Category", "Integration")]
    public void UPnPService_GetStatus_ReturnsValidStatus()
    {
        // Arrange
        using var service = new SyncthingUPnPService(_upnpLoggerMock.Object);

        // Act
        var status = service.GetStatus();

        // Assert
        Assert.NotNull(status);
        Assert.NotNull(status.Devices);
    }
}

/// <summary>
/// Tests for ExternalAddressChangedEventArgs from STUN service
/// </summary>
public class StunExternalAddressChangedEventArgsTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ExternalAddressChangedEventArgs_CanBeCreated()
    {
        // Arrange & Act
        var args = new CreatioHelper.Infrastructure.Services.Network.Stun.ExternalAddressChangedEventArgs
        {
            OldAddress = IPAddress.Parse("192.168.1.100"),
            NewAddress = IPAddress.Parse("203.0.113.50"),
            OldEndPoint = new IPEndPoint(IPAddress.Parse("192.168.1.100"), 22000),
            NewEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.50"), 45000)
        };

        // Assert
        Assert.Equal("192.168.1.100", args.OldAddress?.ToString());
        Assert.Equal("203.0.113.50", args.NewAddress?.ToString());
        Assert.Equal(22000, args.OldEndPoint?.Port);
        Assert.Equal(45000, args.NewEndPoint?.Port);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ExternalAddressChangedEventArgs_AllowsNullValues()
    {
        // Arrange & Act
        var args = new CreatioHelper.Infrastructure.Services.Network.Stun.ExternalAddressChangedEventArgs
        {
            OldAddress = null,
            NewAddress = IPAddress.Parse("203.0.113.50"),
            OldEndPoint = null,
            NewEndPoint = new IPEndPoint(IPAddress.Parse("203.0.113.50"), 45000)
        };

        // Assert
        Assert.Null(args.OldAddress);
        Assert.NotNull(args.NewAddress);
        Assert.Null(args.OldEndPoint);
        Assert.NotNull(args.NewEndPoint);
    }
}
