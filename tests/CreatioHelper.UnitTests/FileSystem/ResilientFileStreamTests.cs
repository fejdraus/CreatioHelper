using System;
using System.IO;
using CreatioHelper.Infrastructure.Services.Sync.FileSystem;
using Xunit;

namespace CreatioHelper.UnitTests.FileSystem;

public class ResilientFileStreamTests : IDisposable
{
    private readonly string _directory;

    public ResilientFileStreamTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"chrfs_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
    }

    private static FileStream Open(string path, ref int fallbacks)
    {
        var count = 0;
        var stream = ResilientFileStream.Open(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan,
            _ => count++);
        fallbacks = count;
        return stream;
    }

    [Fact]
    public void Open_ReadsAnExistingFile()
    {
        var path = Path.Combine(_directory, "present.txt");
        File.WriteAllText(path, "payload");

        var fallbacks = 0;
        using var stream = Open(path, ref fallbacks);

        Assert.Equal(0, fallbacks);
        Assert.Equal(7, stream.Length);
    }

    [Fact]
    public void Open_DoesNotRetryWhenTheFileIsMissing()
    {
        var path = Path.Combine(_directory, "gone.txt");
        var fallbacks = 0;

        Assert.Throws<FileNotFoundException>(() =>
        {
            using var stream = ResilientFileStream.Open(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.Asynchronous, _ => fallbacks++);
        });

        Assert.Equal(0, fallbacks);
    }

    [Fact]
    public void Open_DoesNotRetryWhenTheDirectoryIsMissing()
    {
        var path = Path.Combine(_directory, "no-such-dir", "gone.txt");
        var fallbacks = 0;

        Assert.Throws<DirectoryNotFoundException>(() =>
        {
            using var stream = ResilientFileStream.Open(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.Asynchronous, _ => fallbacks++);
        });

        Assert.Equal(0, fallbacks);
    }

    [Fact]
    public void Open_KeepsTheRequestedOptionsOnSuccess()
    {
        var path = Path.Combine(_directory, "async.txt");
        File.WriteAllText(path, "payload");

        var fallbacks = 0;
        using var stream = Open(path, ref fallbacks);

        Assert.True(stream.IsAsync);
        Assert.Equal(0, fallbacks);
    }

    [Fact]
    public void Open_WorksWithoutAFallbackCallback()
    {
        var path = Path.Combine(_directory, "nocallback.txt");
        File.WriteAllText(path, "payload");

        using var stream = ResilientFileStream.Open(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);

        Assert.Equal(7, stream.Length);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
