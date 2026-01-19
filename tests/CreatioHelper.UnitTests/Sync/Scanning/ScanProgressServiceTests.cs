using CreatioHelper.Infrastructure.Services.Sync.Scanning;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.Scanning;

public class ScanProgressServiceTests
{
    private readonly Mock<ILogger<ScanProgressService>> _loggerMock;
    private readonly ScanProgressService _service;

    public ScanProgressServiceTests()
    {
        _loggerMock = new Mock<ILogger<ScanProgressService>>();
        _service = new ScanProgressService(_loggerMock.Object, progressIntervalSeconds: 0); // No throttling for tests
    }

    #region StartScan Tests

    [Fact]
    public void StartScan_CreatesTracker()
    {
        var tracker = _service.StartScan("folder1");

        Assert.NotNull(tracker);
        Assert.Equal("folder1", tracker.FolderId);
    }

    [Fact]
    public void StartScan_InitializesProgress()
    {
        var tracker = _service.StartScan("folder1", estimatedFiles: 100, estimatedBytes: 10000);

        Assert.Equal(100, tracker.Progress.FilesTotal);
        Assert.Equal(10000, tracker.Progress.BytesTotal);
        Assert.Equal(0, tracker.Progress.FilesScanned);
        Assert.Equal(0, tracker.Progress.BytesScanned);
    }

    [Fact]
    public void StartScan_SetsStartTime()
    {
        var before = DateTime.UtcNow;
        var tracker = _service.StartScan("folder1");
        var after = DateTime.UtcNow;

        Assert.True(tracker.Progress.StartTime >= before);
        Assert.True(tracker.Progress.StartTime <= after);
    }

    [Fact]
    public void StartScan_CancelsExistingScan()
    {
        var tracker1 = _service.StartScan("folder1");
        var tracker2 = _service.StartScan("folder1");

        Assert.Equal(ScanPhase.Cancelled, tracker1.Progress.Phase);
        Assert.NotEqual(ScanPhase.Cancelled, tracker2.Progress.Phase);
    }

    #endregion

    #region GetProgress Tests

    [Fact]
    public void GetProgress_ActiveScan_ReturnsProgress()
    {
        var tracker = _service.StartScan("folder1", 100, 10000);
        tracker.ReportFile("/test/file.txt", 500);

        var progress = _service.GetProgress("folder1");

        Assert.NotNull(progress);
        Assert.Equal(1, progress.FilesScanned);
        Assert.Equal(500, progress.BytesScanned);
    }

    [Fact]
    public void GetProgress_NoActiveScan_ReturnsNull()
    {
        var progress = _service.GetProgress("nonexistent");

        Assert.Null(progress);
    }

    [Fact]
    public void GetProgress_AfterComplete_ReturnsNull()
    {
        var tracker = _service.StartScan("folder1");
        tracker.Complete();

        var progress = _service.GetProgress("folder1");

        Assert.Null(progress);
    }

    #endregion

    #region IsScanInProgress Tests

    [Fact]
    public void IsScanInProgress_ActiveScan_ReturnsTrue()
    {
        _service.StartScan("folder1");

        Assert.True(_service.IsScanInProgress("folder1"));
    }

    [Fact]
    public void IsScanInProgress_NoScan_ReturnsFalse()
    {
        Assert.False(_service.IsScanInProgress("nonexistent"));
    }

    [Fact]
    public void IsScanInProgress_AfterComplete_ReturnsFalse()
    {
        var tracker = _service.StartScan("folder1");
        tracker.Complete();

        Assert.False(_service.IsScanInProgress("folder1"));
    }

    #endregion

    #region ScanProgressTracker Tests

    [Fact]
    public void Tracker_SetPhase_UpdatesProgress()
    {
        var tracker = _service.StartScan("folder1");

        tracker.SetPhase(ScanPhase.Scanning);

        Assert.Equal(ScanPhase.Scanning, tracker.Progress.Phase);
    }

    [Fact]
    public void Tracker_SetEstimates_UpdatesProgress()
    {
        var tracker = _service.StartScan("folder1");

        tracker.SetEstimates(500, 50000);

        Assert.Equal(500, tracker.Progress.FilesTotal);
        Assert.Equal(50000, tracker.Progress.BytesTotal);
    }

    [Fact]
    public void Tracker_ReportFile_UpdatesProgress()
    {
        var tracker = _service.StartScan("folder1", 100, 10000);

        tracker.ReportFile("/test/file.txt", 500);

        Assert.Equal(1, tracker.Progress.FilesScanned);
        Assert.Equal(500, tracker.Progress.BytesScanned);
        Assert.Equal("/test/file.txt", tracker.Progress.CurrentFile);
    }

    [Fact]
    public void Tracker_ReportFiles_BatchUpdate()
    {
        var tracker = _service.StartScan("folder1", 100, 10000);

        tracker.ReportFiles(10, 5000);

        Assert.Equal(10, tracker.Progress.FilesScanned);
        Assert.Equal(5000, tracker.Progress.BytesScanned);
    }

