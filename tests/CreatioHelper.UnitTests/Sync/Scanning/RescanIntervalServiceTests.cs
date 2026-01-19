using CreatioHelper.Infrastructure.Services.Sync.Scanning;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.Scanning;

public class RescanIntervalServiceTests : IDisposable
{
    private readonly Mock<ILogger<RescanIntervalService>> _loggerMock;
    private readonly RescanIntervalConfiguration _config;
    private readonly RescanIntervalService _service;

    public RescanIntervalServiceTests()
    {
        _loggerMock = new Mock<ILogger<RescanIntervalService>>();
        _config = new RescanIntervalConfiguration();
        _service = new RescanIntervalService(_loggerMock.Object, _config);
    }

    public void Dispose()
    {
        _service.Dispose();
    }

    #region GetRescanInterval Tests

    [Fact]
    public void GetRescanInterval_NotSet_ReturnsDefault()
    {
        var interval = _service.GetRescanInterval("folder1");

        Assert.Equal(_config.DefaultRescanInterval, interval);
    }

    [Fact]
    public void GetRescanInterval_AfterSet_ReturnsSetValue()
    {
        var expected = TimeSpan.FromMinutes(30);
        _service.SetRescanInterval("folder1", expected);

        var interval = _service.GetRescanInterval("folder1");

        Assert.Equal(expected, interval);
    }

    [Fact]
    public void GetRescanInterval_NullFolderId_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetRescanInterval(null!));
    }

    #endregion

    #region SetRescanInterval Tests

    [Fact]
    public void SetRescanInterval_ValidInterval_SetsCorrectly()
    {
        var interval = TimeSpan.FromMinutes(15);

        _service.SetRescanInterval("folder1", interval);

        Assert.Equal(interval, _service.GetRescanInterval("folder1"));
    }

    [Fact]
    public void SetRescanInterval_SubSecond_AllowedWithConfig()
    {
        var interval = TimeSpan.FromMilliseconds(500);

        _service.SetRescanInterval("folder1", interval);

        Assert.Equal(interval, _service.GetRescanInterval("folder1"));
    }

    [Fact]
    public void SetRescanInterval_BelowMinimum_ClampedToMinimum()
    {
        var tooSmall = TimeSpan.FromMilliseconds(10);

        _service.SetRescanInterval("folder1", tooSmall);

        var actual = _service.GetRescanInterval("folder1");
        Assert.True(actual >= TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void SetRescanInterval_AboveMaximum_ClampedToMaximum()
    {
        var tooLarge = TimeSpan.FromDays(500);

        _service.SetRescanInterval("folder1", tooLarge);

        var actual = _service.GetRescanInterval("folder1");
        Assert.True(actual <= TimeSpan.FromDays(365));
    }

    #endregion

    #region FsWatcherDelay Tests

    [Fact]
    public void GetFsWatcherDelay_NotSet_ReturnsDefault()
    {
        var delay = _service.GetFsWatcherDelay("folder1");

        Assert.Equal(_config.DefaultFsWatcherDelay, delay);
    }

    [Fact]
    public void SetFsWatcherDelay_ValidDelay_SetsCorrectly()
    {
        var delay = TimeSpan.FromSeconds(5);

        _service.SetFsWatcherDelay("folder1", delay);

        Assert.Equal(delay, _service.GetFsWatcherDelay("folder1"));
    }

    #endregion

    #region ScheduleNextRescan Tests

    [Fact]
    public async Task ScheduleNextRescanAsync_SchedulesCorrectly()
    {
        _service.SetRescanInterval("folder1", TimeSpan.FromSeconds(10));
        var before = DateTime.UtcNow;

        var scheduled = await _service.ScheduleNextRescanAsync("folder1");

        var after = DateTime.UtcNow;
        Assert.True(scheduled > before);
        Assert.True(scheduled > after);
    }

    [Fact]
    public async Task ScheduleNextRescanAsync_CancelsExisting()
    {
        _service.SetRescanInterval("folder1", TimeSpan.FromHours(1));

        var first = await _service.ScheduleNextRescanAsync("folder1");
        var second = await _service.ScheduleNextRescanAsync("folder1");

        // Second schedule should have a different time
        Assert.NotEqual(first, second);
    }

    #endregion

    #region CancelScheduledRescan Tests

    [Fact]
    public async Task CancelScheduledRescan_CancelsSchedule()
    {
        _service.SetRescanInterval("folder1", TimeSpan.FromHours(1));
        await _service.ScheduleNextRescanAsync("folder1");

        _service.CancelScheduledRescan("folder1");

        var timeUntil = _service.GetTimeUntilNextRescan("folder1");
        Assert.Null(timeUntil);
    }

    [Fact]
    public void CancelScheduledRescan_NotScheduled_NoError()
    {
        // Should not throw
        _service.CancelScheduledRescan("folder1");
    }

    #endregion

    #region GetTimeUntilNextRescan Tests

    [Fact]
    public void GetTimeUntilNextRescan_NotScheduled_ReturnsNull()
    {
        var result = _service.GetTimeUntilNextRescan("folder1");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetTimeUntilNextRescan_Scheduled_ReturnsPositiveTime()
    {
        _service.SetRescanInterval("folder1", TimeSpan.FromMinutes(10));
        await _service.ScheduleNextRescanAsync("folder1");

        var timeUntil = _service.GetTimeUntilNextRescan("folder1");

        Assert.NotNull(timeUntil);
        Assert.True(timeUntil.Value > TimeSpan.Zero);
    }

    #endregion

    #region TriggerImmediateRescan Tests

    [Fact]
    public async Task TriggerImmediateRescanAsync_UpdatesStats()
    {
        await _service.TriggerImmediateRescanAsync("folder1");

        var stats = _service.GetStats("folder1");

        Assert.Equal(1, stats.TotalRescans);
        Assert.NotNull(stats.LastRescanTime);
    }

    [Fact]
    public async Task TriggerImmediateRescanAsync_CancelsScheduled()
    {
        _service.SetRescanInterval("folder1", TimeSpan.FromHours(1));
        await _service.ScheduleNextRescanAsync("folder1");

        await _service.TriggerImmediateRescanAsync("folder1");

        var timeUntil = _service.GetTimeUntilNextRescan("folder1");
        Assert.Null(timeUntil);
    }

    #endregion

    #region OnRescanDue Tests

    [Fact]
    public async Task OnRescanDue_NotifiesOnTrigger()
    {
        var notified = false;
        string? notifiedFolderId = null;

        using var subscription = _service.OnRescanDue(folderId =>
        {
            notified = true;
            notifiedFolderId = folderId;
        });

        await _service.TriggerImmediateRescanAsync("folder1");

        Assert.True(notified);
        Assert.Equal("folder1", notifiedFolderId);
    }

    #endregion

    #region GetStats Tests

    [Fact]
    public void GetStats_NotRecorded_ReturnsEmptyStats()
    {
        var stats = _service.GetStats("folder1");

        Assert.Equal("folder1", stats.FolderId);
        Assert.Equal(0, stats.TotalRescans);
        Assert.Null(stats.LastRescanTime);
    }

    [Fact]
    public async Task GetStats_AfterTrigger_ReturnsUpdatedStats()
    {
        await _service.TriggerImmediateRescanAsync("folder1");
        await _service.TriggerImmediateRescanAsync("folder1");

        var stats = _service.GetStats("folder1");

        Assert.Equal(2, stats.TotalRescans);
    }

    #endregion
}

public class RescanIntervalConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new RescanIntervalConfiguration();

        Assert.Equal(TimeSpan.FromHours(1), config.DefaultRescanInterval);
        Assert.Equal(TimeSpan.FromSeconds(10), config.DefaultFsWatcherDelay);
        Assert.True(config.EnableSubSecondIntervals);
        Assert.False(config.EnableAdaptiveIntervals);
    }
}

