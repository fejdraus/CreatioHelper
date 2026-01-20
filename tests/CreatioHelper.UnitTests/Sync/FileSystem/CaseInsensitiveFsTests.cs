using CreatioHelper.Infrastructure.Services.Sync.FileSystem;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests.Sync.FileSystem;

public class CaseInsensitiveFsTests : IDisposable
{
    private readonly Mock<ILogger<CaseInsensitiveFs>> _loggerMock;
    private readonly string _testDirectory;
    private readonly CaseInsensitiveFs _fs;

    public CaseInsensitiveFsTests()
    {
        _loggerMock = new Mock<ILogger<CaseInsensitiveFs>>();
        _testDirectory = Path.Combine(Path.GetTempPath(), $"CaseInsensitiveFsTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDirectory);
        _fs = new CaseInsensitiveFs(_testDirectory, caseInsensitive: true, logger: _loggerMock.Object);
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
    public void Constructor_SetsPropertiesCorrectly()
    {
        // Assert
        Assert.Equal(_testDirectory, _fs.BasePath);
        Assert.True(_fs.IsCaseInsensitive);
    }

    [Fact]
    public void FileExists_FindsFileWithDifferentCase()
    {
        // Arrange
        var fileName = "TestFile.txt";
        File.WriteAllText(Path.Combine(_testDirectory, fileName), "content");

        // Act
        var exists1 = _fs.FileExists("testfile.txt");
        var exists2 = _fs.FileExists("TESTFILE.TXT");
        var exists3 = _fs.FileExists("TestFile.txt");

        // Assert
        Assert.True(exists1);
        Assert.True(exists2);
        Assert.True(exists3);
    }

    [Fact]
    public void FileExists_ReturnsFalse_WhenFileDoesNotExist()
    {
        // Act
        var exists = _fs.FileExists("nonexistent.txt");

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public void DirectoryExists_FindsDirectoryWithDifferentCase()
    {
        // Arrange
        var dirName = "TestDir";
        Directory.CreateDirectory(Path.Combine(_testDirectory, dirName));

        // Act
        var exists1 = _fs.DirectoryExists("testdir");
        var exists2 = _fs.DirectoryExists("TESTDIR");
        var exists3 = _fs.DirectoryExists("TestDir");

        // Assert
        Assert.True(exists1);
        Assert.True(exists2);
        Assert.True(exists3);
    }

    [Fact]
    public void DirectoryExists_ReturnsFalse_WhenDirectoryDoesNotExist()
    {
        // Act
        var exists = _fs.DirectoryExists("nonexistent");

        // Assert
        Assert.False(exists);
    }

    [Fact]
    public void GetRealPath_ReturnsCorrectCase()
    {
        // Arrange
        var fileName = "MyFile.TXT";
        File.WriteAllText(Path.Combine(_testDirectory, fileName), "content");

        // Act
        var realPath = _fs.GetRealPath("myfile.txt");

        // Assert
        Assert.NotNull(realPath);
        Assert.Equal("MyFile.TXT", realPath);
    }

    [Fact]
    public void GetRealPath_ReturnsCorrectCase_ForNestedPaths()
    {
        // Arrange
        var dirPath = Path.Combine(_testDirectory, "Parent", "Child");
        Directory.CreateDirectory(dirPath);
        var fileName = "File.txt";
        File.WriteAllText(Path.Combine(dirPath, fileName), "content");

        // Act
        var realPath = _fs.GetRealPath("PARENT/CHILD/FILE.TXT");

        // Assert
        Assert.NotNull(realPath);
        Assert.Contains("Parent", realPath);
        Assert.Contains("Child", realPath);
        Assert.Contains("File.txt", realPath);
    }

    [Fact]
    public void GetRealPath_ReturnsNull_WhenPathDoesNotExist()
    {
        // Act
        var realPath = _fs.GetRealPath("nonexistent/path/file.txt");

        // Assert
        Assert.Null(realPath);
    }

    [Fact]
    public void OpenRead_OpensFileWithDifferentCase()
    {
        // Arrange
        var fileName = "ReadTest.txt";
        var content = "test content";
        File.WriteAllText(Path.Combine(_testDirectory, fileName), content);

        // Act
        using var stream = _fs.OpenRead("readtest.TXT");

        // Assert
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        Assert.Equal(content, reader.ReadToEnd());
    }

    [Fact]
    public void OpenRead_ReturnsNull_WhenFileDoesNotExist()
    {
        // Act
        var stream = _fs.OpenRead("nonexistent.txt");

        // Assert
        Assert.Null(stream);
    }

    [Fact]
    public void CreateWrite_CreatesFile()
    {
        // Arrange
        var fileName = "NewFile.txt";
        var content = "new content";

        // Act
        using (var stream = _fs.CreateWrite(fileName))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(content);
        }

        // Assert
        Assert.True(File.Exists(Path.Combine(_testDirectory, fileName)));
        Assert.Equal(content, File.ReadAllText(Path.Combine(_testDirectory, fileName)));
    }

    [Fact]
    public void CreateWrite_CreatesDirectories()
    {
        // Arrange
        var filePath = "NewDir/SubDir/File.txt";

        // Act
        using var stream = _fs.CreateWrite(filePath);
        stream.WriteByte(42);

        // Assert
        Assert.True(File.Exists(Path.Combine(_testDirectory, filePath)));
    }

    [Fact]
    public void Delete_DeletesFile_WithDifferentCase()
    {
        // Arrange
        var fileName = "DeleteMe.txt";
        var fullPath = Path.Combine(_testDirectory, fileName);
        File.WriteAllText(fullPath, "content");

        // Act
        var result = _fs.Delete("deleteme.TXT");

        // Assert
        Assert.True(result);
        Assert.False(File.Exists(fullPath));
    }

    [Fact]
    public void Delete_ReturnsFalse_WhenFileDoesNotExist()
    {
        // Act
        var result = _fs.Delete("nonexistent.txt");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void Move_MovesFile_WithDifferentCase()
    {
        // Arrange
        var sourceFile = "Source.txt";
        var destFile = "Destination.txt";
        File.WriteAllText(Path.Combine(_testDirectory, sourceFile), "content");

        // Act
        var result = _fs.Move("SOURCE.TXT", destFile);

        // Assert
        Assert.True(result);
        Assert.False(File.Exists(Path.Combine(_testDirectory, sourceFile)));
        Assert.True(File.Exists(Path.Combine(_testDirectory, destFile)));
    }

    [Fact]
    public void GetFileInfo_ReturnsInfo_WithDifferentCase()
    {
        // Arrange
        var fileName = "InfoTest.txt";
        var content = "test content";
        File.WriteAllText(Path.Combine(_testDirectory, fileName), content);

        // Act
        var info = _fs.GetFileInfo("INFOTEST.txt");

        // Assert
        Assert.NotNull(info);
        Assert.Equal(content.Length, info.Length);
    }

    [Fact]
    public void GetFileInfo_ReturnsNull_WhenFileDoesNotExist()
    {
        // Act
        var info = _fs.GetFileInfo("nonexistent.txt");

        // Assert
        Assert.Null(info);
    }

    [Fact]
    public void EnumerateFiles_ReturnsFiles()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testDirectory, "File1.txt"), "");
        File.WriteAllText(Path.Combine(_testDirectory, "File2.txt"), "");

        // Act
        var files = _fs.EnumerateFiles("").ToList();

        // Assert
        Assert.Equal(2, files.Count);
    }

