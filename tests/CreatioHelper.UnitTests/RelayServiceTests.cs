using CreatioHelper.Infrastructure.Services.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net.Sockets;

namespace CreatioHelper.UnitTests;

public class RelayServiceTests : IDisposable
{
    private readonly Mock<ILogger<RelayService>> _mockLogger;
    private readonly Mock<IConfiguration> _mockConfiguration;
    private readonly HttpClient _httpClient;
    private readonly string _originalDeviceId;
    private readonly string _originalPort;

    public RelayServiceTests()
    {
        _mockLogger = new Mock<ILogger<RelayService>>();
        _mockConfiguration = new Mock<IConfiguration>();
        _httpClient = new HttpClient();
        
        // Store original environment variables
        _originalDeviceId = Environment.GetEnvironmentVariable("Sync__DeviceId") ?? "";
        _originalPort = Environment.GetEnvironmentVariable("Sync__Port") ?? "";
        
        // Set test environment variables
        Environment.SetEnvironmentVariable("Sync__DeviceId", "TEST-DEVICE-1");
        Environment.SetEnvironmentVariable("Sync__Port", "22000");
        
        // Setup configuration mock - avoid extension method in mock setup
        var mockSection = new Mock<IConfigurationSection>();
        mockSection.Setup(x => x.Value).Returns((string?)null);
        mockSection.Setup(x => x.GetChildren()).Returns(new List<IConfigurationSection>());
        _mockConfiguration.Setup(x => x.GetSection("Sync:RelayServers")).Returns(mockSection.Object);
    }

    public void Dispose()
    {
        // Restore original environment variables
        Environment.SetEnvironmentVariable("Sync__DeviceId", _originalDeviceId);
        Environment.SetEnvironmentVariable("Sync__Port", _originalPort);
        _httpClient.Dispose();
    }

    [Fact]
    public void Constructor_WithValidDeviceId_ShouldInitialize()
    {
        // Act
        var service = new RelayService(_mockLogger.Object, _mockConfiguration.Object, _httpClient);

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
            new RelayService(_mockLogger.Object, _mockConfiguration.Object, _httpClient));
        
        Assert.Contains("Sync__DeviceId environment variable is required", exception.Message);
        