public class AdaptiveRescanIntervalAdjusterTests
{
    private readonly Mock<IRescanIntervalService> _serviceMock;
    private readonly RescanIntervalConfiguration _config;
    private readonly Mock<ILogger<AdaptiveRescanIntervalAdjuster>> _loggerMock;
    private readonly AdaptiveRescanIntervalAdjuster _adjuster;

    public AdaptiveRescanIntervalAdjusterTests()
    {
        _serviceMock = new Mock<IRescanIntervalService>();
        _config = new RescanIntervalConfiguration { EnableAdaptiveIntervals = true };
        _loggerMock = new Mock<ILogger<AdaptiveRescanIntervalAdjuster>>();
        _adjuster = new AdaptiveRescanIntervalAdjuster(_serviceMock.Object, _config, _loggerMock.Object);
    }

    [Fact]
    public void RecordChangesFound_DecreasesInterval()
    {
        var currentInterval = TimeSpan.FromMinutes(10);
        _serviceMock.Setup(s => s.GetRescanInterval("folder1")).Returns(currentInterval);

        _adjuster.RecordChangesFound("folder1", 5);

        _serviceMock.Verify(s => s.SetRescanInterval(
            "folder1",
            It.Is<TimeSpan>(t => t < currentInterval)),
            Times.Once);
    }

    [Fact]
    public void RecordNoChanges_AfterMultipleIdle_IncreasesInterval()
    {
        var currentInterval = TimeSpan.FromMinutes(10);
        _serviceMock.Setup(s => s.GetRescanInterval("folder1")).Returns(currentInterval);

        // Record 3 idle scans
        _adjuster.RecordNoChanges("folder1");
        _adjuster.RecordNoChanges("folder1");
        _adjuster.RecordNoChanges("folder1");

        _serviceMock.Verify(s => s.SetRescanInterval(
            "folder1",
            It.Is<TimeSpan>(t => t > currentInterval)),
            Times.Once);
    }

    [Fact]
    public void RecordChangesFound_DisabledConfig_DoesNothing()
    {
        _config.EnableAdaptiveIntervals = false;

        _adjuster.RecordChangesFound("folder1", 5);

        _serviceMock.Verify(s => s.SetRescanInterval(It.IsAny<string>(), It.IsAny<TimeSpan>()), Times.Never);
    }
}
