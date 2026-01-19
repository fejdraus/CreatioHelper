using CreatioHelper.Infrastructure.Services.Sync.FileSystem;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.FileSystem;

public class ModificationTimeServiceTests
{
    private readonly Mock<ILogger<ModificationTimeService>> _loggerMock;
    private readonly ModificationTimeConfiguration _config;
    private readonly ModificationTimeService _service;

    public ModificationTimeServiceTests()
    {
        _loggerMock = new Mock<ILogger<ModificationTimeService>>();
        _config = new ModificationTimeConfiguration();
        _service = new ModificationTimeService(_loggerMock.Object, _config);
    }

    #region AreTimesEqual Tests

    [Fact]
    public void AreTimesEqual_ExactMatch_ReturnsTrue()
    {
        var time = DateTime.UtcNow;

        Assert.True(_service.AreTimesEqual("folder1", time, time));
    }

    [Fact]
    public void AreTimesEqual_WithinWindow_ReturnsTrue()
    {
        var time1 = DateTime.UtcNow;
        var time2 = time1.AddSeconds(1); // Within 2-second default window

        Assert.True(_service.AreTimesEqual("folder1", time1, time2));
    }

    [Fact]
    public void AreTimesEqual_OutsideWindow_ReturnsFalse()
    {
        var time1 = DateTime.UtcNow;
        var time2 = time1.AddSeconds(5); // Outside 2-second default window

        Assert.False(_service.AreTimesEqual("folder1", time1, time2));
    }

    [Fact]
    public void AreTimesEqual_CustomWindow_Respected()
    {
        _service.SetTimeWindow("folder1", TimeSpan.FromSeconds(10));

        var time1 = DateTime.UtcNow;
        var time2 = time1.AddSeconds(5);

        Assert.True(_service.AreTimesEqual("folder1", time1, time2));
    }

    [Fact]
    public void AreTimesEqual_NegativeDiff_ReturnsTrue()
    {
        var time1 = DateTime.UtcNow;
        var time2 = time1.AddSeconds(-1); // Negative difference

        Assert.True(_service.AreTimesEqual("folder1", time1, time2));
    }

