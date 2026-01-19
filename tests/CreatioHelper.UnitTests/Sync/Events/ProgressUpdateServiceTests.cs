using CreatioHelper.Infrastructure.Services.Sync.Events;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.Events;

public class ProgressUpdateServiceTests : IDisposable
{
    private readonly Mock<ILogger<ProgressUpdateService>> _loggerMock;
    private readonly ProgressUpdateConfiguration _config;
    private readonly ProgressUpdateService _service;

    public ProgressUpdateServiceTests()
    {
        _loggerMock = new Mock<ILogger<ProgressUpdateService>>();
        _config = new ProgressUpdateConfiguration();
        _service = new ProgressUpdateService(_loggerMock.Object, _config);
    }

    public void Dispose()
    {
        _service.Dispose();
    }

    #region GetUpdateInterval Tests

    [Fact]
    public void GetUpdateInterval_Default_Returns5Seconds()
    {
        var interval = _service.GetUpdateInterval("folder1");

        Assert.Equal(TimeSpan.FromSeconds(5), interval);
    }

    [Fact]
    public void GetUpdateInterval_AfterSet_ReturnsSetValue()
    {
        _service.SetUpdateInterval("folder1", TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(10), _service.GetUpdateInterval("folder1"));
    }

    [Fact]
    public void GetUpdateInterval_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetUpdateInterval(null!));
    }

    #endregion

    #region SetUpdateInterval Tests

    [Fact]
    public void SetUpdateInterval_ValidInterval_SetsCorrectly()
    {
        _service.SetUpdateInterval("folder1", TimeSpan.FromSeconds(15));

        Assert.Equal(TimeSpan.FromSeconds(15), _service.GetUpdateInterval("folder1"));
    }

    [Fact]
    public void SetUpdateInterval_BelowMinimum_ClampedToMinimum()
    {
        _service.SetUpdateInterval("folder1", TimeSpan.FromMilliseconds(10));

        Assert.True(_service.GetUpdateInterval("folder1") >= _config.MinUpdateInterval);
    }

    [Fact]
    public void SetUpdateInterval_AboveMaximum_ClampedToMaximum()
    {
        _service.SetUpdateInterval("folder1", TimeSpan.FromMinutes(60));

        Assert.True(_service.GetUpdateInterval("folder1") <= _config.MaxUpdateInterval);
    }

    [Fact]
    public void SetUpdateInterval_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.SetUpdateInterval(null!, TimeSpan.FromSeconds(5)));
    }

    #endregion

    #region ReportProgress Tests

    [Fact]
    public void ReportProgress_TracksProgress()
    {
        var progress = new TransferProgress
        {
            FilePath = "file.txt",
            BytesTotal = 1000,
            BytesTransferred = 500
        };

        _service.ReportProgress("folder1", "file.txt", progress);

        var result = _service.GetFileProgress("folder1", "file.txt");
        Assert.NotNull(result);
        Assert.Equal(500, result.BytesTransferred);
    }

    [Fact]
    public void ReportProgress_DisabledFolder_DoesNotTrack()
    {
        _service.SetProgressTrackingEnabled("folder1", false);
        var progress = new TransferProgress { FilePath = "file.txt" };

        _service.ReportProgress("folder1", "file.txt", progress);

        Assert.Null(_service.GetFileProgress("folder1", "file.txt"));
    }

    [Fact]
    public void ReportProgress_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ReportProgress(null!, "file.txt", new TransferProgress()));
    }

    [Fact]
    public void ReportProgress_NullFilePath_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ReportProgress("folder1", null!, new TransferProgress()));
    }

    [Fact]
    public void ReportProgress_NullProgress_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ReportProgress("folder1", "file.txt", null!));
    }

    #endregion

    #region GetFolderProgress Tests

    [Fact]
    public void GetFolderProgress_NoFiles_ReturnsEmpty()
    {
        var progress = _service.GetFolderProgress("folder1");

        Assert.Equal("folder1", progress.FolderId);
        Assert.Equal(0, progress.FilesInProgress);
        Assert.Equal(0, progress.BytesTotal);
    }

    [Fact]
    public void GetFolderProgress_WithFiles_AggregatesCorrectly()
    {
        _service.ReportProgress("folder1", "file1.txt", new TransferProgress
        {
            BytesTotal = 1000,
            BytesTransferred = 500,
            State = TransferState.InProgress
        });
        _service.ReportProgress("folder1", "file2.txt", new TransferProgress
        {
            BytesTotal = 2000,
            BytesTransferred = 2000,
            State = TransferState.Complete
        });

        var progress = _service.GetFolderProgress("folder1");

        Assert.Equal(3000, progress.BytesTotal);
        Assert.Equal(2500, progress.BytesTransferred);
        Assert.Equal(1, progress.FilesInProgress);
        Assert.Equal(1, progress.FilesComplete);
    }

    [Fact]
    public void GetFolderProgress_PercentComplete_CalculatesCorrectly()
    {
        _service.ReportProgress("folder1", "file.txt", new TransferProgress
        {
            BytesTotal = 1000,
            BytesTransferred = 250
        });

        var progress = _service.GetFolderProgress("folder1");

        Assert.Equal(25.0, progress.PercentComplete);
    }

    [Fact]
    public void GetFolderProgress_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetFolderProgress(null!));
    }

    #endregion

    #region GetFileProgress Tests

    [Fact]
    public void GetFileProgress_Exists_ReturnsProgress()
    {
        var original = new TransferProgress
        {
            FilePath = "file.txt",
            BytesTotal = 1000,
            BytesTransferred = 100
        };
        _service.ReportProgress("folder1", "file.txt", original);

        var result = _service.GetFileProgress("folder1", "file.txt");

        Assert.NotNull(result);
        Assert.Equal(100, result.BytesTransferred);
    }

    [Fact]
    public void GetFileProgress_NotExists_ReturnsNull()
    {
        var result = _service.GetFileProgress("folder1", "nonexistent.txt");

        Assert.Null(result);
    }

    [Fact]
    public void GetFileProgress_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetFileProgress(null!, "file.txt"));
    }

    [Fact]
    public void GetFileProgress_NullFilePath_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetFileProgress("folder1", null!));
    }

    #endregion

    #region Subscribe Tests

    [Fact]
    public void Subscribe_ReceivesUpdates()
    {
        var received = new List<ProgressUpdateEventArgs>();
        using var subscription = _service.Subscribe("folder1", args => received.Add(args));

        // Set very short interval for test
        _service.SetUpdateInterval("folder1", TimeSpan.FromMilliseconds(1));

        _service.ReportProgress("folder1", "file.txt", new TransferProgress
        {
            BytesTotal = 1000,
            BytesTransferred = 100
        });

        Assert.Single(received);
        Assert.Equal("folder1", received[0].FolderId);
        Assert.Equal("file.txt", received[0].FilePath);
    }

    [Fact]
    public void Subscribe_AfterDispose_NoMoreUpdates()
    {
        var received = new List<ProgressUpdateEventArgs>();
        var subscription = _service.Subscribe("folder1", args => received.Add(args));

        _service.SetUpdateInterval("folder1", TimeSpan.FromMilliseconds(1));

        subscription.Dispose();

        _service.ReportProgress("folder1", "file.txt", new TransferProgress());

        Assert.Empty(received);
    }

    [Fact]
    public void Subscribe_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.Subscribe(null!, _ => { }));
    }

    [Fact]
    public void Subscribe_NullCallback_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.Subscribe("folder1", null!));
    }

    #endregion

    #region SubscribeAll Tests

    [Fact]
    public void SubscribeAll_ReceivesFromAllFolders()
    {
        var received = new List<ProgressUpdateEventArgs>();
        using var subscription = _service.SubscribeAll(args => received.Add(args));

        _service.SetUpdateInterval("folder1", TimeSpan.FromMilliseconds(1));
        _service.SetUpdateInterval("folder2", TimeSpan.FromMilliseconds(1));

        _service.ReportProgress("folder1", "file1.txt", new TransferProgress());
        _service.ReportProgress("folder2", "file2.txt", new TransferProgress());

        Assert.Equal(2, received.Count);
    }

    [Fact]
    public void SubscribeAll_NullCallback_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.SubscribeAll(null!));
    }

    #endregion

    #region MarkComplete Tests

    [Fact]
    public void MarkComplete_SetsStateToComplete()
    {
        _service.ReportProgress("folder1", "file.txt", new TransferProgress
        {
            BytesTotal = 1000,
            BytesTransferred = 500,
            State = TransferState.InProgress
        });

        _service.MarkComplete("folder1", "file.txt");

        var progress = _service.GetFileProgress("folder1", "file.txt");
        Assert.Equal(TransferState.Complete, progress?.State);
        Assert.Equal(1000, progress?.BytesTransferred);
    }

    [Fact]
    public void MarkComplete_NonExistent_NoOp()
    {
        // Should not throw
        _service.MarkComplete("folder1", "nonexistent.txt");
    }

    [Fact]
    public void MarkComplete_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.MarkComplete(null!, "file.txt"));
    }

    [Fact]
    public void MarkComplete_NullFilePath_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.MarkComplete("folder1", null!));
    }

    #endregion

    #region ClearProgress Tests

    [Fact]
    public void ClearProgress_RemovesAllFiles()
    {
        _service.ReportProgress("folder1", "file1.txt", new TransferProgress());
        _service.ReportProgress("folder1", "file2.txt", new TransferProgress());

        _service.ClearProgress("folder1");

        Assert.Null(_service.GetFileProgress("folder1", "file1.txt"));
        Assert.Null(_service.GetFileProgress("folder1", "file2.txt"));
    }

    [Fact]
    public void ClearProgress_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ClearProgress(null!));
    }

    #endregion

    #region ProgressTracking Enable/Disable Tests

    [Fact]
    public void IsProgressTrackingEnabled_Default_ReturnsTrue()
    {
        Assert.True(_service.IsProgressTrackingEnabled("folder1"));
    }

    [Fact]
    public void SetProgressTrackingEnabled_False_DisablesTracking()
    {
        _service.SetProgressTrackingEnabled("folder1", false);

        Assert.False(_service.IsProgressTrackingEnabled("folder1"));
    }

    [Fact]
    public void SetProgressTrackingEnabled_True_EnablesTracking()
    {
        _service.SetProgressTrackingEnabled("folder1", false);
        _service.SetProgressTrackingEnabled("folder1", true);

        Assert.True(_service.IsProgressTrackingEnabled("folder1"));
    }

    [Fact]
    public void SetProgressTrackingEnabled_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.SetProgressTrackingEnabled(null!, true));
    }

    [Fact]
    public void IsProgressTrackingEnabled_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.IsProgressTrackingEnabled(null!));
    }

    #endregion
}

