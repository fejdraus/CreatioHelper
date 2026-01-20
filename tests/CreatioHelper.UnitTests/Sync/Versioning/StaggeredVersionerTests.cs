using CreatioHelper.Infrastructure.Services.Sync.Versioning;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.UnitTests.Sync.Versioning;

public class StaggeredVersionerTests : IDisposable
{
    private readonly Mock<ILogger<StaggeredVersioner>> _loggerMock;
    private readonly string _testDir;
    private readonly string _versionsDir;

    public StaggeredVersionerTests()
    {
        _loggerMock = new Mock<ILogger<StaggeredVersioner>>();
        _testDir = Path.Combine(Path.GetTempPath(), $"staggered_versioner_tests_{Guid.NewGuid():N}");
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
        using var versioner = new StaggeredVersioner(_loggerMock.Object, _testDir);

        // Assert
        Assert.True(Directory.Exists(_versionsDir));
    }

    [Fact]
    public void Constructor_UsesCustomVersionsPath()
    {
        // Arrange
        var customVersionsPath = Path.Combine(_testDir, "custom_versions");

        // Act
        using var versioner = new StaggeredVersioner(
            _loggerMock.Object, _testDir, versionsPath: customVersionsPath);

        // Assert
        Assert.True(Directory.Exists(customVersionsPath));
        Assert.Equal(customVersionsPath, versioner.VersionsPath);
    }

    [Fact]
    public void Constructor_AcceptsMaxAgeParameter()
    {
        // Act - 30 days max age
        using var versioner = new StaggeredVersioner(
            _loggerMock.Object, _testDir, maxAgeSeconds: 30 * 24 * 3600);

        // Assert - no exception
        Assert.NotNull(versioner);
    }

    #endregion

    #region Property Tests

    [Fact]
    public void VersionerType_ReturnsStaggered()
    {
        // Arrange
        using var versioner = new StaggeredVersioner(_loggerMock.Object, _testDir);

        // Assert
        Assert.Equal("staggered", versioner.VersionerType);
    }

    [Fact]
    public void VersionsPath_ReturnsDefaultStversions()
    {
        // Arrange
        using var versioner = new StaggeredVersioner(_loggerMock.Object, _testDir);

        // Assert
        Assert.Equal(_versionsDir, versioner.VersionsPath);
    }

    #endregion

    #region ArchiveAsync Tests

    [Fact]
    public async Task ArchiveAsync_MovesFileToVersionsDirectory()
    {
        // Arrange
        using var versioner = new StaggeredVersioner(_loggerMock.Object, _testDir);
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
        using var versioner = new StaggeredVersioner(_loggerMock.Object, _testDir);

        // Act & Assert - should not throw
        await versioner.ArchiveAsync("nonexistent.txt");
    }

    [Fact]
    public async Task ArchiveAsync_PreservesFileContent()
    {
        // Arrange
        using var versioner = new StaggeredVersioner(_loggerMock.Object, _testDir);
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
        using var versioner = new StaggeredVersioner(_loggerMock.Object, _testDir);
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
        using var versioner = new StaggeredVersioner(_loggerMock.Object, _testDir);
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
        using var versioner = new StaggeredVersioner(_loggerMock.Object, _testDir);

        // Act
        var versions = await versioner.GetVersionsAsync();

        // Assert
        Assert.Empty(versions);
    }

