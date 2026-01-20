using CreatioHelper.Infrastructure.Services.Sync.Versioning;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.UnitTests.Sync.Versioning;

public class SimpleVersionerTests : IDisposable
{
    private readonly Mock<ILogger<SimpleVersioner>> _loggerMock;
    private readonly string _testDir;
    private readonly string _versionsDir;

    public SimpleVersionerTests()
    {
        _loggerMock = new Mock<ILogger<SimpleVersioner>>();
        _testDir = Path.Combine(Path.GetTempPath(), $"simple_versioner_tests_{Guid.NewGuid():N}");
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
    public void Constructor_CreatesVersionsDirectory()
    {
        // Act
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir);

        // Assert
        Assert.True(Directory.Exists(_versionsDir));
    }

    [Fact]
    public void Constructor_UsesCustomVersionsPath()
    {
        // Arrange
        var customVersionsPath = Path.Combine(_testDir, "custom_versions");

        // Act
        using var versioner = new SimpleVersioner(
            _loggerMock.Object, _testDir, versionsPath: customVersionsPath);

        // Assert
        Assert.True(Directory.Exists(customVersionsPath));
        Assert.Equal(customVersionsPath, versioner.VersionsPath);
    }

    [Fact]
    public void Constructor_AcceptsKeepVersionsParameter()
    {
        // Act
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir, keepVersions: 10);

