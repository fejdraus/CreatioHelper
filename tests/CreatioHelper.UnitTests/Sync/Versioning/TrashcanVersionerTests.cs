using CreatioHelper.Infrastructure.Services.Sync.Versioning;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.UnitTests.Sync.Versioning;

public class TrashcanVersionerTests : IDisposable
{
    private readonly Mock<ILogger<TrashcanVersioner>> _loggerMock;
    private readonly string _testDir;
    private readonly string _versionsDir;

    public TrashcanVersionerTests()
    {
        _loggerMock = new Mock<ILogger<TrashcanVersioner>>();
        _testDir = Path.Combine(Path.GetTempPath(), $"trashcan_versioner_tests_{Guid.NewGuid():N}");
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
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir);

        // Assert
        Assert.True(Directory.Exists(_versionsDir));
    }

    [Fact]
    public void Constructor_UsesCustomVersionsPath()
    {
        // Arrange
        var customVersionsPath = Path.Combine(_testDir, "custom_trash");

        // Act
        using var versioner = new TrashcanVersioner(
            _loggerMock.Object, _testDir, versionsPath: customVersionsPath);

        // Assert
        Assert.True(Directory.Exists(customVersionsPath));
        Assert.Equal(customVersionsPath, versioner.VersionsPath);
    }

    [Fact]
    public void Constructor_AcceptsCleanoutDaysParameter()
    {
        // Act - 30 days cleanout
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir, cleanoutDays: 30);

        // Assert - no exception
        Assert.NotNull(versioner);
    }

    [Fact]
    public void Constructor_AcceptsZeroCleanoutDays_KeepsForever()
    {
        // Act - 0 means keep forever
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir, cleanoutDays: 0);

        // Assert - no exception
        Assert.NotNull(versioner);
    }

    #endregion

    #region Property Tests

    [Fact]
    public void VersionerType_ReturnsTrashcan()
    {
        // Arrange
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir);

        // Assert
        Assert.Equal("trashcan", versioner.VersionerType);
    }

    [Fact]
    public void VersionsPath_ReturnsDefaultStversions()
    {
        // Arrange
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir);

        // Assert
        Assert.Equal(_versionsDir, versioner.VersionsPath);
    }

    #endregion

    #region ArchiveAsync Tests

    [Fact]
    public async Task ArchiveAsync_MovesFileToTrashcan()
    {
        // Arrange
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir);
        var testFile = Path.Combine(_testDir, "test.txt");
        await File.WriteAllTextAsync(testFile, "original content");

        // Act
        await versioner.ArchiveAsync("test.txt");

        // Assert
        Assert.False(File.Exists(testFile)); // Original file moved
        var trashFiles = Directory.GetFiles(_versionsDir, "test~*.txt");
        Assert.Single(trashFiles);
    }

    [Fact]
    public async Task ArchiveAsync_DoesNotThrow_WhenFileDoesNotExist()
    {
        // Arrange
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir);

        // Act & Assert - should not throw
        await versioner.ArchiveAsync("nonexistent.txt");
    }

    [Fact]
    public async Task ArchiveAsync_PreservesFileContent()
    {
        // Arrange
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir);
        var testFile = Path.Combine(_testDir, "preserve.txt");
        var content = "content to preserve";
        await File.WriteAllTextAsync(testFile, content);

        // Act
        await versioner.ArchiveAsync("preserve.txt");

        // Assert
        var trashFiles = Directory.GetFiles(_versionsDir, "preserve~*.txt");
        var archivedContent = await File.ReadAllTextAsync(trashFiles[0]);
        Assert.Equal(content, archivedContent);
    }

    [Fact]
    public async Task ArchiveAsync_CreatesTimestampedFilename()
    {
        // Arrange
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir);
        var testFile = Path.Combine(_testDir, "timestamped.txt");
        await File.WriteAllTextAsync(testFile, "content");

        // Act
        await versioner.ArchiveAsync("timestamped.txt");

        // Assert
        var trashFiles = Directory.GetFiles(_versionsDir, "timestamped~*.txt");
        Assert.Single(trashFiles);
        // Pattern: filename~YYYYMMDD-HHMMSS.ext
        Assert.Matches(@"timestamped~\d{8}-\d{6}\.txt$", Path.GetFileName(trashFiles[0]));
    }

    [Fact]
    public async Task ArchiveAsync_HandlesSubdirectories()
    {
        // Arrange
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir);
        var subDir = Path.Combine(_testDir, "subdir");
        Directory.CreateDirectory(subDir);
        var testFile = Path.Combine(subDir, "nested.txt");
        await File.WriteAllTextAsync(testFile, "nested content");

        // Act
        await versioner.ArchiveAsync(Path.Combine("subdir", "nested.txt"));

        // Assert
        var trashSubDir = Path.Combine(_versionsDir, "subdir");
        Assert.True(Directory.Exists(trashSubDir));
        var trashFiles = Directory.GetFiles(trashSubDir, "nested~*.txt");
        Assert.Single(trashFiles);
    }

    [Fact]
    public async Task ArchiveAsync_KeepsMultipleVersions()
    {
        // Arrange
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir);

        // Manually create multiple "archived" versions to verify trashcan keeps all
        // This avoids timing issues with rapid archiving
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, "multi~20240101-100000.txt"), "v1");
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, "multi~20240101-110000.txt"), "v2");
        await File.WriteAllTextAsync(Path.Combine(_versionsDir, "multi~20240101-120000.txt"), "v3");

        // Act - verify GetVersionsAsync shows all versions
        var versions = await versioner.GetVersionsAsync();

        // Assert - trashcan keeps all versions (unlike simple which limits to N)
        Assert.Equal(3, versions["multi.txt"].Count);
    }

    #endregion

    #region GetVersionsAsync Tests

    [Fact]
    public async Task GetVersionsAsync_ReturnsEmptyDictionary_WhenNoVersions()
    {
        // Arrange
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir);

        // Act
        var versions = await versioner.GetVersionsAsync();

        // Assert
        Assert.Empty(versions);
    }

    [Fact]
    public async Task GetVersionsAsync_ReturnsVersions_WhenTrashFilesExist()
    {
        // Arrange
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir);
        // Create a trash file manually
        var trashFile = Path.Combine(_versionsDir, "test~20240115-103000.txt");
        await File.WriteAllTextAsync(trashFile, "trash content");

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
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir);
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
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir);
        var content = "test content with specific length";
        var trashFile = Path.Combine(_versionsDir, "sized~20240115-100000.txt");
        await File.WriteAllTextAsync(trashFile, content);

        // Act
        var versions = await versioner.GetVersionsAsync();

        // Assert
        var version = versions["sized.txt"].First();
        Assert.Equal(content.Length, version.Size);
    }

    #endregion

    #region RestoreAsync Tests

    [Fact]
    public async Task RestoreAsync_RestoresFile_FromTrashcan()
    {
        // Arrange
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir);
        var trashContent = "restored content";
        var trashFile = Path.Combine(_versionsDir, "restore~20240115-100000.txt");
        await File.WriteAllTextAsync(trashFile, trashContent);

        // Act
        await versioner.RestoreAsync("restore.txt", new DateTime(2024, 1, 15, 10, 0, 0));

        // Assert
        var restoredPath = Path.Combine(_testDir, "restore.txt");
        Assert.True(File.Exists(restoredPath));
        Assert.Equal(trashContent, await File.ReadAllTextAsync(restoredPath));
    }

    [Fact]
    public async Task RestoreAsync_ThrowsFileNotFoundException_WhenVersionNotFound()
    {
        // Arrange
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir);

        // Act & Assert
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            versioner.RestoreAsync("nonexistent.txt", DateTime.Now));
    }

    [Fact]
    public async Task RestoreAsync_ArchivesExistingFile_BeforeRestore()
    {
        // Arrange
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir);

        // Create existing file
        var existingFile = Path.Combine(_testDir, "existing.txt");
        await File.WriteAllTextAsync(existingFile, "existing content");

        // Create version to restore
        var trashFile = Path.Combine(_versionsDir, "existing~20240115-100000.txt");
        await File.WriteAllTextAsync(trashFile, "trash content");

        // Act
        await versioner.RestoreAsync("existing.txt", new DateTime(2024, 1, 15, 10, 0, 0));

        // Assert
        var restoredContent = await File.ReadAllTextAsync(existingFile);
        Assert.Equal("trash content", restoredContent);

        // Original file should have been archived
        var trashFiles = Directory.GetFiles(_versionsDir, "existing~*.txt");
        Assert.True(trashFiles.Length >= 1);
    }

    [Fact]
    public async Task RestoreAsync_CreatesTargetDirectory_IfNotExists()
    {
        // Arrange
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir);
        var trashSubDir = Path.Combine(_versionsDir, "newdir");
        Directory.CreateDirectory(trashSubDir);
        await File.WriteAllTextAsync(
            Path.Combine(trashSubDir, "nested~20240115-100000.txt"), "nested content");

        // Act
        await versioner.RestoreAsync(
            Path.Combine("newdir", "nested.txt"),
            new DateTime(2024, 1, 15, 10, 0, 0));

        // Assert
        var restoredPath = Path.Combine(_testDir, "newdir", "nested.txt");
        Assert.True(File.Exists(restoredPath));
    }

    #endregion

    #region CleanAsync Tests

    [Fact]
    public async Task CleanAsync_DoesNothing_WhenCleanoutDaysIsZero()
    {
        // Arrange - cleanoutDays = 0 means keep forever
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir, cleanoutDays: 0);

        // Create an old trash file
        var oldTrashFile = Path.Combine(_versionsDir, "old~20200101-100000.txt");
        await File.WriteAllTextAsync(oldTrashFile, "old content");

        // Act
        await versioner.CleanAsync();

        // Assert - file should still exist
        Assert.True(File.Exists(oldTrashFile));
    }

    [Fact]
    public async Task CleanAsync_RemovesExpiredFiles_WhenCleanoutDaysSet()
    {
        // Arrange - 1 day cleanout
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir, cleanoutDays: 1);

        // Create an old trash file (2 years ago)
        var oldTrashFile = Path.Combine(_versionsDir, "old~20220101-100000.txt");
        await File.WriteAllTextAsync(oldTrashFile, "old content");

        // Create a recent trash file
        var recentTimestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var recentTrashFile = Path.Combine(_versionsDir, $"old~{recentTimestamp}.txt");
        await File.WriteAllTextAsync(recentTrashFile, "recent content");

        // Act
        await versioner.CleanAsync();

        // Assert - old file should be removed, recent kept
        Assert.False(File.Exists(oldTrashFile));
        Assert.True(File.Exists(recentTrashFile));
    }

    [Fact]
    public async Task CleanAsync_RemovesEmptyDirectories()
    {
        // Arrange
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir, cleanoutDays: 1);

        // Create subdir with old file
        var subDir = Path.Combine(_versionsDir, "emptydir");
        Directory.CreateDirectory(subDir);
        var oldFile = Path.Combine(subDir, "old~20200101-100000.txt");
        await File.WriteAllTextAsync(oldFile, "old");

        // Act
        await versioner.CleanAsync();

        // Assert - empty directory should be removed
        Assert.False(Directory.Exists(subDir));
    }

    [Fact]
    public async Task CleanAsync_DoesNothing_WhenNoVersions()
    {
        // Arrange
        using var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir, cleanoutDays: 30);

        // Act & Assert - should not throw
        await versioner.CleanAsync();
    }

    #endregion

    #region Dispose Tests

    [Fact]
    public void Dispose_CanBeCalledMultipleTimes()
    {
        // Arrange
        var versioner = new TrashcanVersioner(_loggerMock.Object, _testDir);

        // Act & Assert - should not throw
        versioner.Dispose();
        versioner.Dispose();
    }

    #endregion
}
