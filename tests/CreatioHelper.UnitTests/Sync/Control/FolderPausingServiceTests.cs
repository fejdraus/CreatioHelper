using CreatioHelper.Infrastructure.Services.Sync.Control;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.Control;

public class FolderPausingServiceTests : IDisposable
{
    private readonly Mock<ILogger<FolderPausingService>> _loggerMock;
    private readonly FolderPausingConfiguration _config;
    private readonly FolderPausingService _service;

    public FolderPausingServiceTests()
    {
        _loggerMock = new Mock<ILogger<FolderPausingService>>();
        _config = new FolderPausingConfiguration();
        _service = new FolderPausingService(_loggerMock.Object, _config);
    }

    public void Dispose()
    {
        _service.Dispose();
    }

    #region IsFolderPaused Tests

    [Fact]
    public void IsFolderPaused_NotPaused_ReturnsFalse()
    {
        Assert.False(_service.IsFolderPaused("folder1"));
    }

    [Fact]
    public async Task IsFolderPaused_AfterPause_ReturnsTrue()
    {
        await _service.PauseFolderAsync("folder1");

        Assert.True(_service.IsFolderPaused("folder1"));
    }

    [Fact]
    public async Task IsFolderPaused_AfterResume_ReturnsFalse()
    {
        await _service.PauseFolderAsync("folder1");
        await _service.ResumeFolderAsync("folder1");

        Assert.False(_service.IsFolderPaused("folder1"));
    }

