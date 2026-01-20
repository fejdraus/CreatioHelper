using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync.Versioning;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.UnitTests.Sync.Versioning;

public class ExternalVersionerTests : IDisposable
{
    private readonly Mock<ILogger<ExternalVersioner>> _loggerMock;
    private readonly string _testDir;
    private readonly string _versionsDir;

    public ExternalVersionerTests()
    {
        _loggerMock = new Mock<ILogger<ExternalVersioner>>();
        _testDir = Path.Combine(Path.GetTempPath(), $"external_versioner_tests_{Guid.NewGuid():N}");
        _versionsDir = Path.Combine(_testDir, ".stversions");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenLoggerIsNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ExternalVersioner(null!, _testDir, "echo"));
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenFolderPathIsNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ExternalVersioner(_loggerMock.Object, null!, "echo"));
    }

    [Fact]
    public void Constructor_ThrowsArgumentNullException_WhenCommandIsNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ExternalVersioner(_loggerMock.Object, _testDir, null!));
    }

    [Fact]
    public void Constructor_CreatesVersionsDirectory()
    {
        // Act
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        // Assert
        Assert.True(Directory.Exists(_versionsDir));
    }

    [Fact]
    public void Constructor_UsesCustomVersionsPath()
    {
        // Arrange
        var customVersionsPath = Path.Combine(_testDir, "custom_versions");

        // Act
        using var versioner = new ExternalVersioner(
            _loggerMock.Object, _testDir, "echo", customVersionsPath);

        // Assert
        Assert.True(Directory.Exists(customVersionsPath));
        Assert.Equal(customVersionsPath, versioner.VersionsPath);
    }

    #endregion

    #region Property Tests

    [Fact]
    public void VersionerType_ReturnsExternal()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        // Assert
        Assert.Equal("external", versioner.VersionerType);
    }

    [Fact]
    public void VersionsPath_ReturnsDefaultStversions()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        // Assert
        Assert.Equal(_versionsDir, versioner.VersionsPath);
    }

    #endregion

    #region ArchiveAsync Tests

    [Fact]
    public async Task ArchiveAsync_ThrowsArgumentException_WhenFilePathIsEmpty()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            versioner.ArchiveAsync(string.Empty));
    }

    [Fact]
    public async Task ArchiveAsync_DoesNotThrow_WhenFileDoesNotExist()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        // Act (should not throw, just log warning)
        await versioner.ArchiveAsync("nonexistent.txt");

        // Assert - no exception thrown
    }

    #endregion

    #region GetVersionsAsync Tests

    [Fact]
    public async Task GetVersionsAsync_ReturnsEmptyDictionary_WhenNoVersions()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        // Act
        var versions = await versioner.GetVersionsAsync();

        // Assert
        Assert.Empty(versions);
    }

    [Fact]
    public async Task GetVersionsAsync_ReturnsVersions_WhenVersionFilesExist()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        // Create a version file with Syncthing naming convention
        var versionFile = Path.Combine(_versionsDir, "test~20240115-103000.txt");
        await File.WriteAllTextAsync(versionFile, "version content");

        // Act
        var versions = await versioner.GetVersionsAsync();

        // Assert
        Assert.Single(versions);
        Assert.True(versions.ContainsKey("test.txt"));
        Assert.Single(versions["test.txt"]);
    }

    [Fact]
    public async Task GetVersionsAsync_ParsesTimestampFromFilename()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        // Create a version file with known timestamp
        var versionFile = Path.Combine(_versionsDir, "document~20240115-143022.txt");
        await File.WriteAllTextAsync(versionFile, "content");

        // Act
        var versions = await versioner.GetVersionsAsync();

        // Assert
        var version = versions["document.txt"].First();
        Assert.Equal(new DateTime(2024, 1, 15, 14, 30, 22, DateTimeKind.Utc), version.VersionTime);
    }

    [Fact]
    public async Task GetVersionsAsync_HandlesFileWithoutTimestamp()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        // Create a version file without timestamp pattern
        var versionFile = Path.Combine(_versionsDir, "simple_backup.txt");
        await File.WriteAllTextAsync(versionFile, "content");

        // Act
        var versions = await versioner.GetVersionsAsync();

        // Assert
        Assert.Single(versions);
        Assert.True(versions.ContainsKey("simple_backup.txt"));
    }

    [Fact]
    public async Task GetVersionsAsync_ReturnsMultipleVersions_OrderedByTime()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        // Create multiple versions
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, "file~20240101-100000.txt"), "v1");
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, "file~20240103-100000.txt"), "v3");
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, "file~20240102-100000.txt"), "v2");

        // Act
        var versions = await versioner.GetVersionsAsync();

        // Assert
        Assert.Equal(3, versions["file.txt"].Count);
        // Newest first
        Assert.Equal(new DateTime(2024, 1, 3, 10, 0, 0, DateTimeKind.Utc), versions["file.txt"][0].VersionTime);
        Assert.Equal(new DateTime(2024, 1, 2, 10, 0, 0, DateTimeKind.Utc), versions["file.txt"][1].VersionTime);
        Assert.Equal(new DateTime(2024, 1, 1, 10, 0, 0, DateTimeKind.Utc), versions["file.txt"][2].VersionTime);
    }

    [Fact]
    public async Task GetVersionsAsync_IncludesFileSize()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        var content = "test content with specific length";
        var versionFile = Path.Combine(_versionsDir, "sized~20240115-100000.txt");
        await File.WriteAllTextAsync(versionFile, content);

        // Act
        var versions = await versioner.GetVersionsAsync();

        // Assert
        var version = versions["sized.txt"].First();
        Assert.Equal(content.Length, version.Size);
    }

    [Fact]
    public async Task GetVersionsAsync_HandlesSubdirectories()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        var subdir = Path.Combine(_versionsDir, "subdir");
        Directory.CreateDirectory(subdir);
        await File.WriteAllTextAsync(Path.Combine(subdir, "nested~20240115-100000.txt"), "nested");

        // Act
        var versions = await versioner.GetVersionsAsync();

        // Assert
        Assert.Single(versions);
        var key = versions.Keys.First();
        Assert.Contains("nested.txt", key);
    }

    [Fact]
    public async Task GetVersionsAsync_AcceptsCancellationToken()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");
        using var cts = new CancellationTokenSource();

        // Create a version file
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, "file~20240115-100000.txt"), "content");

        // Act - pass non-cancelled token (validates the method accepts the parameter)
        var versions = await versioner.GetVersionsAsync(cts.Token);

        // Assert
        Assert.Single(versions);
    }

    #endregion

    #region RestoreAsync Tests

    [Fact]
    public async Task RestoreAsync_ThrowsArgumentException_WhenFilePathIsEmpty()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(() =>
            versioner.RestoreAsync(string.Empty, DateTime.UtcNow));
    }

    [Fact]
    public async Task RestoreAsync_ThrowsFileNotFoundException_WhenNoVersionsExist()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            versioner.RestoreAsync("nonexistent.txt", DateTime.UtcNow));
    }

    [Fact]
    public async Task RestoreAsync_RestoresFile_ToOriginalLocation()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        // Create a version file
        var versionContent = "restored content";
        var versionFile = Path.Combine(_versionsDir, "restore_test~20240115-100000.txt");
        await File.WriteAllTextAsync(versionFile, versionContent);

        // Act
        await versioner.RestoreAsync("restore_test.txt", new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc));

        // Assert
        var restoredPath = Path.Combine(_testDir, "restore_test.txt");
        Assert.True(File.Exists(restoredPath));
        Assert.Equal(versionContent, await File.ReadAllTextAsync(restoredPath));
    }

    [Fact]
    public async Task RestoreAsync_FindsClosestVersion_WhenExactTimeNotFound()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        // Create version files
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, "file~20240115-100000.txt"), "v1");
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, "file~20240115-120000.txt"), "v2");

        // Act - request a time between the two versions (closer to v1)
        await versioner.RestoreAsync("file.txt", new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc));

        // Assert
        var restoredPath = Path.Combine(_testDir, "file.txt");
        Assert.Equal("v1", await File.ReadAllTextAsync(restoredPath));
    }

    [Fact]
    public async Task RestoreAsync_CreatesDestinationDirectory_IfNotExists()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        var subdir = Path.Combine(_versionsDir, "subdir");
        Directory.CreateDirectory(subdir);
        await File.WriteAllTextAsync(Path.Combine(subdir, "nested~20240115-100000.txt"), "nested content");

        // Act
        await versioner.RestoreAsync(Path.Combine("subdir", "nested.txt"),
            new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc));

        // Assert
        var restoredPath = Path.Combine(_testDir, "subdir", "nested.txt");
        Assert.True(File.Exists(restoredPath));
    }

    #endregion

    #region CleanAsync Tests

    [Fact]
    public async Task CleanAsync_CompletesWithoutError()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        // Act - Clean is delegated to external command, so this just logs
        await versioner.CleanAsync();

        // Assert - no exception thrown
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        // Act & Assert (should not throw)
        versioner.Dispose();
        versioner.Dispose();
    }

    #endregion

    #region ParseOriginalPath Tests (via GetVersionsAsync)

    [Theory]
    [InlineData("file~20240115-100000.txt", "file.txt")]
    [InlineData("document~20240115-100000.pdf", "document.pdf")]
    [InlineData("archive~20240115-100000.tar.gz", "archive.tar.gz")]
    [InlineData("no_timestamp.txt", "no_timestamp.txt")]
    public async Task GetVersionsAsync_ParsesOriginalPathCorrectly(string versionName, string expectedOriginal)
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, versionName), "content");

        // Act
        var versions = await versioner.GetVersionsAsync();

        // Assert
        Assert.True(versions.ContainsKey(expectedOriginal));
    }

    #endregion

    #region ParseVersionTime Tests (via GetVersionsAsync)

    [Theory]
    [InlineData("file~20240115-103022.txt", 2024, 1, 15, 10, 30, 22)]
    [InlineData("file~20231231-235959.txt", 2023, 12, 31, 23, 59, 59)]
    [InlineData("file~20240701-000000.txt", 2024, 7, 1, 0, 0, 0)]
    public async Task GetVersionsAsync_ParsesVersionTimeCorrectly(
        string versionName, int year, int month, int day, int hour, int minute, int second)
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, versionName), "content");

        // Act
        var versions = await versioner.GetVersionsAsync();

        // Assert
        var version = versions.Values.First().First();
        var expected = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Utc);
        Assert.Equal(expected, version.VersionTime);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task GetVersionsAsync_HandlesEmptyVersionsDirectory()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");
        // .stversions is created but empty

        // Act
        var versions = await versioner.GetVersionsAsync();

        // Assert
        Assert.Empty(versions);
    }

    [Fact]
    public async Task GetVersionsAsync_HandlesVersionsDirectoryWithOnlySubdirectories()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");
        Directory.CreateDirectory(Path.Combine(_versionsDir, "empty_subdir"));

        // Act
        var versions = await versioner.GetVersionsAsync();

        // Assert
        Assert.Empty(versions);
    }

    [Fact]
    public async Task RestoreAsync_OverwritesExistingFile()
    {
        // Arrange
        using var versioner = new ExternalVersioner(_loggerMock.Object, _testDir, "echo");

        // Create original file
        var originalPath = Path.Combine(_testDir, "overwrite_test.txt");
        await File.WriteAllTextAsync(originalPath, "original content");

        // Create version file
        var versionFile = Path.Combine(_versionsDir, "overwrite_test~20240115-100000.txt");
        await File.WriteAllTextAsync(versionFile, "version content");

        // Act
        await versioner.RestoreAsync("overwrite_test.txt", new DateTime(2024, 1, 15, 10, 0, 0, DateTimeKind.Utc));

        // Assert
        Assert.Equal("version content", await File.ReadAllTextAsync(originalPath));
    }

    [Fact]
    public void Constructor_AcceptsCustomCleanupInterval()
    {
        // Act
        using var versioner = new ExternalVersioner(
            _loggerMock.Object, _testDir, "echo", cleanupIntervalS: 7200);

        // Assert - no exception, versioner created successfully
        Assert.NotNull(versioner);
    }

    #endregion
}
