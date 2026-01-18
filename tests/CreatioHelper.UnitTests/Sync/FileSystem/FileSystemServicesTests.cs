using CreatioHelper.Infrastructure.Services.Sync.FileSystem;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.UnitTests.Sync.FileSystem;

public class CaseSensitivityWrapperTests : IDisposable
{
    private readonly Mock<ILogger<CaseSensitiveFileSystem>> _loggerMock;
    private readonly string _testDir;

    public CaseSensitivityWrapperTests()
    {
        _loggerMock = new Mock<ILogger<CaseSensitiveFileSystem>>();
        _testDir = Path.Combine(Path.GetTempPath(), $"casefs_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    public void CaseSensitivity_Auto_DefaultValue()
    {
        // Assert
        Assert.Equal(CaseSensitivity.Auto, default(CaseSensitivity));
    }

    [Theory]
    [InlineData(CaseSensitivity.Auto)]
    [InlineData(CaseSensitivity.ForceCase)]
    [InlineData(CaseSensitivity.IgnoreCase)]
    public void CaseSensitivity_AllValuesValid(CaseSensitivity sensitivity)
    {
        // Assert
        Assert.True(Enum.IsDefined(typeof(CaseSensitivity), sensitivity));
    }

    [Fact]
    public void Constructor_AutoMode_DeterminesPlatformSensitivity()
    {
        // Arrange & Act
        var fs = new CaseSensitiveFileSystem(_loggerMock.Object, _testDir, CaseSensitivity.Auto);

        // Assert
        Assert.Equal(CaseSensitivity.Auto, fs.Mode);
    }

    [Fact]
    public void Constructor_ForceCaseMode_SetsCaseSensitive()
    {
        // Arrange & Act
        var fs = new CaseSensitiveFileSystem(_loggerMock.Object, _testDir, CaseSensitivity.ForceCase);

        // Assert
        Assert.True(fs.IsCaseSensitive);
    }

    [Fact]
    public void Constructor_IgnoreCaseMode_SetsCaseInsensitive()
    {
        // Arrange & Act
        var fs = new CaseSensitiveFileSystem(_loggerMock.Object, _testDir, CaseSensitivity.IgnoreCase);

        // Assert
        Assert.False(fs.IsCaseSensitive);
    }

    [Fact]
    public void NormalizePath_CaseInsensitive_LowercasesPath()
    {
        // Arrange
        var fs = new CaseSensitiveFileSystem(_loggerMock.Object, _testDir, CaseSensitivity.IgnoreCase);

        // Act
        var result = fs.NormalizePath("Documents/MyFile.TXT");

        // Assert
        Assert.Equal("documents/myfile.txt", result);
    }

    [Fact]
    public void NormalizePath_CaseSensitive_PreservesCase()
    {
        // Arrange
        var fs = new CaseSensitiveFileSystem(_loggerMock.Object, _testDir, CaseSensitivity.ForceCase);

        // Act
        var result = fs.NormalizePath("Documents/MyFile.TXT");

        // Assert
        Assert.Equal("Documents/MyFile.TXT", result);
    }

    [Fact]
    public void PathEquals_CaseInsensitive_MatchesDifferentCases()
    {
        // Arrange
        var fs = new CaseSensitiveFileSystem(_loggerMock.Object, _testDir, CaseSensitivity.IgnoreCase);

        // Act
        var result = fs.PathEquals("Documents/File.txt", "documents/file.TXT");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void PathEquals_CaseSensitive_RequiresExactMatch()
    {
        // Arrange
        var fs = new CaseSensitiveFileSystem(_loggerMock.Object, _testDir, CaseSensitivity.ForceCase);

        // Act
        var sameCase = fs.PathEquals("Documents/File.txt", "Documents/File.txt");
        var diffCase = fs.PathEquals("Documents/File.txt", "documents/file.TXT");

        // Assert
        Assert.True(sameCase);
        Assert.False(diffCase);
    }

    [Fact]
    public async Task FileExistsExactAsync_ExistingFile_ReturnsTrue()
    {
        // Arrange
        var fs = new CaseSensitiveFileSystem(_loggerMock.Object, _testDir, CaseSensitivity.ForceCase);
        var fileName = "TestFile.txt";
        await File.WriteAllTextAsync(Path.Combine(_testDir, fileName), "content");

        // Act - use relative path
        var result = await fs.FileExistsExactAsync(fileName);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task FileExistsExactAsync_NonExistent_ReturnsFalse()
    {
        // Arrange
        var fs = new CaseSensitiveFileSystem(_loggerMock.Object, _testDir, CaseSensitivity.ForceCase);

        // Act - use relative path
        var result = await fs.FileExistsExactAsync("NonExistent.txt");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task GetActualPathAsync_ExistingFile_ReturnsActualPath()
    {
        // Arrange
        var fs = new CaseSensitiveFileSystem(_loggerMock.Object, _testDir, CaseSensitivity.IgnoreCase);
        var fileName = "ActualFile.txt";
        await File.WriteAllTextAsync(Path.Combine(_testDir, fileName), "content");

        // Act - use relative path
        var result = await fs.GetActualPathAsync(fileName);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("ActualFile.txt", result);
    }

    [Fact]
    public async Task GetActualPathAsync_NonExistent_ReturnsNull()
    {
        // Arrange
        var fs = new CaseSensitiveFileSystem(_loggerMock.Object, _testDir, CaseSensitivity.IgnoreCase);

        // Act - use relative path
        var result = await fs.GetActualPathAsync("nonexistent.txt");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task DetectCaseConflictsAsync_NoConflicts_ReturnsEmpty()
    {
        // Arrange
        var fs = new CaseSensitiveFileSystem(_loggerMock.Object, _testDir, CaseSensitivity.IgnoreCase);
        await File.WriteAllTextAsync(Path.Combine(_testDir, "file1.txt"), "content");
        await File.WriteAllTextAsync(Path.Combine(_testDir, "file2.txt"), "content");

        // Act
        var conflicts = await fs.DetectCaseConflictsAsync(_testDir);

        // Assert
        Assert.Empty(conflicts);
    }

    [Fact]
    public void CaseConflict_RecordProperties()
    {
        // Arrange
        var conflict = new CaseConflict("File.txt", "FILE.TXT", "file.txt");

        // Assert
        Assert.Equal("File.txt", conflict.Path1);
        Assert.Equal("FILE.TXT", conflict.Path2);
        Assert.Equal("file.txt", conflict.NormalizedPath);
    }

    [Fact]
    public void CaseSensitiveFileSystemFactory_CreatesFileSystem()
    {
        // Arrange
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory.Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(_loggerMock.Object);
        var factory = new CaseSensitiveFileSystemFactory(loggerFactory.Object);

        // Act
        var fs = factory.Create(_testDir, CaseSensitivity.Auto);

        // Assert
        Assert.NotNull(fs);
        Assert.Equal(CaseSensitivity.Auto, fs.Mode);
    }
}

public class TempFileHandlerTests : IDisposable
{
    private readonly Mock<ILogger<TempFileHandler>> _loggerMock;
    private readonly TempFileHandler _handler;
    private readonly string _testDir;

    public TempFileHandlerTests()
    {
        _loggerMock = new Mock<ILogger<TempFileHandler>>();
        _testDir = Path.Combine(Path.GetTempPath(), $"tempfile_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _handler = new TempFileHandler(_loggerMock.Object, _testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void IsTempFile_SyncthingTempFile_ReturnsTrue()
    {
        // Arrange
        var tempFile = ".syncthing.test.txt.tmp";

        // Act
        var result = _handler.IsTempFile(tempFile);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void IsTempFile_RegularFile_ReturnsFalse()
    {
        // Arrange
        var regularFile = "document.txt";

        // Act
        var result = _handler.IsTempFile(regularFile);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void TempPrefix_ReturnsSyncthingPrefix()
    {
        // Assert
        Assert.Equal(".syncthing.", _handler.TempPrefix);
    }

    [Fact]
    public void GetTempFilePath_CreatesValidTempPath()
    {
        // Arrange
        var originalPath = Path.Combine(_testDir, "subdir", "file.txt");

        // Act
        var tempPath = _handler.GetTempFilePath(originalPath);

        // Assert
        Assert.Contains(".syncthing.", tempPath);
        Assert.EndsWith(".tmp", tempPath);
    }

    [Fact]
    public void GetTempFilePath_IncrementsActiveCount()
    {
        // Arrange
        var originalPath = Path.Combine(_testDir, "file.txt");
        var initialCount = _handler.ActiveTempFileCount;

        // Act
        _handler.GetTempFilePath(originalPath);

        // Assert
        Assert.Equal(initialCount + 1, _handler.ActiveTempFileCount);
    }

    [Fact]
    public async Task FinalizeTempFileAsync_MovesToTarget()
    {
        // Arrange
        var targetPath = Path.Combine(_testDir, "target.txt");
        var tempPath = _handler.GetTempFilePath(targetPath);
        await File.WriteAllTextAsync(tempPath, "test content");

        // Act
        var success = await _handler.FinalizeTempFileAsync(tempPath, targetPath);

        // Assert
        Assert.True(success);
        Assert.True(File.Exists(targetPath));
        Assert.False(File.Exists(tempPath));
        Assert.Equal("test content", await File.ReadAllTextAsync(targetPath));
    }

    [Fact]
    public async Task FinalizeTempFileAsync_NonExistentTempFile_ReturnsFalse()
    {
        // Arrange
        var tempPath = Path.Combine(_testDir, ".syncthing.nonexistent.tmp");
        var targetPath = Path.Combine(_testDir, "target.txt");

        // Act
        var success = await _handler.FinalizeTempFileAsync(tempPath, targetPath);

        // Assert
        Assert.False(success);
    }

    [Fact]
    public async Task CleanupTempFileAsync_RemovesTempFile()
    {
        // Arrange
        var targetPath = Path.Combine(_testDir, "file.txt");
        var tempPath = _handler.GetTempFilePath(targetPath);
        await File.WriteAllTextAsync(tempPath, "test");

        // Act
        await _handler.CleanupTempFileAsync(tempPath);

        // Assert
        Assert.False(File.Exists(tempPath));
    }

    [Fact]
    public async Task CleanupOrphanedTempFilesAsync_RemovesOldTempFiles()
    {
        // Arrange
        var tempFile1 = Path.Combine(_testDir, ".syncthing.old.tmp");
        var tempFile2 = Path.Combine(_testDir, ".syncthing.new.tmp");
        File.WriteAllText(tempFile1, "old");
        File.WriteAllText(tempFile2, "new");
        File.SetLastWriteTimeUtc(tempFile1, DateTime.UtcNow.AddHours(-25));

        // Act
        var count = await _handler.CleanupOrphanedTempFilesAsync(_testDir, TimeSpan.FromHours(24));

        // Assert
        Assert.Equal(1, count);
        Assert.False(File.Exists(tempFile1));
        Assert.True(File.Exists(tempFile2));
    }

    [Fact]
    public void TempFileOptions_DefaultValues()
    {
        // Arrange
        var options = new TempFileOptions();

        // Assert
        Assert.Equal(".syncthing.", options.TempPrefix);
        Assert.Equal(".tmp", options.TempSuffix);
        Assert.True(options.UseRandomSuffix);
        Assert.False(options.BackupOnOverwrite);
    }
}

public class FolderMarkerServiceTests : IDisposable
{
    private readonly Mock<ILogger<FolderMarkerService>> _loggerMock;
    private readonly FolderMarkerService _service;
    private readonly string _testDir;

    public FolderMarkerServiceTests()
    {
        _loggerMock = new Mock<ILogger<FolderMarkerService>>();
        _service = new FolderMarkerService(_loggerMock.Object);
        _testDir = Path.Combine(Path.GetTempPath(), $"marker_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void DefaultMarkerName_ReturnsStfolder()
    {
        // Assert
        Assert.Equal(".stfolder", _service.DefaultMarkerName);
    }

    [Fact]
    public async Task EnsureMarkerExistsAsync_CreatesStfolderDirectory()
    {
        // Act
        var result = await _service.EnsureMarkerExistsAsync(_testDir);

        // Assert
        Assert.True(result);
        var markerPath = Path.Combine(_testDir, ".stfolder");
        Assert.True(Directory.Exists(markerPath));
    }

    [Fact]
    public async Task EnsureMarkerExistsAsync_AlreadyExists_ReturnsTrue()
    {
        // Arrange
        await _service.EnsureMarkerExistsAsync(_testDir);

        // Act
        var result = await _service.EnsureMarkerExistsAsync(_testDir);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task EnsureMarkerExistsAsync_NonExistentFolder_ReturnsFalse()
    {
        // Act
        var result = await _service.EnsureMarkerExistsAsync(Path.Combine(_testDir, "nonexistent"));

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task MarkerExistsAsync_WithMarker_ReturnsTrue()
    {
        // Arrange
        await _service.EnsureMarkerExistsAsync(_testDir);

        // Act
        var hasMarker = await _service.MarkerExistsAsync(_testDir);

        // Assert
        Assert.True(hasMarker);
    }

    [Fact]
    public async Task MarkerExistsAsync_WithoutMarker_ReturnsFalse()
    {
        // Act
        var hasMarker = await _service.MarkerExistsAsync(_testDir);

        // Assert
        Assert.False(hasMarker);
    }

    [Fact]
    public async Task RemoveMarkerAsync_RemovesMarker()
    {
        // Arrange
        await _service.EnsureMarkerExistsAsync(_testDir);

        // Act
        var result = await _service.RemoveMarkerAsync(_testDir);

        // Assert
        Assert.True(result);
        var markerPath = Path.Combine(_testDir, ".stfolder");
        Assert.False(Directory.Exists(markerPath));
    }

    [Fact]
    public async Task IsSyncFolderAsync_WithMarker_ReturnsTrue()
    {
        // Arrange
        await _service.EnsureMarkerExistsAsync(_testDir);

        // Act
        var isValid = await _service.IsSyncFolderAsync(_testDir);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public async Task IsSyncFolderAsync_WithoutMarker_ReturnsFalse()
    {
        // Act
        var isValid = await _service.IsSyncFolderAsync(_testDir);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public async Task IsSyncFolderAsync_NonExistentFolder_ReturnsFalse()
    {
        // Act
        var isValid = await _service.IsSyncFolderAsync(Path.Combine(_testDir, "nonexistent"));

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public async Task GetMarkerInfoAsync_WithMarker_ReturnsInfo()
    {
        // Arrange
        await _service.EnsureMarkerExistsAsync(_testDir);

        // Act
        var info = await _service.GetMarkerInfoAsync(_testDir);

        // Assert
        Assert.NotNull(info);
        Assert.True(info.Exists);
        Assert.Equal(".stfolder", info.MarkerName);
        Assert.True(info.IsDirectory);
    }

    [Fact]
    public async Task GetMarkerInfoAsync_WithoutMarker_ReturnsNotExists()
    {
        // Act
        var info = await _service.GetMarkerInfoAsync(_testDir);

        // Assert
        Assert.NotNull(info);
        Assert.False(info.Exists);
    }

    [Fact]
    public async Task EnsureMarkerExistsAsync_CustomMarkerName()
    {
        // Act - Using a non-.st* name to trigger file creation
        var result = await _service.EnsureMarkerExistsAsync(_testDir, ".folder-marker");

        // Assert
        Assert.True(result);
        Assert.True(File.Exists(Path.Combine(_testDir, ".folder-marker")));
    }
}

public class FsyncControllerTests : IDisposable
{
    private readonly Mock<ILogger<FsyncController>> _loggerMock;
    private readonly string _testDir;

    public FsyncControllerTests()
    {
        _loggerMock = new Mock<ILogger<FsyncController>>();
        _testDir = Path.Combine(Path.GetTempPath(), $"fsync_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void FsyncEnabled_DefaultTrue()
    {
        // Arrange
        var controller = new FsyncController(_loggerMock.Object);

        // Assert
        Assert.True(controller.FsyncEnabled);
    }

    [Fact]
    public void FsyncEnabled_CanBeDisabled()
    {
        // Arrange
        var controller = new FsyncController(_loggerMock.Object, fsyncEnabled: false);

        // Assert
        Assert.False(controller.FsyncEnabled);
    }

    [Fact]
    public async Task SyncFileAsync_WhenDisabled_SkipsSync()
    {
        // Arrange
        var controller = new FsyncController(_loggerMock.Object, fsyncEnabled: false);
        var filePath = Path.Combine(_testDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "test");

        // Act
        await controller.SyncFileAsync(filePath);

        // Assert
        var stats = controller.GetStats();
        Assert.Equal(1, stats.SkippedSyncs);
        Assert.Equal(0, stats.TotalSyncs);
    }

    [Fact]
    public async Task SyncFileAsync_WhenEnabled_PerformsSync()
    {
        // Arrange
        var controller = new FsyncController(_loggerMock.Object, fsyncEnabled: true);
        var filePath = Path.Combine(_testDir, "test.txt");
        await File.WriteAllTextAsync(filePath, "test");

        // Act
        await controller.SyncFileAsync(filePath);

        // Assert
        var stats = controller.GetStats();
        Assert.Equal(1, stats.TotalSyncs);
        Assert.Equal(1, stats.SuccessfulSyncs);
    }

    [Fact]
    public async Task WriteFileWithSyncAsync_WritesAndSyncs()
    {
        // Arrange
        var controller = new FsyncController(_loggerMock.Object);
        var filePath = Path.Combine(_testDir, "write_test.txt");
        var data = "test data"u8.ToArray();

        // Act
        await controller.WriteFileWithSyncAsync(filePath, data);

        // Assert
        Assert.True(File.Exists(filePath));
        Assert.Equal("test data", await File.ReadAllTextAsync(filePath));
    }

    [Fact]
    public async Task AtomicWriteAsync_WritesAtomically()
    {
        // Arrange
        var controller = new FsyncController(_loggerMock.Object);
        var filePath = Path.Combine(_testDir, "atomic_test.txt");
        var data = "atomic data"u8.ToArray();

        // Act
        await controller.AtomicWriteAsync(filePath, data);

        // Assert
        Assert.True(File.Exists(filePath));
        Assert.Equal("atomic data", await File.ReadAllTextAsync(filePath));
        Assert.False(File.Exists(filePath + ".tmp"));
    }

    [Fact]
    public void GetStats_ReturnsValidStats()
    {
        // Arrange
        var controller = new FsyncController(_loggerMock.Object);

        // Act
        var stats = controller.GetStats();

        // Assert
        Assert.NotNull(stats);
        Assert.Equal(0, stats.TotalSyncs);
        Assert.Equal(0, stats.SuccessfulSyncs);
        Assert.Equal(0, stats.FailedSyncs);
    }

    [Fact]
    public void FsyncStats_AverageSyncTimeMs_Calculation()
    {
        // Arrange
        var stats = new FsyncStats
        {
            TotalSyncs = 10,
            TotalSyncTime = TimeSpan.FromMilliseconds(100)
        };

        // Assert
        Assert.Equal(10, stats.AverageSyncTimeMs);
    }

    [Fact]
    public void FsyncStats_AverageSyncTimeMs_ZeroSyncs()
    {
        // Arrange
        var stats = new FsyncStats { TotalSyncs = 0 };

        // Assert
        Assert.Equal(0, stats.AverageSyncTimeMs);
    }
}

public class CopyRangeOptimizerTests : IDisposable
{
    private readonly Mock<ILogger<CopyRangeOptimizer>> _loggerMock;
    private readonly CopyRangeOptimizer _optimizer;
    private readonly string _testDir;

    public CopyRangeOptimizerTests()
    {
        _loggerMock = new Mock<ILogger<CopyRangeOptimizer>>();
        _optimizer = new CopyRangeOptimizer(_loggerMock.Object);
        _testDir = Path.Combine(Path.GetTempPath(), $"copyrange_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Theory]
    [InlineData(CopyRangeMethod.Auto)]
    [InlineData(CopyRangeMethod.Standard)]
    [InlineData(CopyRangeMethod.CopyFileRange)]
    [InlineData(CopyRangeMethod.Reflink)]
    public void Constructor_AcceptsAllMethods(CopyRangeMethod method)
    {
        // Act
        var optimizer = new CopyRangeOptimizer(_loggerMock.Object, method);

        // Assert
        Assert.NotNull(optimizer);
    }

    [Fact]
    public void ActiveMethod_ReturnsConfiguredMethod()
    {
        // Arrange
        var optimizer = new CopyRangeOptimizer(_loggerMock.Object, CopyRangeMethod.Standard);

        // Assert
        Assert.Equal(CopyRangeMethod.Standard, optimizer.ActiveMethod);
    }

    [Fact]
    public async Task TryCopyRangeAsync_CopiesData()
    {
        // Arrange
        var srcPath = Path.Combine(_testDir, "source.bin");
        var dstPath = Path.Combine(_testDir, "dest.bin");
        var data = new byte[1024];
        new Random(42).NextBytes(data);
        await File.WriteAllBytesAsync(srcPath, data);

        // Create destination file
        await File.WriteAllBytesAsync(dstPath, new byte[1024]);

        // Act
        var success = await _optimizer.TryCopyRangeAsync(srcPath, dstPath, 0, 0, 512);

        // Assert
        Assert.True(success);
        var dstData = await File.ReadAllBytesAsync(dstPath);
        Assert.Equal(data.Take(512).ToArray(), dstData.Take(512).ToArray());
    }

    [Fact]
    public async Task TryCopyRangeAsync_WithOffset_CopiesCorrectRange()
    {
        // Arrange
        var srcPath = Path.Combine(_testDir, "source_offset.bin");
        var dstPath = Path.Combine(_testDir, "dest_offset.bin");
        var data = new byte[1024];
        for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 256);
        await File.WriteAllBytesAsync(srcPath, data);
        await File.WriteAllBytesAsync(dstPath, new byte[1024]);

        // Act
        var success = await _optimizer.TryCopyRangeAsync(srcPath, dstPath, 256, 512, 256);

        // Assert
        Assert.True(success);
        var dstData = await File.ReadAllBytesAsync(dstPath);
        Assert.Equal(data.Skip(256).Take(256).ToArray(), dstData.Skip(512).Take(256).ToArray());
    }

    [Fact]
    public void GetStats_ReturnsValidStats()
    {
        // Act
        var stats = _optimizer.GetStats();

        // Assert
        Assert.NotNull(stats);
        Assert.Equal(0, stats.TotalCopies);
    }

    [Fact]
    public async Task DetectBestMethodAsync_ReturnsMethod()
    {
        // Act
        var method = await _optimizer.DetectBestMethodAsync(_testDir);

        // Assert
        Assert.True(Enum.IsDefined(typeof(CopyRangeMethod), method));
    }

    [Fact]
    public void CopyRangeStats_OptimizedPercentage()
    {
        // Arrange
        var stats = new CopyRangeStats
        {
            TotalCopies = 100,
            OptimizedCopies = 75
        };

        // Assert
        Assert.Equal(75, stats.OptimizedPercentage);
    }

    [Fact]
    public void CopyRangeStats_OptimizedPercentage_ZeroCopies()
    {
        // Arrange
        var stats = new CopyRangeStats { TotalCopies = 0 };

        // Assert
        Assert.Equal(0, stats.OptimizedPercentage);
    }
}
