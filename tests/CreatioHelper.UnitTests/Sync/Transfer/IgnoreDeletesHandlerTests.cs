using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync.Transfer;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.Transfer;

public class IgnoreDeletesHandlerTests
{
    private readonly Mock<ILogger<IgnoreDeletesHandler>> _loggerMock;
    private readonly IgnoreDeletesHandler _handler;

    public IgnoreDeletesHandlerTests()
    {
        _loggerMock = new Mock<ILogger<IgnoreDeletesHandler>>();
        _handler = new IgnoreDeletesHandler(_loggerMock.Object);
    }

    private SyncFolder CreateFolder(string id, bool ignoreDelete)
    {
        var folder = new SyncFolder(id, "Test Folder", "/test/path");
        folder.IgnoreDelete = ignoreDelete;
        return folder;
    }

    #region ShouldApplyDelete Tests

    [Fact]
    public void ShouldApplyDelete_IgnoreDeleteDisabled_ReturnsTrue()
    {
        var folder = CreateFolder("folder1", ignoreDelete: false);

        var result = _handler.ShouldApplyDelete(folder, "/test/file.txt", "device1");

        Assert.True(result);
    }

    [Fact]
    public void ShouldApplyDelete_IgnoreDeleteEnabled_ReturnsFalse()
    {
        var folder = CreateFolder("folder1", ignoreDelete: true);

        var result = _handler.ShouldApplyDelete(folder, "/test/file.txt", "device1");

        Assert.False(result);
    }

    [Fact]
    public void ShouldApplyDelete_NullFolder_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _handler.ShouldApplyDelete(null!, "/test/file.txt", "device1"));
    }

    [Fact]
    public void ShouldApplyDelete_NullFilePath_ThrowsArgumentNullException()
    {
        var folder = CreateFolder("folder1", ignoreDelete: true);

        Assert.Throws<ArgumentNullException>(() =>
            _handler.ShouldApplyDelete(folder, null!, "device1"));
    }

    #endregion

    #region RecordIgnoredDeleteAsync Tests

    [Fact]
    public async Task RecordIgnoredDeleteAsync_RecordsDelete()
    {
        var folder = CreateFolder("folder1", ignoreDelete: true);

        await _handler.RecordIgnoredDeleteAsync(folder, "/test/file.txt", "device1");

        var stats = _handler.GetStats("folder1");
        Assert.Equal(1, stats.TotalIgnoredDeletes);
    }

    [Fact]
    public async Task RecordIgnoredDeleteAsync_MultipleDeletes_IncrementCounter()
    {
        var folder = CreateFolder("folder1", ignoreDelete: true);

        await _handler.RecordIgnoredDeleteAsync(folder, "/test/file1.txt", "device1");
        await _handler.RecordIgnoredDeleteAsync(folder, "/test/file2.txt", "device1");
        await _handler.RecordIgnoredDeleteAsync(folder, "/test/file3.txt", "device2");

        var stats = _handler.GetStats("folder1");
        Assert.Equal(3, stats.TotalIgnoredDeletes);
    }

    [Fact]
    public async Task RecordIgnoredDeleteAsync_TracksByDevice()
    {
        var folder = CreateFolder("folder1", ignoreDelete: true);

        await _handler.RecordIgnoredDeleteAsync(folder, "/test/file1.txt", "device1");
        await _handler.RecordIgnoredDeleteAsync(folder, "/test/file2.txt", "device1");
        await _handler.RecordIgnoredDeleteAsync(folder, "/test/file3.txt", "device2");

        var stats = _handler.GetStats("folder1");
        Assert.Equal(2, stats.IgnoredDeletesByDevice["device1"]);
        Assert.Equal(1, stats.IgnoredDeletesByDevice["device2"]);
    }

    [Fact]
    public async Task RecordIgnoredDeleteAsync_RecordsTimestamps()
    {
        var folder = CreateFolder("folder1", ignoreDelete: true);
        var beforeRecord = DateTime.UtcNow;

        await _handler.RecordIgnoredDeleteAsync(folder, "/test/file.txt", "device1");

        var stats = _handler.GetStats("folder1");
        Assert.NotNull(stats.FirstIgnoredDelete);
        Assert.NotNull(stats.LastIgnoredDelete);
        Assert.True(stats.FirstIgnoredDelete >= beforeRecord);
    }

    [Fact]
    public async Task RecordIgnoredDeleteAsync_KeepsRecentDeletes()
    {
        var folder = CreateFolder("folder1", ignoreDelete: true);

        await _handler.RecordIgnoredDeleteAsync(folder, "/test/file1.txt", "device1");
        await _handler.RecordIgnoredDeleteAsync(folder, "/test/file2.txt", "device1");

        var stats = _handler.GetStats("folder1");
        Assert.Equal(2, stats.RecentDeletes.Count);
    }

    #endregion

    #region GetStats Tests

    [Fact]
    public void GetStats_NoRecords_ReturnsEmptyStats()
    {
        var stats = _handler.GetStats("nonexistent");

        Assert.Equal("nonexistent", stats.FolderId);
        Assert.Equal(0, stats.TotalIgnoredDeletes);
        Assert.Null(stats.FirstIgnoredDelete);
        Assert.Null(stats.LastIgnoredDelete);
    }

    [Fact]
    public void GetStats_NullFolderId_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => _handler.GetStats(null!));
    }

    #endregion

    #region ClearStats Tests

    [Fact]
    public async Task ClearStats_RemovesStats()
    {
        var folder = CreateFolder("folder1", ignoreDelete: true);
        await _handler.RecordIgnoredDeleteAsync(folder, "/test/file.txt", "device1");

        _handler.ClearStats("folder1");

        var stats = _handler.GetStats("folder1");
        Assert.Equal(0, stats.TotalIgnoredDeletes);
    }

    [Fact]
    public void ClearStats_NonexistentFolder_NoException()
    {
        // Should not throw
        _handler.ClearStats("nonexistent");
    }

    #endregion
}

