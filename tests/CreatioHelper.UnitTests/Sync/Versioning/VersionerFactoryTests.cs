using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync.Versioning;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.UnitTests.Sync.Versioning;

public class VersionerFactoryTests : IDisposable
{
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;
    private readonly string _testDir;

    public VersionerFactoryTests()
    {
        _loggerFactoryMock = new Mock<ILoggerFactory>();
        _loggerFactoryMock.Setup(f => f.CreateLogger(It.IsAny<string>()))
            .Returns(new Mock<ILogger>().Object);

        _testDir = Path.Combine(Path.GetTempPath(), $"versioner_factory_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    #region CreateVersioner Tests

    [Fact]
    public void CreateVersioner_CreatesSimpleVersioner()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = VersioningConfiguration.Simple(keep: 10, cleanoutDays: 7);

        // Act
        using var versioner = factory.CreateVersioner(_testDir, config);

        // Assert
        Assert.NotNull(versioner);
        Assert.Equal("simple", versioner.VersionerType);
        Assert.IsType<SimpleVersioner>(versioner);
    }

    [Fact]
    public void CreateVersioner_CreatesStaggeredVersioner()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = VersioningConfiguration.Staggered(maxAgeSeconds: 30 * 24 * 3600);

        // Act
        using var versioner = factory.CreateVersioner(_testDir, config);

        // Assert
        Assert.NotNull(versioner);
        Assert.Equal("staggered", versioner.VersionerType);
        Assert.IsType<StaggeredVersioner>(versioner);
    }

    [Fact]
    public void CreateVersioner_CreatesTrashcanVersioner()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = VersioningConfiguration.Trashcan(cleanoutDays: 14);

        // Act
        using var versioner = factory.CreateVersioner(_testDir, config);

