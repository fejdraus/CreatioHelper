using CreatioHelper.Infrastructure.Services.Sync.Events;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.Events;

public class ScanProgressServiceTests
{
    private readonly Mock<ILogger<ScanProgressService>> _loggerMock;
    private readonly ScanProgressConfiguration _config;
    private readonly ScanProgressService _service;

    public ScanProgressServiceTests()
    {
        _loggerMock = new Mock<ILogger<ScanProgressService>>();
        _config = new ScanProgressConfiguration();
        _service = new ScanProgressService(_loggerMock.Object, _config);
    }

    #region StartScan Tests

    [Fact]
    public void StartScan_ReturnsOperation()
    {
        var operation = _service.StartScan("folder1", ScanType.Full);

        Assert.NotNull(operation);
        Assert.Equal("folder1", operation.FolderId);
        Assert.Equal(ScanType.Full, operation.Type);
        Assert.NotEmpty(operation.ScanId);
    }

    [Fact]
    public void StartScan_CreatesActiveProgress()
    {
        var operation = _service.StartScan("folder1", ScanType.Full);

        var progress = _service.GetProgress("folder1");

        Assert.NotNull(progress);
        Assert.Equal(operation.ScanId, progress.ScanId);
    }

    [Fact]
    public void StartScan_RaisesStartedEvent()
    {
        ScanProgressEvent? receivedEvent = null;
        using var sub = _service.Subscribe(e => receivedEvent = e);

        _service.StartScan("folder1", ScanType.Full);

        Assert.NotNull(receivedEvent);
        Assert.Equal(ScanProgressEventType.Started, receivedEvent.EventType);
    }

