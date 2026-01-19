using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CreatioHelper.Infrastructure.Services.Sync.FileSystem;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.UnitTests.Sync.FileSystem;

[SupportedOSPlatform("windows")]
public class OwnershipProviderTests : IDisposable
{
    private readonly Mock<ILogger<WindowsOwnershipProvider>> _loggerMock;
    private readonly string _testDir;
    private readonly string _testFile;

    public OwnershipProviderTests()
    {
        _loggerMock = new Mock<ILogger<WindowsOwnershipProvider>>();
        _testDir = Path.Combine(Path.GetTempPath(), $"ownership_tests_{Guid.NewGuid():N}");
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
        var provider = new WindowsOwnershipProvider(_loggerMock.Object);

        // Assert
        Assert.True(provider.IsSupported);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task GetOwnershipAsync_File_ReturnsOwnership()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsOwnershipProvider(_loggerMock.Object);

        // Act
        var ownership = await provider.GetOwnershipAsync(_testFile);

        // Assert
        Assert.NotNull(ownership);
        Assert.NotEmpty(ownership.OwnerId);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task GetOwnershipAsync_Directory_ReturnsOwnership()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsOwnershipProvider(_loggerMock.Object);

        // Act
        var ownership = await provider.GetOwnershipAsync(_testDir);

        // Assert
        Assert.NotNull(ownership);
        Assert.NotEmpty(ownership.OwnerId);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task GetOwnershipAsync_NonExistent_ReturnsNull()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsOwnershipProvider(_loggerMock.Object);
        var nonExistentPath = Path.Combine(_testDir, "nonexistent.txt");

        // Act
        var ownership = await provider.GetOwnershipAsync(nonExistentPath);

        // Assert
        Assert.Null(ownership);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task GetOwnershipAsync_IncludesOwnerName()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsOwnershipProvider(_loggerMock.Object);

        // Act
        var ownership = await provider.GetOwnershipAsync(_testFile);

        // Assert
        Assert.NotNull(ownership);
        Assert.NotNull(ownership.OwnerName);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task GetOwnershipAsync_IncludesUnixMode()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsOwnershipProvider(_loggerMock.Object);

        // Act
        var ownership = await provider.GetOwnershipAsync(_testFile);

        // Assert
        Assert.NotNull(ownership);
        Assert.True(ownership.UnixMode > 0);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task CopyOwnershipAsync_CopiesOwnership()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsOwnershipProvider(_loggerMock.Object);
        var destFile = Path.Combine(_testDir, "dest_file.txt");
        File.WriteAllText(destFile, "dest content");

        var sourceOwnership = await provider.GetOwnershipAsync(_testFile);

        // Act
        var result = await provider.CopyOwnershipAsync(_testFile, destFile);

        // Assert
        Assert.True(result);
        var destOwnership = await provider.GetOwnershipAsync(destFile);
        Assert.NotNull(destOwnership);
        Assert.Equal(sourceOwnership?.OwnerId, destOwnership.OwnerId);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task CopyOwnershipAsync_SourceNonExistent_ReturnsFalse()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsOwnershipProvider(_loggerMock.Object);
        var nonExistent = Path.Combine(_testDir, "nonexistent.txt");

        // Act
        var result = await provider.CopyOwnershipAsync(nonExistent, _testFile);

        // Assert
        Assert.False(result);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task GetParentOwnershipAsync_ReturnsParentOwnership()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsOwnershipProvider(_loggerMock.Object);

        // Act
        var ownership = await provider.GetParentOwnershipAsync(_testFile);

        // Assert
        Assert.NotNull(ownership);
        Assert.NotEmpty(ownership.OwnerId);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task GetParentOwnershipAsync_RootPath_ReturnsNull()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsOwnershipProvider(_loggerMock.Object);

        // Act - C:\ has no parent
        var ownership = await provider.GetParentOwnershipAsync("C:\\");

        // Assert
        Assert.Null(ownership);
    }

    [Fact]
    public void FileOwnership_DefaultValues()
    {
        // Arrange
        var ownership = new FileOwnership();

        // Assert
        Assert.Equal(string.Empty, ownership.OwnerId);
        Assert.Equal(string.Empty, ownership.GroupId);
        Assert.Null(ownership.OwnerName);
        Assert.Null(ownership.GroupName);
        Assert.Equal(0, ownership.UnixMode);
    }

    [Fact]
    public void FileOwnership_Properties()
    {
        // Arrange
        var ownership = new FileOwnership
        {
            OwnerId = "S-1-5-21-123456789",
            GroupId = "S-1-5-32-544",
            OwnerName = "DOMAIN\\User",
            GroupName = "Administrators",
            UnixMode = 493 // 0o755
        };

        // Assert
        Assert.Equal("S-1-5-21-123456789", ownership.OwnerId);
        Assert.Equal("S-1-5-32-544", ownership.GroupId);
        Assert.Equal("DOMAIN\\User", ownership.OwnerName);
        Assert.Equal("Administrators", ownership.GroupName);
        Assert.Equal(493, ownership.UnixMode);
    }

    [Fact]
    public void OwnershipProviderFactory_CreatesCorrectProvider()
    {
        // Arrange
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(Mock.Of<ILogger>());
        var factory = new OwnershipProviderFactory(loggerFactory.Object);

        // Act
        var provider = factory.Create();

        // Assert
        Assert.NotNull(provider);
        Assert.True(provider.IsSupported);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task SetOwnershipAsync_NonExistentFile_ReturnsFalse()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsOwnershipProvider(_loggerMock.Object);
        var nonExistent = Path.Combine(_testDir, "nonexistent.txt");
        var ownership = new FileOwnership { OwnerId = "S-1-5-21-123456789" };

        // Act
        var result = await provider.SetOwnershipAsync(nonExistent, ownership);

        // Assert
        Assert.False(result);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task GetOwnershipAsync_ReadOnlyFile_IncludesRestrictedMode()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsOwnershipProvider(_loggerMock.Object);
        var readOnlyFile = Path.Combine(_testDir, "readonly.txt");
        File.WriteAllText(readOnlyFile, "content");
        File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);

        try
        {
            // Act
            var ownership = await provider.GetOwnershipAsync(readOnlyFile);

            // Assert
            Assert.NotNull(ownership);
            // Read-only should have reduced permissions
            Assert.True(ownership.UnixMode < 493); // Less than 0o755
        }
        finally
        {
            // Cleanup - remove readonly attribute
            File.SetAttributes(readOnlyFile, FileAttributes.Normal);
        }
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task GetOwnershipAsync_Directory_HasDirectoryMode()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // Skip on non-Windows

        // Arrange
        var provider = new WindowsOwnershipProvider(_loggerMock.Object);

        // Act
        var ownership = await provider.GetOwnershipAsync(_testDir);

        // Assert
        Assert.NotNull(ownership);
        Assert.Equal(493, ownership.UnixMode); // 0o755 for directory
    }
}