        // Assert - no exception
        Assert.NotNull(versioner);
    }

    [Fact]
    public void Constructor_AcceptsCleanoutDaysParameter()
    {
        // Act
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir, cleanoutDays: 30);

        // Assert - no exception
        Assert.NotNull(versioner);
    }

    #endregion

    #region Property Tests

    [Fact]
    public void VersionerType_ReturnsSimple()
    {
        // Arrange
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir);

        // Assert
        Assert.Equal("simple", versioner.VersionerType);
    }

    [Fact]
    public void VersionsPath_ReturnsDefaultStversions()
    {
        // Arrange
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir);

        // Assert
        Assert.Equal(_versionsDir, versioner.VersionsPath);
    }

    #endregion

    #region ArchiveAsync Tests

    [Fact]
    public async Task ArchiveAsync_MovesFileToVersionsDirectory()
    {
        // Arrange
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir, keepVersions: 5);
        var testFile = Path.Combine(_testDir, "test.txt");
        await File.WriteAllTextAsync(testFile, "original content");

        // Act
        await versioner.ArchiveAsync("test.txt");

        // Assert
        Assert.False(File.Exists(testFile)); // Original file moved
        var versionFiles = Directory.GetFiles(_versionsDir, "test~*.txt");
        Assert.Single(versionFiles);
    }

    [Fact]
    public async Task ArchiveAsync_DoesNotThrow_WhenFileDoesNotExist()
    {
        // Arrange
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir);

        // Act & Assert - should not throw
        await versioner.ArchiveAsync("nonexistent.txt");
    }

    [Fact]
    public async Task ArchiveAsync_PreservesFileContent()
    {
        // Arrange
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir, keepVersions: 5);
        var testFile = Path.Combine(_testDir, "preserve.txt");
        var content = "content to preserve";
        await File.WriteAllTextAsync(testFile, content);

        // Act
        await versioner.ArchiveAsync("preserve.txt");

        // Assert
        var versionFiles = Directory.GetFiles(_versionsDir, "preserve~*.txt");
        var archivedContent = await File.ReadAllTextAsync(versionFiles[0]);
        Assert.Equal(content, archivedContent);
    }

    [Fact]
    public async Task ArchiveAsync_CreatesTimestampedFilename()
    {
        // Arrange
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir, keepVersions: 5);
        var testFile = Path.Combine(_testDir, "timestamped.txt");
        await File.WriteAllTextAsync(testFile, "content");

        // Act
        await versioner.ArchiveAsync("timestamped.txt");

        // Assert
        var versionFiles = Directory.GetFiles(_versionsDir, "timestamped~*.txt");
        Assert.Single(versionFiles);
        // Pattern: filename~YYYYMMDD-HHMMSS.ext
        Assert.Matches(@"timestamped~\d{8}-\d{6}\.txt$", Path.GetFileName(versionFiles[0]));
    }

    [Fact]
    public async Task ArchiveAsync_HandlesSubdirectories()
    {
        // Arrange
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir, keepVersions: 5);
        var subDir = Path.Combine(_testDir, "subdir");
        Directory.CreateDirectory(subDir);
        var testFile = Path.Combine(subDir, "nested.txt");
        await File.WriteAllTextAsync(testFile, "nested content");

        // Act
        await versioner.ArchiveAsync(Path.Combine("subdir", "nested.txt"));

        // Assert
        var versionSubDir = Path.Combine(_versionsDir, "subdir");
        Assert.True(Directory.Exists(versionSubDir));
        var versionFiles = Directory.GetFiles(versionSubDir, "nested~*.txt");
        Assert.Single(versionFiles);
    }

    #endregion

    #region GetVersionsAsync Tests

    [Fact]
    public async Task GetVersionsAsync_ReturnsEmptyDictionary_WhenNoVersions()
    {
        // Arrange
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir);

        // Act
        var versions = await versioner.GetVersionsAsync();

        // Assert
        Assert.Empty(versions);
    }

    [Fact]
    public async Task GetVersionsAsync_ReturnsVersions_WhenVersionFilesExist()
    {
        // Arrange
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir);
        // Create a version file manually
        var versionFile = Path.Combine(_versionsDir, "test~20240115-103000.txt");
        await File.WriteAllTextAsync(versionFile, "version content");

        // Act
        var versions = await versioner.GetVersionsAsync();

        // Assert
        Assert.Single(versions);
        Assert.True(versions.ContainsKey("test.txt"));
    }

    [Fact]
    public async Task GetVersionsAsync_SortsVersionsByTimeDescending()
    {
        // Arrange
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir);
        // Create versions with different timestamps
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, "file~20240101-100000.txt"), "v1");
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, "file~20240103-100000.txt"), "v3");
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, "file~20240102-100000.txt"), "v2");

        // Act
        var versions = await versioner.GetVersionsAsync();

        // Assert
        Assert.Equal(3, versions["file.txt"].Count);
        // Newest first
        Assert.Equal(new DateTime(2024, 1, 3, 10, 0, 0), versions["file.txt"][0].VersionTime);
        Assert.Equal(new DateTime(2024, 1, 2, 10, 0, 0), versions["file.txt"][1].VersionTime);
        Assert.Equal(new DateTime(2024, 1, 1, 10, 0, 0), versions["file.txt"][2].VersionTime);
    }

    [Fact]
    public async Task GetVersionsAsync_IncludesFileSize()
    {
        // Arrange
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir);
        var content = "test content with specific length";
        var versionFile = Path.Combine(_versionsDir, "sized~20240115-100000.txt");
        await File.WriteAllTextAsync(versionFile, content);

        // Act
        var versions = await versioner.GetVersionsAsync();

        // Assert
        var version = versions["sized.txt"].First();
        Assert.Equal(content.Length, version.Size);
    }

    #endregion

    #region RestoreAsync Tests

    [Fact]
    public async Task RestoreAsync_RestoresFile_ToOriginalLocation()
    {
        // Arrange
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir, keepVersions: 10);
        var versionContent = "restored content";
        var versionFile = Path.Combine(_versionsDir, "restore~20240115-100000.txt");
        await File.WriteAllTextAsync(versionFile, versionContent);

        // Act
        await versioner.RestoreAsync("restore.txt", new DateTime(2024, 1, 15, 10, 0, 0));

        // Assert
        var restoredPath = Path.Combine(_testDir, "restore.txt");
        Assert.True(File.Exists(restoredPath));
        Assert.Equal(versionContent, await File.ReadAllTextAsync(restoredPath));
    }

    [Fact]
    public async Task RestoreAsync_ThrowsFileNotFoundException_WhenVersionNotFound()
    {
        // Arrange
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir);

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            versioner.RestoreAsync("nonexistent.txt", DateTime.Now));
    }

    [Fact]
    public async Task RestoreAsync_ArchivesExistingFile_BeforeRestore()
    {
        // Arrange
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir, keepVersions: 10);

        // Create existing file
        var existingFile = Path.Combine(_testDir, "existing.txt");
        await File.WriteAllTextAsync(existingFile, "existing content");

        // Create version to restore
        var versionFile = Path.Combine(_versionsDir, "existing~20240115-100000.txt");
        await File.WriteAllTextAsync(versionFile, "version content");

        // Act
        await versioner.RestoreAsync("existing.txt", new DateTime(2024, 1, 15, 10, 0, 0));

        // Assert
        var restoredContent = await File.ReadAllTextAsync(existingFile);
        Assert.Equal("version content", restoredContent);

        // Original file should have been archived
        var versionFiles = Directory.GetFiles(_versionsDir, "existing~*.txt");
        Assert.True(versionFiles.Length >= 1); // At least one archive created
    }

    #endregion

    #region CleanAsync Tests

    [Fact]
    public async Task CleanAsync_RemovesOldVersions_WhenExceedingKeepCount()
    {
        // Arrange
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir, keepVersions: 2);

        // Create 4 versions
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, "file~20240101-100000.txt"), "v1");
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, "file~20240102-100000.txt"), "v2");
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, "file~20240103-100000.txt"), "v3");
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, "file~20240104-100000.txt"), "v4");

        // Act
        await versioner.CleanAsync();

        // Assert - should keep only 2 newest
        var remainingFiles = Directory.GetFiles(_versionsDir, "file~*.txt");
        Assert.Equal(2, remainingFiles.Length);
        Assert.Contains(remainingFiles, f => f.Contains("20240104"));
        Assert.Contains(remainingFiles, f => f.Contains("20240103"));
    }

    [Fact]
    public async Task CleanAsync_RemovesOldVersions_BasedOnCleanoutDays()
    {
        // Arrange
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir, keepVersions: 100, cleanoutDays: 1);

        // Create an old version file
        var oldVersionFile = Path.Combine(_versionsDir, "old~20200101-100000.txt");
        await File.WriteAllTextAsync(oldVersionFile, "old content");

        // Create a recent version file
        var recentTimestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var recentVersionFile = Path.Combine(_versionsDir, $"old~{recentTimestamp}.txt");
        await File.WriteAllTextAsync(recentVersionFile, "recent content");

        // Act
        await versioner.CleanAsync();

        // Assert - old version should be removed
        Assert.False(File.Exists(oldVersionFile));
        Assert.True(File.Exists(recentVersionFile));
    }

    [Fact]
    public async Task CleanAsync_DoesNothing_WhenNoVersions()
    {
        // Arrange
        using var versioner = new SimpleVersioner(_loggerMock.Object, _testDir);

        // Act & Assert - should not throw
        await versioner.CleanAsync();
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var versioner = new SimpleVersioner(_loggerMock.Object, _testDir);

        // Act & Assert - should not throw
        versioner.Dispose();
        versioner.Dispose();
    }

    #endregion
}
