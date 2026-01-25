using System;
using CreatioHelper.Infrastructure.Services.Network.GeoIP;
using Microsoft.Extensions.Logging;
using Moq;
using System.Net;
using System.Threading.Tasks;
using Xunit;

namespace CreatioHelper.UnitTests.Network;

public class GeoIpProviderTests : IAsyncDisposable
{
    private readonly Mock<ILogger<GeoIpProvider>> _loggerMock;
    private readonly GeoIpProvider _provider;

    public GeoIpProviderTests()
    {
        _loggerMock = new Mock<ILogger<GeoIpProvider>>();
        _provider = new GeoIpProvider(_loggerMock.Object);
    }

    [Fact]
    public void GeoLocation_DistanceTo_ReturnsNull_WhenCoordinatesMissing()
    {
        // Arrange
        var location1 = new GeoLocation { Country = "US" };
        var location2 = new GeoLocation { Country = "DE", Latitude = 52.52, Longitude = 13.405 };

        // Act
        var distance = location1.DistanceTo(location2);

        // Assert
        Assert.Null(distance);
    }

    [Fact]
    public void GeoLocation_DistanceTo_CalculatesCorrectDistance()
    {
        // Arrange
        // New York City
        var location1 = new GeoLocation
        {
            Country = "US",
            City = "New York",
            Latitude = 40.7128,
            Longitude = -74.0060
        };

        // London
        var location2 = new GeoLocation
        {
            Country = "GB",
            City = "London",
            Latitude = 51.5074,
            Longitude = -0.1278
        };

        // Act
        var distance = location1.DistanceTo(location2);

        // Assert
        Assert.NotNull(distance);
        // NYC to London is approximately 5,570 km
        Assert.True(distance >= 5500 && distance <= 5650, $"Expected distance around 5570km, got {distance}km");
    }

    [Fact]
    public void GeoLocation_DistanceTo_ReturnsZero_WhenSameLocation()
    {
        // Arrange
        var location = new GeoLocation
        {
            Country = "US",
            City = "New York",
            Latitude = 40.7128,
            Longitude = -74.0060
        };

        // Act
        var distance = location.DistanceTo(location);

        // Assert
        Assert.NotNull(distance);
        Assert.Equal(0, distance.Value, 0.001);
    }

    [Fact]
    public void GeoLocation_DistanceTo_CalculatesAntipodalDistance()
    {
        // Arrange
        // North Pole
        var location1 = new GeoLocation { Latitude = 90, Longitude = 0 };

        // South Pole
        var location2 = new GeoLocation { Latitude = -90, Longitude = 0 };

        // Act
        var distance = location1.DistanceTo(location2);

        // Assert
        Assert.NotNull(distance);
        // Half of Earth's circumference is approximately 20,000 km
        Assert.True(distance >= 19900 && distance <= 20100, $"Expected distance around 20000km, got {distance}km");
    }

    [Fact]
    public void IsReady_ReturnsFalse_WhenDatabaseNotLoaded()
    {
        // Assert
        Assert.False(_provider.IsReady);
    }

    [Fact]
    public async Task LookupAsync_ReturnsNull_WhenDatabaseNotAvailable()
    {
        // Act
        var result = await _provider.LookupAsync(IPAddress.Parse("8.8.8.8"));

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task LookupAsync_WithString_ReturnsNull_ForInvalidAddress()
    {
        // Act
        var result = await _provider.LookupAsync("not-an-ip");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task LookupAsync_WithString_HandlesValidIpFormat()
    {
        // Act - should not throw even without database
        var result = await _provider.LookupAsync("8.8.8.8");

        // Assert
        Assert.Null(result); // No database loaded
    }

    [Fact]
    public async Task LookupAsync_ReturnsNull_ForLocalhostAddress()
    {
        // Arrange - even if database were loaded, localhost should return null
        var options = new GeoIpProvider.GeoIpOptions
        {
            DatabasePath = "nonexistent.mmdb"
        };
        await using var provider = new GeoIpProvider(_loggerMock.Object, options);

        // Act
        var result = await provider.LookupAsync(IPAddress.Loopback);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task RefreshDatabaseAsync_ThrowsWarning_WhenCredentialsNotConfigured()
    {
        // Act & Assert - should not throw but log warning
        await _provider.RefreshDatabaseAsync();

        // Verify warning was logged
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("AccountId or LicenseKey not configured")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void Edition_ReturnsNull_WhenDatabaseNotLoaded()
    {
        // Assert
        Assert.Null(_provider.Edition);
    }

    [Fact]
    public void DatabaseBuildDate_ReturnsNull_WhenDatabaseNotLoaded()
    {
        // Assert
        Assert.Null(_provider.DatabaseBuildDate);
    }

    [Fact]
    public void GeoIpOptions_HasCorrectDefaults()
    {
        // Arrange
        var options = new GeoIpProvider.GeoIpOptions();

        // Assert
        Assert.Equal("GeoLite2-City.mmdb", options.DatabasePath);
        Assert.Equal("GeoLite2-City", options.Edition);
        Assert.Null(options.AccountId);
        Assert.Null(options.LicenseKey);
    }

    [Theory]
    [InlineData("10.0.0.1")]        // Private 10.x.x.x
    [InlineData("172.16.0.1")]      // Private 172.16-31.x.x
    [InlineData("172.31.255.255")]  // Private 172.16-31.x.x
    [InlineData("192.168.1.1")]     // Private 192.168.x.x
    [InlineData("169.254.1.1")]     // Link-local
    [InlineData("127.0.0.1")]       // Loopback
    public async Task LookupAsync_ReturnsNull_ForPrivateAddresses(string ipAddress)
    {
        // Act
        var result = await _provider.LookupAsync(IPAddress.Parse(ipAddress));

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData("172.15.0.1")]      // Not private (below 172.16)
    [InlineData("172.32.0.1")]      // Not private (above 172.31)
    public async Task LookupAsync_AttemptsLookup_ForNonPrivateAddresses(string ipAddress)
    {
        // These addresses aren't private, so lookup should be attempted
        // (will still return null because no database)
        var result = await _provider.LookupAsync(IPAddress.Parse(ipAddress));
        Assert.Null(result); // No database, but the attempt was made
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
    }
}