public class TransferProgressTests
{
    [Fact]
    public void PercentComplete_CalculatesCorrectly()
    {
        var progress = new TransferProgress
        {
            BytesTotal = 1000,
            BytesTransferred = 250
        };

        Assert.Equal(25.0, progress.PercentComplete);
    }

    [Fact]
    public void PercentComplete_ZeroTotal_ReturnsZero()
    {
        var progress = new TransferProgress
        {
            BytesTotal = 0,
            BytesTransferred = 0
        };

        Assert.Equal(0.0, progress.PercentComplete);
    }

    [Fact]
    public void Elapsed_CalculatesFromStartTime()
    {
        var progress = new TransferProgress
        {
            StartTime = DateTime.UtcNow.AddSeconds(-10)
        };

        Assert.True(progress.Elapsed >= TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void BytesPerSecond_CalculatesCorrectly()
    {
        var progress = new TransferProgress
        {
            StartTime = DateTime.UtcNow.AddSeconds(-10),
            BytesTransferred = 1000
        };

        Assert.True(progress.BytesPerSecond >= 90); // Approximately 100 bytes/sec
    }

    [Fact]
    public void EstimatedTimeRemaining_CalculatesCorrectly()
    {
        var progress = new TransferProgress
        {
            StartTime = DateTime.UtcNow.AddSeconds(-10),
            BytesTotal = 2000,
            BytesTransferred = 1000
        };

        var remaining = progress.EstimatedTimeRemaining;
        Assert.NotNull(remaining);
        Assert.True(remaining.Value >= TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void EstimatedTimeRemaining_Complete_ReturnsNull()
    {
        var progress = new TransferProgress
        {
            BytesTotal = 1000,
            BytesTransferred = 1000
        };

        Assert.Null(progress.EstimatedTimeRemaining);
    }
}

public class ProgressUpdateConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new ProgressUpdateConfiguration();

        Assert.Equal(TimeSpan.FromSeconds(5), config.DefaultUpdateInterval);
        Assert.Equal(TimeSpan.FromMilliseconds(100), config.MinUpdateInterval);
        Assert.Equal(TimeSpan.FromMinutes(5), config.MaxUpdateInterval);
        Assert.True(config.DefaultProgressTrackingEnabled);
    }

    [Fact]
    public void GetEffectiveInterval_NoOverride_ReturnsDefault()
    {
        var config = new ProgressUpdateConfiguration { DefaultUpdateInterval = TimeSpan.FromSeconds(10) };

        Assert.Equal(TimeSpan.FromSeconds(10), config.GetEffectiveInterval("folder1"));
    }

    [Fact]
    public void GetEffectiveInterval_WithOverride_ReturnsOverride()
    {
        var config = new ProgressUpdateConfiguration();
        config.FolderIntervals["folder1"] = TimeSpan.FromSeconds(15);

        Assert.Equal(TimeSpan.FromSeconds(15), config.GetEffectiveInterval("folder1"));
    }

    [Fact]
    public void ClampInterval_BelowMin_ReturnsMin()
    {
        var config = new ProgressUpdateConfiguration();

        var result = config.ClampInterval(TimeSpan.FromMilliseconds(10));

        Assert.Equal(config.MinUpdateInterval, result);
    }

    [Fact]
    public void ClampInterval_AboveMax_ReturnsMax()
    {
        var config = new ProgressUpdateConfiguration();

        var result = config.ClampInterval(TimeSpan.FromMinutes(60));

        Assert.Equal(config.MaxUpdateInterval, result);
    }
}