    [Fact]
    public void StartScan_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.StartScan(null!, ScanType.Full));
    }

    #endregion

    #region UpdateProgress Tests

    [Fact]
    public void UpdateProgress_UpdatesActiveProgress()
    {
        var operation = _service.StartScan("folder1", ScanType.Full);

        _service.UpdateProgress(operation.ScanId, 100, 1024000, "file.txt");

        var progress = _service.GetProgress("folder1");
        Assert.Equal(100, progress!.FilesScanned);
        Assert.Equal(1024000, progress.BytesScanned);
        Assert.Equal("file.txt", progress.CurrentFile);
    }

    [Fact]
    public void UpdateProgress_EmitsProgressEvent()
    {
        var operation = _service.StartScan("folder1", ScanType.Full);
        ScanProgressEvent? receivedEvent = null;
        using var sub = _service.Subscribe(e =>
        {
            if (e.EventType == ScanProgressEventType.Progress)
                receivedEvent = e;
        });

        _service.UpdateProgress(operation.ScanId, 100, 1024000);

        Assert.NotNull(receivedEvent);
        Assert.Equal(ScanProgressEventType.Progress, receivedEvent.EventType);
    }

    [Fact]
    public void UpdateProgress_InvalidScanId_NoOp()
    {
        // Should not throw
        _service.UpdateProgress("invalid", 100, 1024000);
    }

    [Fact]
    public void UpdateProgress_NullScanId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.UpdateProgress(null!, 100, 1024000));
    }

    #endregion

    #region CompleteScan Tests

    [Fact]
    public void CompleteScan_RemovesFromActive()
    {
        var operation = _service.StartScan("folder1", ScanType.Full);

        _service.CompleteScan(operation.ScanId, ScanResult.Success);

        Assert.Null(_service.GetProgress("folder1"));
    }

    [Fact]
    public void CompleteScan_AddsToHistory()
    {
        var operation = _service.StartScan("folder1", ScanType.Full);
        _service.UpdateProgress(operation.ScanId, 100, 1024000);

        _service.CompleteScan(operation.ScanId, ScanResult.Success);

        var history = _service.GetHistory("folder1");
        Assert.Single(history);
        Assert.Equal(ScanResult.Success, history[0].Result);
    }

    [Fact]
    public void CompleteScan_UpdatesStatistics()
    {
        var operation = _service.StartScan("folder1", ScanType.Full);
        _service.UpdateProgress(operation.ScanId, 100, 1024000);

        _service.CompleteScan(operation.ScanId, ScanResult.Success);

        var stats = _service.GetStatistics("folder1");
        Assert.Equal(1, stats.TotalScans);
        Assert.Equal(1, stats.SuccessfulScans);
        Assert.Equal(100, stats.TotalFilesScanned);
    }

    [Fact]
    public void CompleteScan_RaisesCompletedEvent()
    {
        var operation = _service.StartScan("folder1", ScanType.Full);
        ScanProgressEvent? receivedEvent = null;
        using var sub = _service.Subscribe(e =>
        {
            if (e.EventType == ScanProgressEventType.Completed)
                receivedEvent = e;
        });

        _service.CompleteScan(operation.ScanId, ScanResult.Success);

        Assert.NotNull(receivedEvent);
        Assert.Equal(ScanProgressEventType.Completed, receivedEvent.EventType);
        Assert.Equal(ScanResult.Success, receivedEvent.Result);
    }

    [Fact]
    public void CompleteScan_Failed_TracksFailure()
    {
        var operation = _service.StartScan("folder1", ScanType.Full);

        _service.CompleteScan(operation.ScanId, ScanResult.Failed);

        var stats = _service.GetStatistics("folder1");
        Assert.Equal(1, stats.FailedScans);
    }

    [Fact]
    public void CompleteScan_NullScanId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.CompleteScan(null!, ScanResult.Success));
    }

    #endregion

    #region CancelScan Tests

    [Fact]
    public void CancelScan_CompleteAsCancelled()
    {
        var operation = _service.StartScan("folder1", ScanType.Full);

        _service.CancelScan(operation.ScanId);

        var stats = _service.GetStatistics("folder1");
        Assert.Equal(1, stats.CancelledScans);
    }

    [Fact]
    public void CancelScan_RaisesCancelledEvent()
    {
        var operation = _service.StartScan("folder1", ScanType.Full);
        ScanProgressEvent? receivedEvent = null;
        using var sub = _service.Subscribe(e =>
        {
            if (e.EventType == ScanProgressEventType.Cancelled)
                receivedEvent = e;
        });

        _service.CancelScan(operation.ScanId);

        Assert.NotNull(receivedEvent);
        Assert.Equal(ScanProgressEventType.Cancelled, receivedEvent.EventType);
    }

    [Fact]
    public void CancelScan_NullScanId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.CancelScan(null!));
    }

    #endregion

    #region GetProgress Tests

    [Fact]
    public void GetProgress_NoActiveScan_ReturnsNull()
    {
        var progress = _service.GetProgress("folder1");

        Assert.Null(progress);
    }

    [Fact]
    public void GetProgress_WithActiveScan_ReturnsProgress()
    {
        var operation = _service.StartScan("folder1", ScanType.Full);

        var progress = _service.GetProgress("folder1");

        Assert.NotNull(progress);
        Assert.Equal(operation.ScanId, progress.ScanId);
    }

    [Fact]
    public void GetProgress_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetProgress(null!));
    }

    #endregion

    #region GetActiveScans Tests

    [Fact]
    public void GetActiveScans_NoScans_ReturnsEmpty()
    {
        var scans = _service.GetActiveScans();

        Assert.Empty(scans);
    }

    [Fact]
    public void GetActiveScans_WithScans_ReturnsAll()
    {
        _service.StartScan("folder1", ScanType.Full);
        _service.StartScan("folder2", ScanType.Quick);

        var scans = _service.GetActiveScans();

        Assert.Equal(2, scans.Count);
    }

    #endregion

    #region IsScanning Tests

    [Fact]
    public void IsScanning_NoScan_ReturnsFalse()
    {
        Assert.False(_service.IsScanning("folder1"));
    }

    [Fact]
    public void IsScanning_ActiveScan_ReturnsTrue()
    {
        _service.StartScan("folder1", ScanType.Full);

        Assert.True(_service.IsScanning("folder1"));
    }

    [Fact]
    public void IsScanning_AfterComplete_ReturnsFalse()
    {
        var operation = _service.StartScan("folder1", ScanType.Full);
        _service.CompleteScan(operation.ScanId, ScanResult.Success);

        Assert.False(_service.IsScanning("folder1"));
    }

    [Fact]
    public void IsScanning_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.IsScanning(null!));
    }

    #endregion

    #region Subscribe Tests

    [Fact]
    public void Subscribe_ReceivesEvents()
    {
        var events = new List<ScanProgressEvent>();
        using var sub = _service.Subscribe(e => events.Add(e));

        var operation = _service.StartScan("folder1", ScanType.Full);
        _service.UpdateProgress(operation.ScanId, 50, 500000);
        _service.CompleteScan(operation.ScanId, ScanResult.Success);

        Assert.True(events.Count >= 2); // At least Started and Completed
    }

    [Fact]
    public void Subscribe_Dispose_StopsReceiving()
    {
        var events = new List<ScanProgressEvent>();
        var sub = _service.Subscribe(e => events.Add(e));

        sub.Dispose();

        _service.StartScan("folder1", ScanType.Full);

        Assert.Empty(events);
    }

    [Fact]
    public void Subscribe_NullHandler_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.Subscribe(null!));
    }

    #endregion

    #region GetHistory Tests

    [Fact]
    public void GetHistory_NoHistory_ReturnsEmpty()
    {
        var history = _service.GetHistory("folder1");

        Assert.Empty(history);
    }

    [Fact]
    public void GetHistory_WithHistory_ReturnsEntries()
    {
        var operation = _service.StartScan("folder1", ScanType.Full);
        _service.CompleteScan(operation.ScanId, ScanResult.Success);

        var history = _service.GetHistory("folder1");

        Assert.Single(history);
    }

    [Fact]
    public void GetHistory_RespectsLimit()
    {
        for (int i = 0; i < 20; i++)
        {
            var op = _service.StartScan("folder1", ScanType.Full);
            _service.CompleteScan(op.ScanId, ScanResult.Success);
        }

        var history = _service.GetHistory("folder1", 5);

        Assert.Equal(5, history.Count);
    }

    [Fact]
    public void GetHistory_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetHistory(null!));
    }

    #endregion

    #region GetStatistics Tests

    [Fact]
    public void GetStatistics_Initial_ReturnsEmptyStats()
    {
        var stats = _service.GetStatistics("folder1");

        Assert.Equal("folder1", stats.FolderId);
        Assert.Equal(0, stats.TotalScans);
        Assert.Equal(0, stats.SuccessfulScans);
    }

    [Fact]
    public void GetStatistics_TracksAverageScanTime()
    {
        var op1 = _service.StartScan("folder1", ScanType.Full);
        Thread.Sleep(10); // Small delay
        _service.CompleteScan(op1.ScanId, ScanResult.Success);

        var stats = _service.GetStatistics("folder1");

        Assert.True(stats.AverageScanTime > TimeSpan.Zero);
    }

    [Fact]
    public void GetStatistics_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetStatistics(null!));
    }

    #endregion
}

public class ScanProgressTests
{
    [Fact]
    public void ProgressPercent_CalculatesCorrectly()
    {
        var progress = new ScanProgress
        {
            FilesScanned = 50,
            TotalFiles = 100
        };

        Assert.Equal(50.0, progress.ProgressPercent);
    }

    [Fact]
    public void ProgressPercent_NoTotal_ReturnsZero()
    {
        var progress = new ScanProgress
        {
            FilesScanned = 50,
            TotalFiles = null
        };

        Assert.Equal(0.0, progress.ProgressPercent);
    }
}

public class ScanProgressConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new ScanProgressConfiguration();

        Assert.Equal(TimeSpan.FromMilliseconds(100), config.MinProgressInterval);
        Assert.Equal(100, config.MaxHistoryEntries);
        Assert.True(config.EmitProgressEvents);
    }
}