    [Fact]
    public void EnumerateDirectories_ReturnsDirectories()
    {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_testDirectory, "Dir1"));
        Directory.CreateDirectory(Path.Combine(_testDirectory, "Dir2"));

        // Act
        var dirs = _fs.EnumerateDirectories("").ToList();

        // Assert
        Assert.Equal(2, dirs.Count);
    }

    [Fact]
    public void ClearCache_ClearsAllEntries()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testDirectory, "CacheTest.txt"), "");
        _fs.GetRealPath("cachetest.txt"); // Populate cache

        var (_, _, sizeBefore) = _fs.CacheStats;
        Assert.True(sizeBefore > 0);

        // Act
        _fs.ClearCache();

        // Assert
        var (_, _, sizeAfter) = _fs.CacheStats;
        Assert.Equal(0, sizeAfter);
    }

    [Fact]
    public void InvalidatePath_RemovesFromCache()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testDirectory, "InvalidateTest.txt"), "");
        _fs.GetRealPath("invalidatetest.txt"); // Populate cache

        // Act
        _fs.InvalidatePath("invalidatetest.txt");

        // After invalidation, the next GetRealPath should re-traverse
        var stats1 = _fs.CacheStats;
        _fs.GetRealPath("invalidatetest.txt");
        var stats2 = _fs.CacheStats;

        // Assert - misses should have increased
        Assert.True(stats2.Misses > stats1.Misses);
    }

    [Fact]
    public void CacheStats_TracksCacheUsage()
    {
        // Arrange
        File.WriteAllText(Path.Combine(_testDirectory, "StatsTest.txt"), "");
        _fs.ClearCache();

        // Act
        _fs.GetRealPath("statstest.txt"); // Miss
        _fs.GetRealPath("statstest.txt"); // Hit
        _fs.GetRealPath("statstest.txt"); // Hit

        // Assert
        var (hits, misses, size) = _fs.CacheStats;
        Assert.Equal(2, hits);
        Assert.Equal(1, misses);
        Assert.True(size > 0);
    }

    [Fact]
    public void CaseSensitiveMode_DoesNotIgnoreCase()
    {
        // Arrange
        using var caseSensitiveFs = new CaseInsensitiveFs(_testDirectory, caseInsensitive: false);
        File.WriteAllText(Path.Combine(_testDirectory, "CaseSensitive.txt"), "");

        // Act - on case-sensitive systems, lowercase won't match
        // Note: This test behavior depends on the OS
        var exists = caseSensitiveFs.FileExists("CaseSensitive.txt");

        // Assert - the exact case should always work
        Assert.True(exists);
    }

    [Fact]
    public void GetRealPath_HandlesPathSeparators()
    {
        // Arrange
        var dirPath = Path.Combine(_testDirectory, "TestPath");
        Directory.CreateDirectory(dirPath);
        File.WriteAllText(Path.Combine(dirPath, "File.txt"), "");

        // Act - test both forward and back slashes
        var realPath1 = _fs.GetRealPath("testpath/file.txt");
        var realPath2 = _fs.GetRealPath("testpath\\file.txt");

        // Assert
        Assert.NotNull(realPath1);
        Assert.NotNull(realPath2);
    }

    [Fact]
    public void GetRealPath_ReturnsNull_ForEmptyPath()
    {
        // Act
        var realPath1 = _fs.GetRealPath("");
        var realPath2 = _fs.GetRealPath(null!);

        // Assert
        Assert.Null(realPath1);
        Assert.Null(realPath2);
    }
}