    [Fact]
    public void IsFolderPaused_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.IsFolderPaused(null!));
    }

    #endregion

    #region PauseFolderAsync Tests

    [Fact]
    public async Task PauseFolderAsync_NewFolder_Pauses()
    {
        await _service.PauseFolderAsync("folder1");

        Assert.True(_service.IsFolderPaused("folder1"));
    }

    [Fact]
    public async Task PauseFolderAsync_WithReason_StoresReason()
    {
        await _service.PauseFolderAsync("folder1", "Maintenance mode");

        var info = _service.GetPauseInfo("folder1");
        Assert.Equal("Maintenance mode", info?.PauseReason);
    }

    [Fact]
    public async Task PauseFolderAsync_AlreadyPaused_NoOp()
    {
        await _service.PauseFolderAsync("folder1");
        var firstPauseTime = _service.GetPauseInfo("folder1")?.PausedAt;

        await Task.Delay(10);
        await _service.PauseFolderAsync("folder1");
        var secondPauseTime = _service.GetPauseInfo("folder1")?.PausedAt;

        Assert.Equal(firstPauseTime, secondPauseTime);
    }

    [Fact]
    public async Task PauseFolderAsync_NullFolderId_ThrowsArgumentNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.PauseFolderAsync(null!));
    }

    #endregion

    #region ResumeFolderAsync Tests

    [Fact]
    public async Task ResumeFolderAsync_PausedFolder_Resumes()
    {
        await _service.PauseFolderAsync("folder1");
        await _service.ResumeFolderAsync("folder1");

        Assert.False(_service.IsFolderPaused("folder1"));
    }

    [Fact]
    public async Task ResumeFolderAsync_NotPaused_NoOp()
    {
        // Should not throw
        await _service.ResumeFolderAsync("folder1");

        Assert.False(_service.IsFolderPaused("folder1"));
    }

    [Fact]
    public async Task ResumeFolderAsync_ClearsReasonAndTime()
    {
        await _service.PauseFolderAsync("folder1", "Some reason");
        await _service.ResumeFolderAsync("folder1");

        var info = _service.GetPauseInfo("folder1");
        Assert.Null(info?.PauseReason);
        Assert.Null(info?.PausedAt);
    }

    #endregion

    #region ToggleFolderPauseAsync Tests

    [Fact]
    public async Task ToggleFolderPauseAsync_NotPaused_Pauses()
    {
        var result = await _service.ToggleFolderPauseAsync("folder1");

        Assert.True(result); // Now paused
        Assert.True(_service.IsFolderPaused("folder1"));
    }

    [Fact]
    public async Task ToggleFolderPauseAsync_Paused_Resumes()
    {
        await _service.PauseFolderAsync("folder1");

        var result = await _service.ToggleFolderPauseAsync("folder1");

        Assert.False(result); // Now not paused
        Assert.False(_service.IsFolderPaused("folder1"));
    }

    #endregion

    #region GetPausedFolders Tests

    [Fact]
    public void GetPausedFolders_NoPaused_ReturnsEmpty()
    {
        var paused = _service.GetPausedFolders();

        Assert.Empty(paused);
    }

    [Fact]
    public async Task GetPausedFolders_SomePaused_ReturnsOnlyPaused()
    {
        await _service.PauseFolderAsync("folder1");
        await _service.PauseFolderAsync("folder2");
        await _service.PauseFolderAsync("folder3");
        await _service.ResumeFolderAsync("folder2");

        var paused = _service.GetPausedFolders();

        Assert.Equal(2, paused.Count);
        Assert.Contains("folder1", paused);
        Assert.Contains("folder3", paused);
        Assert.DoesNotContain("folder2", paused);
    }

    #endregion

    #region GetPauseInfo Tests

    [Fact]
    public void GetPauseInfo_NotPaused_ReturnsNotPausedInfo()
    {
        var info = _service.GetPauseInfo("folder1");

        Assert.NotNull(info);
        Assert.Equal("folder1", info.FolderId);
        Assert.False(info.IsPaused);
        Assert.Null(info.PausedAt);
    }

    [Fact]
    public async Task GetPauseInfo_Paused_ReturnsFullInfo()
    {
        await _service.PauseFolderAsync("folder1", "Test reason");

        var info = _service.GetPauseInfo("folder1");

        Assert.NotNull(info);
        Assert.True(info.IsPaused);
        Assert.Equal("Test reason", info.PauseReason);
        Assert.NotNull(info.PausedAt);
        Assert.NotNull(info.PauseDuration);
        Assert.True(info.PauseDuration.Value >= TimeSpan.Zero);
    }

    [Fact]
    public void GetPauseInfo_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetPauseInfo(null!));
    }

    #endregion

    #region WaitUntilResumedAsync Tests

    [Fact]
    public async Task WaitUntilResumedAsync_NotPaused_ReturnsImmediately()
    {
        var task = _service.WaitUntilResumedAsync("folder1");

        await task;
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitUntilResumedAsync_Paused_WaitsForResume()
    {
        await _service.PauseFolderAsync("folder1");
        var waitTask = _service.WaitUntilResumedAsync("folder1");

        await Task.Delay(50);
        Assert.False(waitTask.IsCompleted);

        await _service.ResumeFolderAsync("folder1");

        await waitTask;
        Assert.True(waitTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitUntilResumedAsync_Cancellation_ThrowsOperationCanceled()
    {
        await _service.PauseFolderAsync("folder1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // TaskCanceledException inherits from OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.WaitUntilResumedAsync("folder1", cts.Token));
    }

    #endregion

    #region Events Tests

    [Fact]
    public async Task FolderPaused_RaisedOnPause()
    {
        var raised = false;
        string? raisedFolderId = null;

        _service.FolderPaused += (s, e) =>
        {
            raised = true;
            raisedFolderId = e.FolderId;
        };

        await _service.PauseFolderAsync("folder1");

        Assert.True(raised);
        Assert.Equal("folder1", raisedFolderId);
    }

    [Fact]
    public async Task FolderResumed_RaisedOnResume()
    {
        var raised = false;
        string? raisedFolderId = null;

        _service.FolderResumed += (s, e) =>
        {
            raised = true;
            raisedFolderId = e.FolderId;
        };

        await _service.PauseFolderAsync("folder1");
        await _service.ResumeFolderAsync("folder1");

        Assert.True(raised);
        Assert.Equal("folder1", raisedFolderId);
    }

    [Fact]
    public async Task FolderPaused_EventArgsCorrect()
    {
        FolderPauseEventArgs? capturedArgs = null;

        _service.FolderPaused += (s, e) => capturedArgs = e;

        await _service.PauseFolderAsync("folder1", "Test reason");

        Assert.NotNull(capturedArgs);
        Assert.Equal("folder1", capturedArgs.FolderId);
        Assert.True(capturedArgs.IsPaused);
        Assert.Equal("Test reason", capturedArgs.Reason);
    }

    #endregion

    #region InitiallyPausedFolders Tests

    [Fact]
    public void InitiallyPausedFolders_ArePausedOnStart()
    {
        var config = new FolderPausingConfiguration();
        config.InitiallyPausedFolders.Add("folder1");
        config.InitiallyPausedFolders.Add("folder2");

        using var service = new FolderPausingService(_loggerMock.Object, config);

        Assert.True(service.IsFolderPaused("folder1"));
        Assert.True(service.IsFolderPaused("folder2"));
        Assert.False(service.IsFolderPaused("folder3"));
    }

    #endregion

    #region AutoResume Tests

    [Fact]
    public async Task AutoResumeAfter_ResumesAfterDelay()
    {
        var config = new FolderPausingConfiguration
        {
            AutoResumeAfter = TimeSpan.FromMilliseconds(100)
        };
        using var service = new FolderPausingService(_loggerMock.Object, config);

        await service.PauseFolderAsync("folder1");
        Assert.True(service.IsFolderPaused("folder1"));

        await Task.Delay(200);

        Assert.False(service.IsFolderPaused("folder1"));
    }

    #endregion
}

public class FolderPausingConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new FolderPausingConfiguration();

        Assert.Empty(config.InitiallyPausedFolders);
        Assert.True(config.PersistPauseState);
        Assert.Null(config.AutoResumeAfter);
    }
}

public class FolderPauseInfoTests
{
    [Fact]
    public void PauseDuration_NotPaused_ReturnsNull()
    {
        var info = new FolderPauseInfo
        {
            FolderId = "folder1",
            IsPaused = false,
            PausedAt = null
        };

        Assert.Null(info.PauseDuration);
    }

    [Fact]
    public void PauseDuration_Paused_ReturnsPositive()
    {
        var info = new FolderPauseInfo
        {
            FolderId = "folder1",
            IsPaused = true,
            PausedAt = DateTime.UtcNow.AddSeconds(-10)
        };

        Assert.NotNull(info.PauseDuration);
        Assert.True(info.PauseDuration.Value >= TimeSpan.FromSeconds(10));
    }
}
