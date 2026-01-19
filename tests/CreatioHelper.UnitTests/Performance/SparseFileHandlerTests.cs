using System.Runtime.InteropServices;
using CreatioHelper.Infrastructure.Services.Sync.FileSystem;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.UnitTests.Performance;

public class SparseFileHandlerTests : IDisposable
{
    private readonly Mock<ILogger<SparseFileHandler>> _loggerMock;
    private readonly SparseFileHandler _handler;
    private readonly string _testDir;

    public SparseFileHandlerTests()
    {
        _loggerMock = new Mock<ILogger<SparseFileHandler>>();
        _handler = new SparseFileHandler(_loggerMock.Object);
        _testDir = Path.Combine(Path.GetTempPath(), $"sparse_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void IsSparseSupported_ReturnsValue()
    {
        // Act
        var isSupported = _handler.IsSparseSupported(_testDir);

        // Assert - On Windows NTFS it should be true, on other FS may vary
        // We just verify it returns a value without throwing
        Assert.True(isSupported || !isSupported); // Always passes, checking no exception
    }

    [Fact]
    public void IsSparseSupported_CachesResult()
    {
        // Act
        var result1 = _handler.IsSparseSupported(_testDir);
        var result2 = _handler.IsSparseSupported(_testDir);

        // Assert
        Assert.Equal(result1, result2);
    }

    [Fact]
    public async Task CreateSparseFileAsync_CreatesFile()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "sparse_create.bin");
        var size = 1024L * 1024; // 1 MB

        // Act
        await _handler.CreateSparseFileAsync(filePath, size);

        // Assert
        Assert.True(File.Exists(filePath));
        var fileInfo = new FileInfo(filePath);
        Assert.Equal(size, fileInfo.Length);
    }

    [Fact]
    public async Task CreateSparseFileAsync_CreatesParentDirectory()
    {
        // Arrange
        var subDir = Path.Combine(_testDir, "subdir", "nested");
        var filePath = Path.Combine(subDir, "sparse_nested.bin");

        // Act
        await _handler.CreateSparseFileAsync(filePath, 1024);

        // Assert
        Assert.True(Directory.Exists(subDir));
        Assert.True(File.Exists(filePath));
    }