    [Fact]
    public async Task GetVersionsAsync_ReturnsVersions_WhenVersionFilesExist()
    {
        // Arrange
        using var versioner = new StaggeredVersioner(_loggerMock.Object, _testDir);
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
        using var versioner = new StaggeredVersioner(_loggerMock.Object, _testDir);
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
        using var versioner = new StaggeredVersioner(_loggerMock.Object, _testDir);
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
        using var versioner = new StaggeredVersioner(_loggerMock.Object, _testDir);
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
        using var versioner = new StaggeredVersioner(_loggerMock.Object, _testDir);

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            versioner.RestoreAsync("nonexistent.txt", DateTime.Now));
    }

    [Fact]
    public async Task RestoreAsync_ArchivesExistingFile_BeforeRestore()
    {
        // Arrange
        using var versioner = new StaggeredVersioner(_loggerMock.Object, _testDir);

        // Create existing file with specific content
        var existingFile = Path.Combine(_testDir, "existing.txt");
        await File.WriteAllTextAsync(existingFile, "existing content");

        // Create version to restore - use a timestamp far in the past (weeks ago)
        // This ensures it won't be cleaned up by staggered interval logic when we archive
        var versionTimestamp = DateTime.Now.AddDays(-14);
        var versionTimestampStr = versionTimestamp.ToString("yyyyMMdd-HHmmss");
        var versionFile = Path.Combine(_versionsDir, $"existing~{versionTimestampStr}.txt");
        await File.WriteAllTextAsync(versionFile, "version content");

        // Act - restore archives the existing file, then moves the version to original location
        await versioner.RestoreAsync("existing.txt", versionTimestamp);

        // Assert - file should be restored with version content
        var restoredContent = await File.ReadAllTextAsync(existingFile);
        Assert.Equal("version content", restoredContent);

        // A new version file should exist (the archived "existing content")
        // Note: the original version file was moved, not copied
        var versionFiles = Directory.GetFiles(_versionsDir, "existing~*.txt");
        Assert.Single(versionFiles); // The archived existing file

        // The archived file should contain the old "existing content"
        var archivedContent = await File.ReadAllTextAsync(versionFiles[0]);
        Assert.Equal("existing content", archivedContent);
    }

    #endregion

    #region CleanAsync Tests

    [Fact]
    public async Task CleanAsync_RemovesVersionsOlderThanMaxAge()
    {
        // Arrange - 1 day max age
        using var versioner = new StaggeredVersioner(_loggerMock.Object, _testDir, maxAgeSeconds: 24 * 3600);

        // Create an old version (2 years ago)
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, "old~20220101-100000.txt"), "old content");

        // Create a recent version
        var recentTimestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, $"old~{recentTimestamp}.txt"), "recent content");

        // Act
        await versioner.CleanAsync();

        // Assert - old version should be removed
        var remainingFiles = Directory.GetFiles(_versionsDir, "old~*.txt");
        Assert.Single(remainingFiles);
        Assert.Contains(recentTimestamp, remainingFiles[0]);
    }

    [Fact]
    public async Task CleanAsync_DoesNothing_WhenNoVersions()
    {
        // Arrange
        using var versioner = new StaggeredVersioner(_loggerMock.Object, _testDir);

        // Act & Assert - should not throw
        await versioner.CleanAsync();
    }

    [Fact]
    public async Task CleanAsync_AppliesStaggeredIntervals()
    {
        // Arrange - with long max age to focus on interval testing
        using var versioner = new StaggeredVersioner(_loggerMock.Object, _testDir, maxAgeSeconds: 365 * 24 * 3600);

        // Create versions very close together (within 30 sec interval)
        var baseTime = DateTime.Now.AddMinutes(-5);
        for (int i = 0; i < 5; i++)
        {
            var timestamp = baseTime.AddSeconds(i * 5).ToString("yyyyMMdd-HHmmss"); // 5 sec apart
            await File.WriteAllTextAsync(Path.Combine(_versionsDir, $"stagger~{timestamp}.txt"), $"v{i}");
        }

        // Act
        await versioner.CleanAsync();

        // Assert - some versions should be removed due to staggered intervals
        var remainingFiles = Directory.GetFiles(_versionsDir, "stagger~*.txt");
        // Due to 30 sec interval, not all 5 should remain
        Assert.True(remainingFiles.Length < 5);
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var versioner = new StaggeredVersioner(_loggerMock.Object, _testDir);

        // Act & Assert - should not throw
        versioner.Dispose();
        versioner.Dispose();
    }

    #endregion
}