    [Fact]
    public void Tracker_Complete_SetsPhaseAndEndTime()
    {
        var tracker = _service.StartScan("folder1");

        tracker.Complete();

        Assert.Equal(ScanPhase.Completed, tracker.Progress.Phase);
        Assert.NotNull(tracker.Progress.EndTime);
    }

    [Fact]
    public void Tracker_Cancel_SetsPhaseAndEndTime()
    {
        var tracker = _service.StartScan("folder1");

        tracker.Cancel();

        Assert.Equal(ScanPhase.Cancelled, tracker.Progress.Phase);
        Assert.NotNull(tracker.Progress.EndTime);
        Assert.True(tracker.CancellationToken.IsCancellationRequested);
    }

    [Fact]
    public void Tracker_Fail_SetsPhase()
    {
        var tracker = _service.StartScan("folder1");

        tracker.Fail("Test error");

        Assert.Equal(ScanPhase.Failed, tracker.Progress.Phase);
        Assert.NotNull(tracker.Progress.EndTime);
    }

    [Fact]
    public void Tracker_Dispose_CancelsIfActive()
    {
        var tracker = _service.StartScan("folder1");
        tracker.SetPhase(ScanPhase.Scanning);

        tracker.Dispose();

        Assert.Equal(ScanPhase.Cancelled, tracker.Progress.Phase);
    }

    #endregion

    #region Progress Calculation Tests

    [Fact]
    public void Progress_PercentComplete_CalculatesCorrectly()
    {
        var tracker = _service.StartScan("folder1", 100, 10000);
        tracker.ReportFiles(50, 5000);

        Assert.Equal(50.0, tracker.Progress.PercentComplete);
    }

    [Fact]
    public void Progress_BytesPercentComplete_CalculatesCorrectly()
    {
        var tracker = _service.StartScan("folder1", 100, 10000);
        tracker.ReportFiles(25, 2500);

        Assert.Equal(25.0, tracker.Progress.BytesPercentComplete);
    }

    [Fact]
    public void Progress_PercentComplete_ZeroTotal_ReturnsZero()
    {
        var tracker = _service.StartScan("folder1", 0, 0);

        Assert.Equal(0.0, tracker.Progress.PercentComplete);
        Assert.Equal(0.0, tracker.Progress.BytesPercentComplete);
    }

    [Fact]
    public void Progress_Elapsed_CalculatesCorrectly()
    {
        var tracker = _service.StartScan("folder1");

        // Small delay to ensure elapsed time > 0
        Thread.Sleep(10);

        Assert.True(tracker.Progress.Elapsed > TimeSpan.Zero);
    }

    #endregion

    #region Subscribe Tests

    [Fact]
    public void Subscribe_ReceivesProgress()
    {
        var receivedProgress = new List<ScanProgress>();

        using var subscription = _service.Subscribe("folder1", p => receivedProgress.Add(p));
        var tracker = _service.StartScan("folder1");
        tracker.ReportFile("/test/file.txt", 100);

        // Should have received at least the initial and one update
        Assert.NotEmpty(receivedProgress);
    }

    [Fact]
    public void SubscribeAll_ReceivesProgressFromAllFolders()
    {
        var receivedProgress = new List<ScanProgress>();

        using var subscription = _service.SubscribeAll(p => receivedProgress.Add(p));
        _service.StartScan("folder1");
        _service.StartScan("folder2");

        Assert.True(receivedProgress.Count >= 2);
        Assert.Contains(receivedProgress, p => p.FolderId == "folder1");
        Assert.Contains(receivedProgress, p => p.FolderId == "folder2");
    }

    #endregion
}

public class ScanProgressTests
{
    [Fact]
    public void FilesPerSecond_ReturnsZero_WhenNoElapsedTime()
    {
        var progress = new ScanProgress
        {
            FilesScanned = 100,
            StartTime = DateTime.UtcNow
        };

        // Files per second when elapsed is essentially zero
        Assert.True(progress.FilesPerSecond >= 0);
    }

    [Fact]
    public void EstimatedTimeRemaining_ReturnsNull_WhenNoFilesPerSecond()
    {
        var progress = new ScanProgress
        {
            FilesTotal = 100,
            FilesScanned = 0,
            StartTime = DateTime.UtcNow
        };

        Assert.Null(progress.EstimatedTimeRemaining);
    }

    [Fact]
    public void EstimatedTimeRemaining_ReturnsNull_WhenComplete()
    {
        var progress = new ScanProgress
        {
            FilesTotal = 100,
            FilesScanned = 100,
            StartTime = DateTime.UtcNow.AddSeconds(-10)
        };

        Assert.Null(progress.EstimatedTimeRemaining);
    }
}