    [Fact]
    public void AreTimesEqual_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.AreTimesEqual(null!, DateTime.UtcNow, DateTime.UtcNow));
    }

    #endregion

    #region GetTimeWindow Tests

    [Fact]
    public void GetTimeWindow_Default_Returns2Seconds()
    {
        var window = _service.GetTimeWindow("folder1");

        Assert.Equal(TimeSpan.FromSeconds(2), window);
    }

    [Fact]
    public void GetTimeWindow_AfterSet_ReturnsSetValue()
    {
        _service.SetTimeWindow("folder1", TimeSpan.FromSeconds(5));

        var window = _service.GetTimeWindow("folder1");

        Assert.Equal(TimeSpan.FromSeconds(5), window);
    }

    [Fact]
    public void GetTimeWindow_FatCompatibility_Returns2Seconds()
    {
        var config = new ModificationTimeConfiguration { FatCompatibilityMode = true };
        var service = new ModificationTimeService(_loggerMock.Object, config);

        var window = service.GetTimeWindow("folder1");

        Assert.Equal(TimeSpan.FromSeconds(2), window);
    }

    [Fact]
    public void GetTimeWindow_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetTimeWindow(null!));
    }

    #endregion

    #region SetTimeWindow Tests

    [Fact]
    public void SetTimeWindow_ValidWindow_Sets()
    {
        _service.SetTimeWindow("folder1", TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(10), _service.GetTimeWindow("folder1"));
    }

    [Fact]
    public void SetTimeWindow_NegativeWindow_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _service.SetTimeWindow("folder1", TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void SetTimeWindow_ExceedsMax_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _service.SetTimeWindow("folder1", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void SetTimeWindow_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.SetTimeWindow(null!, TimeSpan.FromSeconds(5)));
    }

    #endregion

    #region ResetTimeWindow Tests

    [Fact]
    public void ResetTimeWindow_AfterSet_ResetsToDefault()
    {
        _service.SetTimeWindow("folder1", TimeSpan.FromSeconds(10));

        _service.ResetTimeWindow("folder1");

        Assert.Equal(TimeSpan.FromSeconds(2), _service.GetTimeWindow("folder1"));
    }

    [Fact]
    public void ResetTimeWindow_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ResetTimeWindow(null!));
    }

    #endregion

    #region NormalizeTime Tests

    [Fact]
    public void NormalizeTime_SecondPrecision_TruncatesMilliseconds()
    {
        var time = new DateTime(2024, 1, 15, 10, 30, 45, 500, DateTimeKind.Utc);

        var normalized = _service.NormalizeTime("folder1", time);

        Assert.Equal(0, normalized.Millisecond);
        Assert.Equal(45, normalized.Second);
    }

    [Fact]
    public void NormalizeTime_FatCompatibility_TruncatesToTwoSeconds()
    {
        var config = new ModificationTimeConfiguration { FatCompatibilityMode = true };
        var service = new ModificationTimeService(_loggerMock.Object, config);

        var time = new DateTime(2024, 1, 15, 10, 30, 45, 500, DateTimeKind.Utc);

        var normalized = service.NormalizeTime("folder1", time);

        Assert.Equal(44, normalized.Second); // Truncated to even seconds
    }

    [Fact]
    public void NormalizeTime_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.NormalizeTime(null!, DateTime.UtcNow));
    }

    #endregion

    #region NeedsSync Tests

    [Fact]
    public void NeedsSync_TimesEqual_ReturnsFalse()
    {
        var time = DateTime.UtcNow;

        Assert.False(_service.NeedsSync("folder1", time, time));
    }

    [Fact]
    public void NeedsSync_RemoteNewer_ReturnsTrue()
    {
        var local = DateTime.UtcNow;
        var remote = local.AddSeconds(5);

        Assert.True(_service.NeedsSync("folder1", local, remote));
    }

    [Fact]
    public void NeedsSync_LocalNewer_ReturnsFalse()
    {
        var local = DateTime.UtcNow;
        var remote = local.AddSeconds(-5);

        Assert.False(_service.NeedsSync("folder1", local, remote));
    }

    [Fact]
    public void NeedsSync_WithinWindow_ReturnsFalse()
    {
        var local = DateTime.UtcNow;
        var remote = local.AddSeconds(1); // Within window but technically newer

        Assert.False(_service.NeedsSync("folder1", local, remote));
    }

    [Fact]
    public void NeedsSync_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.NeedsSync(null!, DateTime.UtcNow, DateTime.UtcNow));
    }

    #endregion

    #region GetStats Tests

    [Fact]
    public void GetStats_Initial_ReturnsEmptyStats()
    {
        var stats = _service.GetStats("folder1");

        Assert.Equal("folder1", stats.FolderId);
        Assert.Equal(0, stats.ComparisonsCount);
        Assert.Equal(0, stats.ExactMatches);
        Assert.Equal(0, stats.MatchesWithinWindow);
        Assert.Equal(0, stats.Mismatches);
    }

    [Fact]
    public void GetStats_AfterComparisons_TracksCorrectly()
    {
        var time = DateTime.UtcNow;
        _service.AreTimesEqual("folder1", time, time); // Exact
        _service.AreTimesEqual("folder1", time, time.AddSeconds(1)); // Within window
        _service.AreTimesEqual("folder1", time, time.AddSeconds(5)); // Mismatch

        var stats = _service.GetStats("folder1");

        Assert.Equal(3, stats.ComparisonsCount);
        Assert.Equal(1, stats.ExactMatches);
        Assert.Equal(1, stats.MatchesWithinWindow);
        Assert.Equal(1, stats.Mismatches);
    }

    [Fact]
    public void GetStats_MatchRate_CalculatesCorrectly()
    {
        var time = DateTime.UtcNow;
        _service.AreTimesEqual("folder1", time, time); // Match
        _service.AreTimesEqual("folder1", time, time.AddSeconds(5)); // Mismatch

        var stats = _service.GetStats("folder1");

        Assert.Equal(50.0, stats.MatchRate);
    }

    [Fact]
    public void GetStats_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetStats(null!));
    }

    #endregion
}

public class ModificationTimeConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new ModificationTimeConfiguration();

        Assert.Equal(TimeSpan.FromSeconds(2), config.DefaultTimeWindow);
        Assert.Equal(TimePrecision.Second, config.DefaultPrecision);
        Assert.False(config.FatCompatibilityMode);
        Assert.Equal(TimeSpan.FromMinutes(1), config.MaxTimeWindow);
    }
}