public class PatternAwareIgnoreDeletesHandlerTests
{
    private readonly Mock<ILogger<IgnoreDeletesHandler>> _baseLoggerMock;
    private readonly Mock<ILogger<PatternAwareIgnoreDeletesHandler>> _loggerMock;
    private readonly IgnoreDeletesHandler _baseHandler;
    private readonly IgnoreDeletesConfiguration _config;
    private readonly PatternAwareIgnoreDeletesHandler _handler;

    public PatternAwareIgnoreDeletesHandlerTests()
    {
        _baseLoggerMock = new Mock<ILogger<IgnoreDeletesHandler>>();
        _loggerMock = new Mock<ILogger<PatternAwareIgnoreDeletesHandler>>();
        _baseHandler = new IgnoreDeletesHandler(_baseLoggerMock.Object);
        _config = new IgnoreDeletesConfiguration
        {
            AlwaysDeletePatterns = new List<string> { "*.tmp", "*.temp", "~$*", ".~lock.*" }
        };
        _handler = new PatternAwareIgnoreDeletesHandler(_baseHandler, _config, _loggerMock.Object);
    }

    private SyncFolder CreateFolder(string id, bool ignoreDelete)
    {
        var folder = new SyncFolder(id, "Test Folder", "/test/path");
        folder.IgnoreDelete = ignoreDelete;
        return folder;
    }

    [Fact]
    public void ShouldApplyDelete_TmpFile_AlwaysDeletes()
    {
        var folder = CreateFolder("folder1", ignoreDelete: true);

        var result = _handler.ShouldApplyDelete(folder, "/test/cache.tmp", "device1");

        Assert.True(result); // Should delete even with IgnoreDelete enabled
    }

    [Fact]
    public void ShouldApplyDelete_TempFile_AlwaysDeletes()
    {
        var folder = CreateFolder("folder1", ignoreDelete: true);

        var result = _handler.ShouldApplyDelete(folder, "/test/file.temp", "device1");

        Assert.True(result);
    }

    [Fact]
    public void ShouldApplyDelete_OfficeLockFile_AlwaysDeletes()
    {
        var folder = CreateFolder("folder1", ignoreDelete: true);

        var result = _handler.ShouldApplyDelete(folder, "/test/~$document.docx", "device1");

        Assert.True(result);
    }

    [Fact]
    public void ShouldApplyDelete_LibreOfficeLockFile_AlwaysDeletes()
    {
        var folder = CreateFolder("folder1", ignoreDelete: true);

        var result = _handler.ShouldApplyDelete(folder, "/test/.~lock.document.odt#", "device1");

        Assert.True(result);
    }

    [Fact]
    public void ShouldApplyDelete_RegularFile_RespectsIgnoreDelete()
    {
        var folder = CreateFolder("folder1", ignoreDelete: true);

        var result = _handler.ShouldApplyDelete(folder, "/test/important.txt", "device1");

        Assert.False(result); // Should not delete
    }

    [Fact]
    public void ShouldApplyDelete_RegularFile_IgnoreDeleteDisabled_Deletes()
    {
        var folder = CreateFolder("folder1", ignoreDelete: false);

        var result = _handler.ShouldApplyDelete(folder, "/test/important.txt", "device1");

        Assert.True(result);
    }
}