        // Restore for cleanup
        Environment.SetEnvironmentVariable("Sync__DeviceId", "TEST-DEVICE-1");
    }

    [Fact]
    public async Task ConnectToDeviceAsync_WithNonExistentDevice_ShouldFallbackToRelay()
    {
        // Arrange
        var service = new RelayService(_mockLogger.Object, _mockConfiguration.Object, _httpClient);
        var targetDevice = "NON-EXISTENT-DEVICE";

        // Act
        var result = await service.ConnectToDeviceAsync(targetDevice);

        // Assert - Should fallback to relay and succeed (in this simplified implementation)
        Assert.True(result);
    }

    [Fact]
    public async Task ConnectToDeviceAsync_WithExistingConnection_ShouldReturnTrue()
    {
        // Arrange
        var service = new RelayService(_mockLogger.Object, _mockConfiguration.Object, _httpClient);
        var targetDevice = "TARGET-DEVICE";
        
        // First connection attempt (simulated success through relay)
        await service.ConnectToDeviceAsync(targetDevice);

        // Act - Second connection attempt
        var result = await service.ConnectToDeviceAsync(targetDevice);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task SendDataAsync_WithoutConnection_ShouldReturnFalse()
    {
        // Arrange
        var service = new RelayService(_mockLogger.Object, _mockConfiguration.Object, _httpClient);
        var targetDevice = "NON-CONNECTED-DEVICE";
        var testData = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        var result = await service.SendDataAsync(targetDevice, testData);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task SendDataAsync_WithActiveConnection_ShouldReturnTrue()
    {
        // Arrange
        var service = new RelayService(_mockLogger.Object, _mockConfiguration.Object, _httpClient);
        var targetDevice = "TARGET-DEVICE";
        var testData = new byte[] { 1, 2, 3, 4, 5 };
        
        // Establish connection first (through relay since direct will fail)
        await service.ConnectToDeviceAsync(targetDevice);

        // Act
        var result = await service.SendDataAsync(targetDevice, testData);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsConnectedToDevice_WithoutConnection_ShouldReturnFalse()
    {
        // Arrange
        var service = new RelayService(_mockLogger.Object, _mockConfiguration.Object, _httpClient);
        var targetDevice = "NON-CONNECTED-DEVICE";

        // Act
        var result = service.IsConnectedToDevice(targetDevice);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task IsConnectedToDevice_WithActiveConnection_ShouldReturnTrue()
    {
        // Arrange
        var service = new RelayService(_mockLogger.Object, _mockConfiguration.Object, _httpClient);
        var targetDevice = "TARGET-DEVICE";
        
        // Establish connection
        await service.ConnectToDeviceAsync(targetDevice);

        // Act
        var result = service.IsConnectedToDevice(targetDevice);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task DisconnectFromDeviceAsync_WithActiveConnection_ShouldDisconnect()
    {
        // Arrange
        var service = new RelayService(_mockLogger.Object, _mockConfiguration.Object, _httpClient);
        var targetDevice = "TARGET-DEVICE";
        
        // Establish connection
        await service.ConnectToDeviceAsync(targetDevice);
        Assert.True(service.IsConnectedToDevice(targetDevice));

        // Act
        await service.DisconnectFromDeviceAsync(targetDevice);

        // Assert
        Assert.False(service.IsConnectedToDevice(targetDevice));
    }

    [Fact]
    public async Task DisconnectFromDeviceAsync_WithoutConnection_ShouldNotThrow()
    {
        // Arrange
        var service = new RelayService(_mockLogger.Object, _mockConfiguration.Object, _httpClient);
        var targetDevice = "NON-CONNECTED-DEVICE";

        // Act & Assert - Should not throw
        await service.DisconnectFromDeviceAsync(targetDevice);
    }

    [Fact]
    public async Task MultipleConnections_ShouldBeTrackedSeparately()
    {
        // Arrange
        var service = new RelayService(_mockLogger.Object, _mockConfiguration.Object, _httpClient);
        var device1 = "DEVICE-1";
        var device2 = "DEVICE-2";

        // Act
        await service.ConnectToDeviceAsync(device1);
        await service.ConnectToDeviceAsync(device2);

        // Assert
        Assert.True(service.IsConnectedToDevice(device1));
        Assert.True(service.IsConnectedToDevice(device2));
        
        // Disconnect one
        await service.DisconnectFromDeviceAsync(device1);
        
        Assert.False(service.IsConnectedToDevice(device1));
        Assert.True(service.IsConnectedToDevice(device2));
    }

    [Fact]
    public async Task ConnectToDeviceAsync_WithInvalidTcpAddress_ShouldFallbackToRelay()
    {
        // Arrange
        var service = new RelayService(_mockLogger.Object, _mockConfiguration.Object, _httpClient);
        var targetDevice = "TARGET-DEVICE";
        var invalidTcpAddress = "tcp://999.999.999.999:22000"; // Invalid IP

        // Act
        var result = await service.ConnectToDeviceAsync(targetDevice, invalidTcpAddress);

        // Assert - Should fallback to relay and succeed
        Assert.True(result);
        Assert.True(service.IsConnectedToDevice(targetDevice));
    }

    [Fact]
    public async Task ConnectToDeviceAsync_WithMalformedAddress_ShouldFallbackToRelay()
    {
        // Arrange
        var service = new RelayService(_mockLogger.Object, _mockConfiguration.Object, _httpClient);
        var targetDevice = "TARGET-DEVICE";
        var malformedAddress = "not-a-valid-address";

        // Act
        var result = await service.ConnectToDeviceAsync(targetDevice, malformedAddress);

        // Assert - Should fallback to relay and succeed
        Assert.True(result);
        Assert.True(service.IsConnectedToDevice(targetDevice));
    }
}

// DirectConnection tests removed due to TcpClient.Connected being non-mockable
// These would require integration tests with real TCP connections

public class RelayConnectionTests
{
    [Fact]
    public async Task SendDataAsync_WithValidRelay_ShouldReturnTrue()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        var connection = new RelayConnectionImpl("wss://relay.test.com", "TARGET-DEVICE", "SOURCE-DEVICE", mockLogger.Object);
        var testData = new byte[] { 1, 2, 3 };

        // Act
        var result = await connection.SendDataAsync(testData);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsConnected_NewRelayConnection_ShouldReturnTrue()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        var connection = new RelayConnectionImpl("wss://relay.test.com", "TARGET-DEVICE", "SOURCE-DEVICE", mockLogger.Object);

        // Act & Assert
        Assert.True(connection.IsConnected);
    }

    [Fact]
    public async Task DisconnectAsync_ShouldSetConnectedToFalse()
    {
        // Arrange
        var mockLogger = new Mock<ILogger>();
        var connection = new RelayConnectionImpl("wss://relay.test.com", "TARGET-DEVICE", "SOURCE-DEVICE", mockLogger.Object);
        
        Assert.True(connection.IsConnected);

        // Act
        await connection.DisconnectAsync();

        // Assert
        Assert.False(connection.IsConnected);
    }
}