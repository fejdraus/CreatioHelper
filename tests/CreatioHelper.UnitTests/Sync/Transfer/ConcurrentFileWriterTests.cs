using CreatioHelper.Infrastructure.Services.Sync.Transfer;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.UnitTests.Sync.Transfer;

public class ConcurrentFileWriterTests : IAsyncDisposable
{
    private readonly Mock<ILogger<ConcurrentFileWriter>> _loggerMock;
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;
    private readonly string _testDir;

    public ConcurrentFileWriterTests()
    {
        _loggerMock = new Mock<ILogger<ConcurrentFileWriter>>();
        _loggerFactoryMock = new Mock<ILoggerFactory>();
        _loggerFactoryMock.Setup(x => x.CreateLogger(It.IsAny<string>()))
            .Returns(_loggerMock.Object);
        _testDir = Path.Combine(Path.GetTempPath(), $"concurrent_writer_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Delay(100); // Allow file handles to be released
        if (Directory.Exists(_testDir))
        {
            try
            {
                Directory.Delete(_testDir, true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public async Task WriteBlockAsync_WritesDataAtOffset()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "test_write.bin");
        var fileSize = 1024L;
        await using var writer = new ConcurrentFileWriter(_loggerMock.Object, filePath, fileSize);

        var data = new byte[256];
        new Random(42).NextBytes(data);

        // Act
        var success = await writer.WriteBlockAsync(0, data);

        // Assert
        Assert.True(success);
        Assert.True(writer.IsBlockWritten(0));
    }

    [Fact]
    public async Task WriteBlockAsync_MultipleBlocks_WritesAllBlocks()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "test_multi.bin");
        var fileSize = 1024L;
        await using var writer = new ConcurrentFileWriter(_loggerMock.Object, filePath, fileSize);

        var block1 = new byte[256];
        var block2 = new byte[256];
        new Random(1).NextBytes(block1);
        new Random(2).NextBytes(block2);

        // Act
        var success1 = await writer.WriteBlockAsync(0, block1);
        var success2 = await writer.WriteBlockAsync(256, block2);

        // Assert
        Assert.True(success1);
        Assert.True(success2);
        Assert.True(writer.IsBlockWritten(0));
        Assert.True(writer.IsBlockWritten(256));
    }

    [Fact]
    public async Task WriteBlockAsync_DuplicateBlock_IgnoresDuplicate()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "test_dup.bin");
        var fileSize = 1024L;
        await using var writer = new ConcurrentFileWriter(_loggerMock.Object, filePath, fileSize);

        var data = new byte[256];

        // Act
        await writer.WriteBlockAsync(0, data);
        var success = await writer.WriteBlockAsync(0, data); // Duplicate

