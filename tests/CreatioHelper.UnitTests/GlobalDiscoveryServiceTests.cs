using System.Net;
using System.Text.Json;
using CreatioHelper.Infrastructure.Services.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;

namespace CreatioHelper.UnitTests;

public class GlobalDiscoveryServiceTests : IDisposable
{
    private readonly Mock<ILogger<GlobalDiscoveryService>> _mockLogger;
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly Mock<HttpMessageHandler> _mockHttpHandler;
    private readonly HttpClient _httpClient;
    private readonly string _originalDeviceId;

    public GlobalDiscoveryServiceTests()
    {
        _mockLogger = new Mock<ILogger<GlobalDiscoveryService>>();
        _mockConfiguration = new Mock<IConfiguration>();
        _mockHttpHandler = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_mockHttpHandler.Object);
        
        // Store original environment variable
        _originalDeviceId = Environment.GetEnvironmentVariable("Sync__DeviceId") ?? "";
        
        // Set test device ID
        Environment.SetEnvironmentVariable("Sync__DeviceId", "TEST-DEVICE-1");
        
        // Setup configuration mock - avoid extension method in mock setup
        var mockSection = new Mock<IConfigurationSection>();
        mockSection.Setup(x => x.Value).Returns((string?)null);
        mockSection.Setup(x => x.GetChildren()).Returns(new List<IConfigurationSection>());
        _mockConfiguration.Setup(x => x.GetSection("Sync:DiscoveryServers")).Returns(mockSection.Object);
    }

    public void Dispose()
    {
        // Restore original environment variable
        Environment.SetEnvironmentVariable("Sync__DeviceId", _originalDeviceId);
        _httpClient.Dispose();
    }

    [Fact]
    public void Constructor_WithValidDeviceId_ShouldInitialize()
    {
        // Act
        var service = new GlobalDiscoveryService(_mockLogger.Object, _mockConfiguration.Object, _httpClient);

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public void Constructor_WithoutDeviceId_ShouldThrowException()
    {
        // Arrange
        Environment.SetEnvironmentVariable("Sync__DeviceId", null);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new GlobalDiscoveryService(_mockLogger.Object, _mockConfiguration.Object, _httpClient));
        
        Assert.Contains("Sync__DeviceId environment variable is required", exception.Message);
        
        // Restore for cleanup
        Environment.SetEnvironmentVariable("Sync__DeviceId", "TEST-DEVICE-1");
    }

    [Fact]
    public async Task DiscoverDeviceAsync_WithValidResponse_ShouldReturnAddresses()
    {
        // Arrange
        var targetDeviceId = "TARGET-DEVICE";
        var expectedAddresses = new List<string> { "tcp://192.168.1.100:22000", "tcp://10.0.0.50:22000" };
        
        var discoveryResponse = new DiscoveryResponse
        {
            Addresses = expectedAddresses,
            LastSeen = DateTimeOffset.UtcNow
        };
        
        var responseJson = JsonSerializer.Serialize(discoveryResponse);
        
        _mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseJson)
            });

        var service = new GlobalDiscoveryService(_mockLogger.Object, _mockConfiguration.Object, _httpClient);

        // Act
        var result = await service.DiscoverDeviceAsync(targetDeviceId);

        // Assert
        Assert.Equal(expectedAddresses.Count, result.Count);
        Assert.All(expectedAddresses, address => Assert.Contains(address, result));
    }

    [Fact]
    public async Task DiscoverDeviceAsync_WithHttpError_ShouldReturnEmptyList()
    {
        // Arrange
        var targetDeviceId = "TARGET-DEVICE";
        
        _mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.InternalServerError
            });

        var service = new GlobalDiscoveryService(_mockLogger.Object, _mockConfiguration.Object, _httpClient);

        // Act
        var result = await service.DiscoverDeviceAsync(targetDeviceId);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task DiscoverDeviceAsync_WithMultipleServers_ShouldCombineResults()
    {
        // Arrange
        var targetDeviceId = "TARGET-DEVICE";
        var addresses1 = new List<string> { "tcp://192.168.1.100:22000" };
        var addresses2 = new List<string> { "tcp://10.0.0.50:22000" };
        
        var callCount = 0;
        _mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() =>
            {
                callCount++;
                var addresses = callCount == 1 ? addresses1 : addresses2;
                var response = new DiscoveryResponse { Addresses = addresses };
                var json = JsonSerializer.Serialize(response);
                
                return new HttpResponseMessage
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(json)
                };
            });

        var service = new GlobalDiscoveryService(_mockLogger.Object, _mockConfiguration.Object, _httpClient);

        // Act
        var result = await service.DiscoverDeviceAsync(targetDeviceId);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains("tcp://192.168.1.100:22000", result);
        Assert.Contains("tcp://10.0.0.50:22000", result);
    }

    [Fact]
    public async Task DiscoverDeviceAsync_WithDuplicateAddresses_ShouldReturnUniqueAddresses()
    {
        // Arrange
        var targetDeviceId = "TARGET-DEVICE";
        var duplicateAddress = "tcp://192.168.1.100:22000";
        var addresses = new List<string> { duplicateAddress, duplicateAddress, "tcp://10.0.0.50:22000" };
        
        var discoveryResponse = new DiscoveryResponse { Addresses = addresses };
        var responseJson = JsonSerializer.Serialize(discoveryResponse);
        
        _mockHttpHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(responseJson)
            });

        var service = new GlobalDiscoveryService(_mockLogger.Object, _mockConfiguration.Object, _httpClient);

        // Act
        var result = await service.DiscoverDeviceAsync(targetDeviceId);

        // Assert
        Assert.Equal(2, result.Count); // Should remove duplicate
        Assert.Contains(duplicateAddress, result);
        Assert.Contains("tcp://10.0.0.50:22000", result);
    }


    [Fact]
    public void DiscoveryResponse_ShouldSerializeCorrectly()
    {
        // Arrange
        var response = new DiscoveryResponse
        {
            Addresses = new List<string> { "tcp://192.168.1.100:22000" },
            LastSeen = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero)
        };

        // Act
        var json = JsonSerializer.Serialize(response);
        var deserialized = JsonSerializer.Deserialize<DiscoveryResponse>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Single(deserialized.Addresses);
        Assert.Equal("tcp://192.168.1.100:22000", deserialized.Addresses[0]);
        Assert.Equal(response.LastSeen, deserialized.LastSeen);
    }

    [Fact]
    public void DeviceAnnouncement_ShouldSerializeCorrectly()
    {
        // Arrange
        var announcement = new DeviceAnnouncement
        {
            DeviceId = "TEST-DEVICE-1",
            Addresses = new List<string> { "tcp://127.0.0.1:22000" },
            Timestamp = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero)
        };

        // Act
        var json = JsonSerializer.Serialize(announcement);
        var deserialized = JsonSerializer.Deserialize<DeviceAnnouncement>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.Equal("TEST-DEVICE-1", deserialized.DeviceId);
        Assert.Single(deserialized.Addresses);
        Assert.Equal("tcp://127.0.0.1:22000", deserialized.Addresses[0]);
        Assert.Equal(announcement.Timestamp, deserialized.Timestamp);
    }
}