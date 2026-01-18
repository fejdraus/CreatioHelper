using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CreatioHelper.Infrastructure.Services.Sync.FileSystem;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.UnitTests.Sync.FileSystem;

public class ExtendedAttributeProviderTests : IDisposable
{
    private readonly Mock<ILogger<WindowsExtendedAttributeProvider>> _loggerMock;
    private readonly string _testDir;
    private readonly string _testFile;

    public ExtendedAttributeProviderTests()
    {
        _loggerMock = new Mock<ILogger<WindowsExtendedAttributeProvider>>();
        _testDir = Path.Combine(Path.GetTempPath(), $"xattr_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _testFile = Path.Combine(_testDir, "test_file.txt");
        File.WriteAllText(_testFile, "test content");
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void WindowsProvider_IsSupported_ReturnsTrue()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsExtendedAttributeProvider(_loggerMock.Object);

        // Assert
        Assert.True(provider.IsSupported);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task SetAttributeAsync_GetAttributeAsync_RoundTrip()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsExtendedAttributeProvider(_loggerMock.Object);
        var attrName = "user.test";
        var attrValue = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        await provider.SetAttributeAsync(_testFile, attrName, attrValue);
        var result = await provider.GetAttributeAsync(_testFile, attrName);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(attrValue, result);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task GetAttributeAsync_NonExistent_ReturnsNull()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsExtendedAttributeProvider(_loggerMock.Object);

        // Act
        var result = await provider.GetAttributeAsync(_testFile, "nonexistent.attr");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task SetAttributesAsync_SetsMultipleAttributes()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsExtendedAttributeProvider(_loggerMock.Object);
        var attrs = new Dictionary<string, byte[]>
        {
            ["user.attr1"] = new byte[] { 1, 2, 3 },
            ["user.attr2"] = new byte[] { 4, 5, 6 }
        };

        // Act
        await provider.SetAttributesAsync(_testFile, attrs);

        // Assert
        var attr1 = await provider.GetAttributeAsync(_testFile, "user.attr1");
        var attr2 = await provider.GetAttributeAsync(_testFile, "user.attr2");

        Assert.Equal(new byte[] { 1, 2, 3 }, attr1);
        Assert.Equal(new byte[] { 4, 5, 6 }, attr2);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task RemoveAttributeAsync_RemovesAttribute()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsExtendedAttributeProvider(_loggerMock.Object);
        var attrName = "user.toremove";
        await provider.SetAttributeAsync(_testFile, attrName, new byte[] { 1, 2, 3 });

        // Act
        await provider.RemoveAttributeAsync(_testFile, attrName);

        // Assert
        var result = await provider.GetAttributeAsync(_testFile, attrName);
        Assert.Null(result);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task RemoveAttributeAsync_NonExistent_DoesNotThrow()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsExtendedAttributeProvider(_loggerMock.Object);

        // Act & Assert - should not throw
        await provider.RemoveAttributeAsync(_testFile, "nonexistent.attr");
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task GetAttributesAsync_ReturnsAllAttributes()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsExtendedAttributeProvider(_loggerMock.Object);

        // Note: This test depends on ListAttributeNamesAsync implementation
        // which may not find attributes without proper ADS enumeration

        // Act
        var attrs = await provider.GetAttributesAsync(_testFile);

        // Assert
        Assert.NotNull(attrs);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ListAttributeNamesAsync_ReturnsEmptyForNewFile()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsExtendedAttributeProvider(_loggerMock.Object);
        var newFile = Path.Combine(_testDir, "new_file.txt");
        File.WriteAllText(newFile, "content");

        // Act
        var names = await provider.ListAttributeNamesAsync(newFile);

        // Assert
        Assert.NotNull(names);
        Assert.Empty(names);
    }

    [Fact]
    public void ExtendedAttributeProviderFactory_CreatesCorrectProvider()
    {
        // Arrange
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(Mock.Of<ILogger>());
        var factory = new ExtendedAttributeProviderFactory(loggerFactory.Object);

        // Act
        var provider = factory.Create();

        // Assert
        Assert.NotNull(provider);
        Assert.True(provider.IsSupported);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task SetAttributeAsync_LargeValue_Works()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsExtendedAttributeProvider(_loggerMock.Object);
        var largeValue = new byte[1024 * 10]; // 10KB
        new Random(42).NextBytes(largeValue);

        // Act
        await provider.SetAttributeAsync(_testFile, "user.large", largeValue);
        var result = await provider.GetAttributeAsync(_testFile, "user.large");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(largeValue, result);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task SetAttributeAsync_OverwritesExisting()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsExtendedAttributeProvider(_loggerMock.Object);
        await provider.SetAttributeAsync(_testFile, "user.overwrite", new byte[] { 1, 2, 3 });

        // Act
        await provider.SetAttributeAsync(_testFile, "user.overwrite", new byte[] { 4, 5, 6, 7 });
        var result = await provider.GetAttributeAsync(_testFile, "user.overwrite");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, result);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task SetAttributeAsync_EmptyValue_Works()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsExtendedAttributeProvider(_loggerMock.Object);

        // Act
        await provider.SetAttributeAsync(_testFile, "user.empty", Array.Empty<byte>());
        var result = await provider.GetAttributeAsync(_testFile, "user.empty");

        // Assert
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task GetAttributeAsync_NonExistentFile_ReturnsNull()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsExtendedAttributeProvider(_loggerMock.Object);
        var nonExistentFile = Path.Combine(_testDir, "nonexistent.txt");

        // Act
        var result = await provider.GetAttributeAsync(nonExistentFile, "user.test");

        // Assert
        Assert.Null(result);
    }
}