        // Assert
        Assert.True(success); // Should succeed silently
    }

    [Fact]
    public async Task WriteBlockAsync_InvalidOffset_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "test_invalid.bin");
        var fileSize = 1024L;
        await using var writer = new ConcurrentFileWriter(_loggerMock.Object, filePath, fileSize);

        var data = new byte[256];

        // Act
        var success = await writer.WriteBlockAsync(-1, data);

        // Assert
        Assert.False(success);
    }

    [Fact]
    public async Task WriteBlockAsync_ExceedsFileSize_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "test_exceed.bin");
        var fileSize = 512L;
        await using var writer = new ConcurrentFileWriter(_loggerMock.Object, filePath, fileSize);

        var data = new byte[256];

        // Act
        var success = await writer.WriteBlockAsync(512, data); // Would exceed file size

        // Assert
        Assert.False(success);
    }

    [Fact]
    public async Task IsBlockWritten_UnwrittenBlock_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "test_unwritten.bin");
        await using var writer = new ConcurrentFileWriter(_loggerMock.Object, filePath, 1024);

        // Assert
        Assert.False(writer.IsBlockWritten(0));
        Assert.False(writer.IsBlockWritten(256));
    }

    [Fact]
    public async Task GetMissingBlockOffsets_ReturnsUnwrittenOffsets()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "test_missing.bin");
        var fileSize = 1024L;
        var blockSize = 256;
        await using var writer = new ConcurrentFileWriter(_loggerMock.Object, filePath, fileSize);

        await writer.WriteBlockAsync(0, new byte[blockSize]);
        await writer.WriteBlockAsync(512, new byte[blockSize]);

        // Act
        var missing = writer.GetMissingBlockOffsets(blockSize);

        // Assert
        Assert.Equal(2, missing.Count);
        Assert.Contains(256L, missing);
        Assert.Contains(768L, missing);
    }

    [Fact]
    public async Task GetStatus_ReturnsCorrectStatus()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "test_status.bin");
        var fileSize = 1024L;
        await using var writer = new ConcurrentFileWriter(_loggerMock.Object, filePath, fileSize);

        await writer.WriteBlockAsync(0, new byte[512]);

        // Act
        var status = writer.GetStatus();

        // Assert
        Assert.Equal(filePath, status.FilePath);
        Assert.Equal(fileSize, status.FileSize);
        Assert.Equal(512, status.BytesWritten);
        Assert.Equal(1, status.BlocksWritten);
        Assert.False(status.IsComplete);
    }

    [Fact]
    public async Task GetStatus_WhenComplete_IsCompleteTrue()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "test_complete.bin");
        var fileSize = 512L;
        await using var writer = new ConcurrentFileWriter(_loggerMock.Object, filePath, fileSize);

        await writer.WriteBlockAsync(0, new byte[512]);

        // Act
        var status = writer.GetStatus();

        // Assert
        Assert.True(status.IsComplete);
        Assert.Equal(100, status.ProgressPercent);
    }

    [Fact]
    public async Task FinalizeAsync_WhenComplete_ReturnsTrue()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "test_finalize.bin");
        var fileSize = 256L;
        await using var writer = new ConcurrentFileWriter(_loggerMock.Object, filePath, fileSize);

        await writer.WriteBlockAsync(0, new byte[256]);

        // Act
        var success = await writer.FinalizeAsync();

        // Assert
        Assert.True(success);
    }

    [Fact]
    public async Task FinalizeAsync_WhenIncomplete_ReturnsFalse()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "test_incomplete.bin");
        var fileSize = 512L;
        await using var writer = new ConcurrentFileWriter(_loggerMock.Object, filePath, fileSize);

        await writer.WriteBlockAsync(0, new byte[256]); // Only half written

        // Act
        var success = await writer.FinalizeAsync();

        // Assert
        Assert.False(success);
    }

    [Fact]
    public async Task ConcurrentWrites_AllBlocksWritten()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "test_concurrent.bin");
        var fileSize = 4096L;
        var blockSize = 256;
        await using var writer = new ConcurrentFileWriter(_loggerMock.Object, filePath, fileSize);

        // Act - Simulate concurrent writes
        var tasks = new List<Task<bool>>();
        for (long offset = 0; offset < fileSize; offset += blockSize)
        {
            var o = offset;
            tasks.Add(Task.Run(() => writer.WriteBlockAsync(o, new byte[blockSize])));
        }
        var results = await Task.WhenAll(tasks);

        // Assert
        Assert.All(results, r => Assert.True(r));
        var status = writer.GetStatus();
        Assert.True(status.IsComplete);
        Assert.Equal(fileSize, status.BytesWritten);
    }

    [Fact]
    public void ConcurrentFileWriterOptions_DefaultValues()
    {
        // Arrange
        var options = new ConcurrentFileWriterOptions();

        // Assert
        Assert.Equal(64 * 1024, options.BufferSize);
        Assert.True(options.PreAllocate);
        Assert.False(options.FlushAfterWrite);
        Assert.True(options.TruncateOnIncomplete);
    }

    [Fact]
    public void ConcurrentFileWriterFactory_CreatesSingletonPerPath()
    {
        // Arrange
        var factory = new ConcurrentFileWriterFactory(_loggerFactoryMock.Object);
        var filePath = Path.Combine(_testDir, "factory_test.bin");

        // Act
        var writer1 = factory.GetOrCreate(filePath, 1024);
        var writer2 = factory.GetOrCreate(filePath, 1024);

        // Assert
        Assert.Same(writer1, writer2);
    }

    [Fact]
    public void ConcurrentFileWriterFactory_Remove_RemovesWriter()
    {
        // Arrange
        var factory = new ConcurrentFileWriterFactory(_loggerFactoryMock.Object);
        var filePath = Path.Combine(_testDir, "factory_remove.bin");
        factory.GetOrCreate(filePath, 1024);

        // Act
        var removed = factory.Remove(filePath);
        var writers = factory.GetActiveWriters();

        // Assert
        Assert.True(removed);
        Assert.DoesNotContain(filePath, writers.Keys);
    }

    [Fact]
    public void PendingBlock_RecordProperties()
    {
        // Arrange
        var block = new PendingBlock
        {
            Offset = 1024,
            Data = new byte[] { 1, 2, 3 },
            Hash = new byte[] { 4, 5, 6 },
            ReceivedAt = DateTime.UtcNow,
            SourceDevice = "device-123"
        };

        // Assert
        Assert.Equal(1024, block.Offset);
        Assert.Equal(3, block.Data.Length);
        Assert.Equal(3, block.Hash.Length);
        Assert.Equal("device-123", block.SourceDevice);
    }

    [Fact]
    public void ConcurrentWriteStatus_ProgressCalculation()
    {
        // Arrange
        var status = new ConcurrentWriteStatus
        {
            FileSize = 1000,
            BytesWritten = 500
        };

        // Assert
        Assert.Equal(50, status.ProgressPercent);
        Assert.False(status.IsComplete);
    }

    [Fact]
    public void ConcurrentWriteStatus_IsComplete_WhenFullyWritten()
    {
        // Arrange
        var status = new ConcurrentWriteStatus
        {
            FileSize = 1000,
            BytesWritten = 1000
        };

        // Assert
        Assert.True(status.IsComplete);
        Assert.Equal(100, status.ProgressPercent);
    }
}
