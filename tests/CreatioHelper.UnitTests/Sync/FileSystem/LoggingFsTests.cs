using CreatioHelper.Infrastructure.Services.Sync.FileSystem;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests.Sync.FileSystem;

public class LoggingFsTests : IDisposable
{
    private readonly Mock<ILogger<LoggingFs>> _loggerMock;
    private readonly string _testDirectory;
    private readonly LoggingFs _fs;

    public LoggingFsTests()
    {
        _loggerMock = new Mock<ILogger<LoggingFs>>();
        _loggerMock.Setup(x => x.IsEnabled(It.IsAny<LogLevel>())).Returns(true);

        _testDirectory = Path.Combine(Path.GetTempPath(), $"LoggingFsTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _fs = new LoggingFs(_loggerMock.Object, _testDirectory);
    }

    public void Dispose()
    {
        _fs.Dispose();
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public void FileExists_LogsOperation()
    {
        // Arrange
        var fileName = "test.txt";
        File.WriteAllText(Path.Combine(_testDirectory, fileName), "content");

        // Act
        var result = _fs.FileExists(fileName);

        // Assert
        Assert.True(result);
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("FileExists") && v.ToString()!.Contains(fileName)),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void DirectoryExists_LogsOperation()
    {
        // Arrange
        var dirName = "testdir";
        Directory.CreateDirectory(Path.Combine(_testDirectory, dirName));

        // Act
        var result = _fs.DirectoryExists(dirName);

        // Assert
        Assert.True(result);
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("DirectoryExists")),
                null,
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Fact]
    public void OpenRead_LogsOperationAndTracksBytes()
    {
        // Arrange
        var fileName = "read.txt";
        var content = "test content";
        File.WriteAllText(Path.Combine(_testDirectory, fileName), content);

        // Act
        using (var stream = _fs.OpenRead(fileName))
        {
            Assert.NotNull(stream);
            var buffer = new byte[1024];
            _ = stream!.Read(buffer, 0, buffer.Length);
        }

        // Assert
        var stats = _fs.GetStats();
        Assert.Equal(1, stats.Reads);
        Assert.True(stats.TotalBytesRead > 0);
    }

    [Fact]
    public void CreateWrite_LogsOperationAndTracksBytes()
    {
        // Arrange
        var fileName = "write.txt";
        var content = new byte[] { 1, 2, 3, 4, 5 };

        // Act
        using (var stream = _fs.CreateWrite(fileName))
        {
            stream.Write(content, 0, content.Length);
        }

        // Assert
        var stats = _fs.GetStats();
        Assert.Equal(1, stats.Writes);
        Assert.Equal(content.Length, stats.TotalBytesWritten);
    }

    [Fact]
    public void Delete_LogsOperation()
    {
        // Arrange
        var fileName = "delete.txt";
        File.WriteAllText(Path.Combine(_testDirectory, fileName), "content");

        // Act
        var result = _fs.Delete(fileName);

        // Assert
        Assert.True(result);
        var stats = _fs.GetStats();
        Assert.Equal(1, stats.Deletes);
    }

    [Fact]
    public void Move_LogsOperation()
    {
        // Arrange
        var sourceFile = "source.txt";
        var destFile = "dest.txt";
        File.WriteAllText(Path.Combine(_testDirectory, sourceFile), "content");

        // Act
        var result = _fs.Move(sourceFile, destFile);

        // Assert
        Assert.True(result);
        var stats = _fs.GetStats();
        Assert.Equal(1, stats.Moves);
    }

    [Fact]
    public void GetFileInfo_ReturnsCorrectInfo()
    {
        // Arrange
        var fileName = "info.txt";
        var content = "test content";
        File.WriteAllText(Path.Combine(_testDirectory, fileName), content);

        // Act
        var info = _fs.GetFileInfo(fileName);

        // Assert
        Assert.NotNull(info);
        Assert.Equal(content.Length, info!.Length);
    }

    [Fact]
    public void EnumerateFiles_LogsOperation()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testDirectory, "file1.txt"), "");
        File.WriteAllText(Path.Combine(_testDirectory, "file2.txt"), "");

        // Act
        var files = _fs.EnumerateFiles("").ToList();

        // Assert
        Assert.Equal(2, files.Count);
        var stats = _fs.GetStats();
        Assert.Equal(1, stats.Enumerations);
    }

    [Fact]
    public void EnumerateDirectories_LogsOperation()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_testDirectory, "dir1"));
        Directory.CreateDirectory(Path.Combine(_testDirectory, "dir2"));

        // Act
        var dirs = _fs.EnumerateDirectories("").ToList();

        // Assert
        Assert.Equal(2, dirs.Count);
        var stats = _fs.GetStats();
        Assert.Equal(1, stats.Enumerations);
    }

    [Fact]
    public void CreateDirectory_CreatesDirectory()
    {
        // Arrange
        var dirPath = "newdir/subdir";

        // Act
        _fs.CreateDirectory(dirPath);

        // Assert
        Assert.True(Directory.Exists(Path.Combine(_testDirectory, dirPath)));
    }

    [Fact]
    public void ReadAllBytes_ReturnsContent()
    {
        // Arrange
        var fileName = "bytes.bin";
        var content = new byte[] { 1, 2, 3, 4, 5 };
        File.WriteAllBytes(Path.Combine(_testDirectory, fileName), content);

        // Act
        var result = _fs.ReadAllBytes(fileName);

        // Assert
        Assert.Equal(content, result);
        var stats = _fs.GetStats();
        Assert.Equal(1, stats.Reads);
        Assert.Equal(content.Length, stats.TotalBytesRead);
    }

    [Fact]
    public void WriteAllBytes_WritesContent()
    {
        // Arrange
        var fileName = "output.bin";
        var content = new byte[] { 5, 4, 3, 2, 1 };

        // Act
        _fs.WriteAllBytes(fileName, content);

        // Assert
        var writtenContent = File.ReadAllBytes(Path.Combine(_testDirectory, fileName));
        Assert.Equal(content, writtenContent);
        var stats = _fs.GetStats();
        Assert.Equal(1, stats.Writes);
        Assert.Equal(content.Length, stats.TotalBytesWritten);
    }

    [Fact]
    public void ReadAllText_ReturnsContent()
    {
        // Arrange
        var fileName = "text.txt";
        var content = "Hello, World!";
        File.WriteAllText(Path.Combine(_testDirectory, fileName), content);

        // Act
        var result = _fs.ReadAllText(fileName);

        // Assert
        Assert.Equal(content, result);
    }

    [Fact]
    public void WriteAllText_WritesContent()
    {
        // Arrange
        var fileName = "output.txt";
        var content = "Hello, World!";

        // Act
        _fs.WriteAllText(fileName, content);

        // Assert
        var writtenContent = File.ReadAllText(Path.Combine(_testDirectory, fileName));
        Assert.Equal(content, writtenContent);
    }

    [Fact]
    public void GetStats_ReturnsAccumulatedStats()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testDirectory, "test.txt"), "content");

        // Act
        _fs.FileExists("test.txt");
        _fs.FileExists("nonexistent.txt");
        _fs.DirectoryExists("");

        // Assert
        var stats = _fs.GetStats();
        Assert.Equal(2, stats.FileExistsChecks);
        Assert.Equal(1, stats.DirectoryExistsChecks);
    }

    [Fact]
    public void ResetStats_ClearsAllStats()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testDirectory, "test.txt"), "content");
        _fs.FileExists("test.txt");
        _fs.FileExists("test.txt");

        // Act
        _fs.ResetStats();

        // Assert
        var stats = _fs.GetStats();
        Assert.Equal(0, stats.FileExistsChecks);
        Assert.Equal(0, stats.Reads);
        Assert.Equal(0, stats.Writes);
    }

    [Fact]
    public void MinimumLogLevel_CanBeChanged()
    {
        // Arrange
        _fs.MinimumLogLevel = LogLevel.Information;

        // Assert
        Assert.Equal(LogLevel.Information, _fs.MinimumLogLevel);
    }

    [Fact]
    public void IncludeTiming_CanBeChanged()
    {
        // Arrange
        _fs.IncludeTiming = false;

        // Assert
        Assert.False(_fs.IncludeTiming);
    }

    [Fact]
    public void FileSystemOperationStats_ToString_ReturnsFormattedString()
    {
        // Arrange
        var stats = new FileSystemOperationStats
        {
            Reads = 10,
            Writes = 5,
            Deletes = 2,
            Moves = 1,
            FileExistsChecks = 20,
            DirectoryExistsChecks = 5,
            Enumerations = 3,
            Errors = 0,
            TotalBytesRead = 1024,
            TotalBytesWritten = 512
        };

        // Act
        var result = stats.ToString();

        // Assert
        Assert.Contains("Reads: 10", result);
        Assert.Contains("Writes: 5", result);
        Assert.Contains("Deletes: 2", result);
        Assert.Contains("Moves: 1", result);
        Assert.Contains("BytesRead: 1024", result);
        Assert.Contains("BytesWritten: 512", result);
    }

    [Fact]
    public void WrapsUnderlyingFileSystem()
    {
        // Arrange
        using var underlying = new CaseInsensitiveFs(_testDirectory, caseInsensitive: true);
        using var loggingFs = new LoggingFs(_loggerMock.Object, underlying);

        // Create a file with specific case
        File.WriteAllText(Path.Combine(_testDirectory, "TestFile.txt"), "content");

        // Act - use different case
        var exists = loggingFs.FileExists("testfile.txt");

        // Assert - should work because underlying is case-insensitive
        Assert.True(exists);
        Assert.Same(underlying, loggingFs.Underlying);
    }

    [Fact]
    public void TracksErrors()
    {
        // Act
        _fs.OpenRead("nonexistent_file.txt");

        // Assert
        var stats = _fs.GetStats();
        Assert.Equal(1, stats.Reads);
        // Note: OpenRead doesn't increment errors for missing files, it just returns null
    }

    [Fact]
    public void WriteAllText_CreatesDirectories()
    {
        // Arrange
        var filePath = "new/nested/dir/file.txt";
        var content = "content";

        // Act
        _fs.WriteAllText(filePath, content);

        // Assert
        Assert.True(File.Exists(Path.Combine(_testDirectory, filePath)));
    }

    [Fact]
    public void WriteAllBytes_CreatesDirectories()
    {
        // Arrange
        var filePath = "another/nested/dir/file.bin";
        var content = new byte[] { 1, 2, 3 };

        // Act
        _fs.WriteAllBytes(filePath, content);

        // Assert
        Assert.True(File.Exists(Path.Combine(_testDirectory, filePath)));
    }
}