        // Assert
        Assert.NotNull(versioner);
        Assert.Equal("trashcan", versioner.VersionerType);
        Assert.IsType<TrashcanVersioner>(versioner);
    }

    [Fact]
    public void CreateVersioner_CreatesExternalVersioner()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = new VersioningConfiguration
        {
            Type = "external",
            Params = new Dictionary<string, string> { ["command"] = "echo" }
        };

        // Act
        using var versioner = factory.CreateVersioner(_testDir, config);

        // Assert
        Assert.NotNull(versioner);
        Assert.Equal("external", versioner.VersionerType);
        Assert.IsType<ExternalVersioner>(versioner);
    }

    [Fact]
    public void CreateVersioner_ThrowsArgumentException_WhenVersioningDisabled()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = VersioningConfiguration.Disabled;

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            factory.CreateVersioner(_testDir, config));
    }

    [Fact]
    public void CreateVersioner_ThrowsArgumentException_WhenConfigIsNull()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            factory.CreateVersioner(_testDir, null!));
    }

    [Fact]
    public void CreateVersioner_ThrowsArgumentException_WhenTypeNotSupported()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = new VersioningConfiguration { Type = "unknown" };

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            factory.CreateVersioner(_testDir, config));
    }

    [Fact]
    public void CreateVersioner_ThrowsArgumentException_WhenExternalMissingCommand()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = new VersioningConfiguration
        {
            Type = "external",
            Params = new Dictionary<string, string>() // Missing "command"
        };

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            factory.CreateVersioner(_testDir, config));
    }

    [Fact]
    public void CreateVersioner_UsesCustomFSPath_WhenProvided()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var customPath = Path.Combine(_testDir, "custom_versions");
        var config = new VersioningConfiguration
        {
            Type = "simple",
            Params = new Dictionary<string, string> { ["keep"] = "5" },
            FSPath = customPath
        };

        // Act
        using var versioner = factory.CreateVersioner(_testDir, config);

        // Assert
        Assert.Equal(customPath, versioner.VersionsPath);
    }

    [Fact]
    public void CreateVersioner_UsesRelativeFSPath()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = new VersioningConfiguration
        {
            Type = "simple",
            Params = new Dictionary<string, string> { ["keep"] = "5" },
            FSPath = "relative_versions"
        };

        // Act
        using var versioner = factory.CreateVersioner(_testDir, config);

        // Assert
        var expectedPath = Path.Combine(_testDir, "relative_versions");
        Assert.Equal(expectedPath, versioner.VersionsPath);
    }

    [Fact]
    public void CreateVersioner_UsesCustomCleanupInterval()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = new VersioningConfiguration
        {
            Type = "simple",
            Params = new Dictionary<string, string> { ["keep"] = "5" },
            CleanupIntervalS = 7200 // 2 hours
        };

        // Act
        using var versioner = factory.CreateVersioner(_testDir, config);

        // Assert - no exception, versioner created with custom interval
        Assert.NotNull(versioner);
    }

    [Theory]
    [InlineData("simple")]
    [InlineData("SIMPLE")]
    [InlineData("Simple")]
    public void CreateVersioner_IsCaseInsensitive(string type)
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = new VersioningConfiguration
        {
            Type = type,
            Params = new Dictionary<string, string> { ["keep"] = "5" }
        };

        // Act
        using var versioner = factory.CreateVersioner(_testDir, config);

        // Assert
        Assert.Equal("simple", versioner.VersionerType);
    }

    #endregion

    #region ValidateConfiguration Tests

    [Fact]
    public void ValidateConfiguration_ReturnsTrue_ForDisabledConfig()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = VersioningConfiguration.Disabled;

        // Act
        var isValid = factory.ValidateConfiguration(config);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void ValidateConfiguration_ReturnsTrue_ForNullConfig()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);

        // Act
        var isValid = factory.ValidateConfiguration(null!);

        // Assert
        Assert.True(isValid); // Null/disabled is valid
    }

    [Fact]
    public void ValidateConfiguration_ReturnsTrue_ForValidSimpleConfig()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = VersioningConfiguration.Simple(keep: 5, cleanoutDays: 7);

        // Act
        var isValid = factory.ValidateConfiguration(config);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void ValidateConfiguration_ReturnsFalse_ForInvalidSimpleConfig()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = new VersioningConfiguration
        {
            Type = "simple",
            Params = new Dictionary<string, string>
            {
                ["keep"] = "0", // Invalid: must be >= 1
                ["cleanoutDays"] = "7"
            }
        };

        // Act
        var isValid = factory.ValidateConfiguration(config);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void ValidateConfiguration_ReturnsTrue_ForValidStaggeredConfig()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = VersioningConfiguration.Staggered(maxAgeSeconds: 86400);

        // Act
        var isValid = factory.ValidateConfiguration(config);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void ValidateConfiguration_ReturnsTrue_ForValidTrashcanConfig()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = VersioningConfiguration.Trashcan(cleanoutDays: 30);

        // Act
        var isValid = factory.ValidateConfiguration(config);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void ValidateConfiguration_ReturnsTrue_ForValidExternalConfig()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = new VersioningConfiguration
        {
            Type = "external",
            Params = new Dictionary<string, string> { ["command"] = "/path/to/script" }
        };

        // Act
        var isValid = factory.ValidateConfiguration(config);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void ValidateConfiguration_ReturnsFalse_ForExternalWithoutCommand()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = new VersioningConfiguration
        {
            Type = "external",
            Params = new Dictionary<string, string>() // Missing command
        };

        // Act
        var isValid = factory.ValidateConfiguration(config);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void ValidateConfiguration_ReturnsFalse_ForUnsupportedType()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = new VersioningConfiguration { Type = "unsupported" };

        // Act
        var isValid = factory.ValidateConfiguration(config);

        // Assert
        Assert.False(isValid);
    }

    #endregion

    #region GetSupportedTypes Tests

    [Fact]
    public void GetSupportedTypes_ReturnsAllSupportedTypes()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);

        // Act
        var types = factory.GetSupportedTypes().ToList();

        // Assert
        Assert.Contains("simple", types);
        Assert.Contains("staggered", types);
        Assert.Contains("trashcan", types);
        Assert.Contains("external", types);
        Assert.Equal(4, types.Count);
    }

    #endregion

    #region Parameter Defaults Tests

    [Fact]
    public void CreateSimpleVersioner_UsesDefaultKeep_WhenNotSpecified()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = new VersioningConfiguration
        {
            Type = "simple",
            Params = new Dictionary<string, string>() // No "keep" specified
        };

        // Act
        using var versioner = factory.CreateVersioner(_testDir, config);

        // Assert - should not throw, uses default keep=5
        Assert.NotNull(versioner);
    }

    [Fact]
    public void CreateStaggeredVersioner_UsesDefaultMaxAge_WhenNotSpecified()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = new VersioningConfiguration
        {
            Type = "staggered",
            Params = new Dictionary<string, string>() // No "maxAge" specified
        };

        // Act
        using var versioner = factory.CreateVersioner(_testDir, config);

        // Assert - should not throw, uses default maxAge=1 year
        Assert.NotNull(versioner);
    }

    [Fact]
    public void CreateTrashcanVersioner_UsesDefaultCleanoutDays_WhenNotSpecified()
    {
        // Arrange
        var factory = new VersionerFactory(_loggerFactoryMock.Object);
        var config = new VersioningConfiguration
        {
            Type = "trashcan",
            Params = new Dictionary<string, string>() // No "cleanoutDays" specified
        };

        // Act
        using var versioner = factory.CreateVersioner(_testDir, config);

        // Assert - should not throw, uses default cleanoutDays=0
        Assert.NotNull(versioner);
    }

    #endregion
}