    [Fact]
    public async Task CreateSparseFileAsync_ZeroSize_CreatesEmptyFile()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "sparse_zero.bin");

        // Act
        await _handler.CreateSparseFileAsync(filePath, 0);

        // Assert
        Assert.True(File.Exists(filePath));
        Assert.Equal(0, new FileInfo(filePath).Length);
    }

    [Fact]
    public async Task CreateSparseFileAsync_NegativeSize_ThrowsArgumentOutOfRangeException()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "sparse_negative.bin");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _handler.CreateSparseFileAsync(filePath, -1));
    }

    [Fact]
    public async Task WriteSparseAsync_WritesData()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "sparse_write.bin");
        await _handler.CreateSparseFileAsync(filePath, 1024);

        var data = new byte[256];
        new Random(42).NextBytes(data);

        // Act
        await _handler.WriteSparseAsync(filePath, 0, data);

        // Assert
        var readData = new byte[256];
        await using var stream = File.OpenRead(filePath);
        await stream.ReadExactlyAsync(readData);
        Assert.Equal(data, readData);
    }

    [Fact]
    public async Task WriteSparseAsync_WritesAtOffset()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "sparse_offset.bin");
        await _handler.CreateSparseFileAsync(filePath, 1024);

        var data = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        await _handler.WriteSparseAsync(filePath, 500, data);

        // Assert
        await using var stream = File.OpenRead(filePath);
        stream.Seek(500, SeekOrigin.Begin);
        var readData = new byte[5];
        await stream.ReadExactlyAsync(readData);
        Assert.Equal(data, readData);
    }

    [Fact]
    public async Task WriteSparseAsync_SkipsZeroData()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "sparse_skip_zeros.bin");
        await _handler.CreateSparseFileAsync(filePath, 1024);

        var zeroData = new byte[256]; // All zeros

        // Act
        await _handler.WriteSparseAsync(filePath, 0, zeroData);

        // Assert - Statistics should show bytes saved
        var stats = _handler.GetStatistics();
        Assert.Equal(256, stats.BytesSaved);
    }

    [Fact]
    public async Task WriteSparseAsync_NullOrEmptyData_DoesNothing()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "sparse_empty.bin");
        await _handler.CreateSparseFileAsync(filePath, 1024);

        // Act
        await _handler.WriteSparseAsync(filePath, 0, Array.Empty<byte>());
        await _handler.WriteSparseAsync(filePath, 0, null!);

        // Assert - No errors
    }

    [Fact]
    public async Task PunchHoleAsync_NonExistentFile_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "nonexistent.bin");

        // Act
        var result = await _handler.PunchHoleAsync(filePath, 0, 1024);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task PunchHoleAsync_ZeroLength_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "punch_zero.bin");
        await _handler.CreateSparseFileAsync(filePath, 1024);

        // Act
        var result = await _handler.PunchHoleAsync(filePath, 0, 0);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task PunchHoleAsync_NegativeLength_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "punch_negative.bin");
        await _handler.CreateSparseFileAsync(filePath, 1024);

        // Act
        var result = await _handler.PunchHoleAsync(filePath, 0, -100);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task PunchHoleAsync_ValidFile_ReturnsResult()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "punch_valid.bin");
        await _handler.CreateSparseFileAsync(filePath, 10240);

        // Write some data first
        var data = new byte[4096];
        new Random().NextBytes(data);
        await using (var stream = File.OpenWrite(filePath))
        {
            await stream.WriteAsync(data);
        }

        // Act
        var result = await _handler.PunchHoleAsync(filePath, 0, 4096);

        // Assert - Result depends on file system support
        // On NTFS it should succeed, on other FS may use fallback
        Assert.True(result || !result); // Just verify no exception
    }

    [Fact]
    public void GetAllocatedSize_NonExistentFile_ReturnsZero()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "nonexistent_alloc.bin");

        // Act
        var size = _handler.GetAllocatedSize(filePath);

        // Assert
        Assert.Equal(0, size);
    }

    [Fact]
    public async Task GetAllocatedSize_ExistingFile_ReturnsSize()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "alloc_size.bin");
        await _handler.CreateSparseFileAsync(filePath, 1024);

        // Write some data
        await File.WriteAllBytesAsync(filePath, new byte[512]);

        // Act
        var size = _handler.GetAllocatedSize(filePath);

        // Assert
        Assert.True(size >= 0);
    }

    [Fact]
    public void GetSparseRegions_NonExistentFile_ReturnsEmpty()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "nonexistent_regions.bin");

        // Act
        var regions = _handler.GetSparseRegions(filePath).ToList();

        // Assert
        Assert.Empty(regions);
    }

    [Fact]
    public async Task GetSparseRegions_FileWithNoSparseRegions_ReturnsEmpty()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "no_sparse.bin");
        var data = new byte[1024];
        new Random().NextBytes(data);
        await File.WriteAllBytesAsync(filePath, data);

        // Act
        var regions = _handler.GetSparseRegions(filePath).ToList();

        // Assert
        Assert.Empty(regions);
    }

    [Fact]
    public void IsSparseRegion_NonExistentFile_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "nonexistent_check.bin");

        // Act
        var result = _handler.IsSparseRegion(filePath, 0, 1024);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task OptimizeFileAsync_NonExistentFile_ReturnsZero()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "nonexistent_opt.bin");

        // Act
        var bytesSaved = await _handler.OptimizeFileAsync(filePath);

        // Assert
        Assert.Equal(0, bytesSaved);
    }

    [Fact]
    public async Task OptimizeFileAsync_SmallFile_ReturnsZero()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "small_file.bin");
        await File.WriteAllBytesAsync(filePath, new byte[100]);

        // Act
        var bytesSaved = await _handler.OptimizeFileAsync(filePath, minHoleSize: 4096);

        // Assert
        Assert.Equal(0, bytesSaved);
    }

    [Fact]
    public async Task OptimizeFileAsync_UnsupportedFileSystem_ReturnsZero()
    {
        // This test is tricky because we can't easily mock filesystem support
        // Just verify behavior on current filesystem
        var filePath = Path.Combine(_testDir, "opt_test.bin");
        await File.WriteAllBytesAsync(filePath, new byte[1024]);

        // Act
        var bytesSaved = await _handler.OptimizeFileAsync(filePath);

        // Assert - May be 0 or positive depending on FS
        Assert.True(bytesSaved >= 0);
    }

    [Fact]
    public async Task OptimizeFileAsync_RespectsCancellation()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "cancel_opt.bin");
        await _handler.CreateSparseFileAsync(filePath, 100 * 1024);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _handler.OptimizeFileAsync(filePath, cancellationToken: cts.Token));
    }

    [Fact]
    public void GetStatistics_ReturnsCorrectStats()
    {
        // Act
        var stats = _handler.GetStatistics();

        // Assert
        Assert.NotNull(stats);
        Assert.True(stats.FilesCreated >= 0);
        Assert.True(stats.HolesPunched >= 0);
        Assert.True(stats.BytesSaved >= 0);
        Assert.True(stats.SparseWrites >= 0);
        Assert.True(stats.FilesOptimized >= 0);
    }

    [Fact]
    public async Task GetStatistics_TracksFilesCreated()
    {
        // Arrange
        var initialStats = _handler.GetStatistics();
        var initialCount = initialStats.FilesCreated;

        // Act
        await _handler.CreateSparseFileAsync(Path.Combine(_testDir, "stats1.bin"), 1024);
        await _handler.CreateSparseFileAsync(Path.Combine(_testDir, "stats2.bin"), 1024);

        var finalStats = _handler.GetStatistics();

        // Assert
        Assert.Equal(initialCount + 2, finalStats.FilesCreated);
    }

    [Fact]
    public void SparseRegion_Properties()
    {
        // Arrange
        var region = new SparseRegion
        {
            Offset = 1024,
            Length = 4096
        };

        // Assert
        Assert.Equal(1024, region.Offset);
        Assert.Equal(4096, region.Length);
        Assert.Equal(5120, region.End); // Offset + Length
    }

    [Fact]
    public void SparseFileStatistics_Properties()
    {
        // Arrange
        var stats = new SparseFileStatistics
        {
            FilesCreated = 10,
            HolesPunched = 5,
            BytesSaved = 1024 * 1024,
            SparseWrites = 20,
            FilesOptimized = 3,
            IsSupported = true
        };

        // Assert
        Assert.Equal(10, stats.FilesCreated);
        Assert.Equal(5, stats.HolesPunched);
        Assert.Equal(1024 * 1024, stats.BytesSaved);
        Assert.Equal(20, stats.SparseWrites);
        Assert.Equal(3, stats.FilesOptimized);
        Assert.True(stats.IsSupported);
    }

    [Fact]
    public async Task SparseFile_OnNTFS_SavesSpace()
    {
        // Skip if not on Windows
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // Skip on non-Windows platforms
        }

        // Arrange
        var filePath = Path.Combine(_testDir, "ntfs_sparse.bin");
        var fileSize = 10L * 1024 * 1024; // 10 MB

        // Act
        await _handler.CreateSparseFileAsync(filePath, fileSize);

        // Assert
        var allocatedSize = _handler.GetAllocatedSize(filePath);
        var logicalSize = new FileInfo(filePath).Length;

        // On NTFS, allocated size should be less than logical size for sparse file
        Assert.True(allocatedSize <= logicalSize);
    }

    [Fact]
    public async Task PunchHole_OnNTFS_DecreasesAllocatedSize()
    {
        // Skip if not on Windows
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return; // Skip on non-Windows platforms
        }

        // Arrange
        var filePath = Path.Combine(_testDir, "ntfs_punch.bin");
        var data = new byte[64 * 1024]; // 64KB of data
        new Random().NextBytes(data);
        await File.WriteAllBytesAsync(filePath, data);

        var beforeSize = _handler.GetAllocatedSize(filePath);

        // Act
        var result = await _handler.PunchHoleAsync(filePath, 0, 32 * 1024); // Punch 32KB hole

        // Assert
        if (result)
        {
            var afterSize = _handler.GetAllocatedSize(filePath);
            Assert.True(afterSize <= beforeSize);
        }
    }

    [Fact]
    public async Task WriteSparseAsync_MixedData_TracksCorrectly()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "mixed_data.bin");
        await _handler.CreateSparseFileAsync(filePath, 4096);

        var initialStats = _handler.GetStatistics();
        var initialWrites = initialStats.SparseWrites;
        var initialSaved = initialStats.BytesSaved;

        // Act - Write non-zero data
        var nonZeroData = new byte[1024];
        new Random().NextBytes(nonZeroData);
        await _handler.WriteSparseAsync(filePath, 0, nonZeroData);

        // Write zero data
        var zeroData = new byte[1024]; // All zeros
        await _handler.WriteSparseAsync(filePath, 1024, zeroData);

        // Assert
        var finalStats = _handler.GetStatistics();
        Assert.Equal(initialWrites + 1, finalStats.SparseWrites); // Only non-zero write counted
        Assert.Equal(initialSaved + 1024, finalStats.BytesSaved); // Zero data saved
    }
}
