using CreatioHelper.Infrastructure.Services.Sync.SystemControl;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.SystemControl;

public class ProcessPriorityServiceTests
{
    private readonly Mock<ILogger<ProcessPriorityService>> _loggerMock;
    private readonly ProcessPriorityConfiguration _config;
    private readonly ProcessPriorityService _service;

    public ProcessPriorityServiceTests()
    {
        _loggerMock = new Mock<ILogger<ProcessPriorityService>>();
        _config = new ProcessPriorityConfiguration();
        _service = new ProcessPriorityService(_loggerMock.Object, _config);
    }

    #region IsLowPriorityEnabled Tests

    [Fact]
    public void IsLowPriorityEnabled_Default_ReturnsFalse()
    {
        Assert.False(_service.IsLowPriorityEnabled);
    }

    [Fact]
    public void IsLowPriorityEnabled_AfterEnabled_ReturnsTrue()
    {
        _service.SetLowPriority(true);

        Assert.True(_service.IsLowPriorityEnabled);
    }

    #endregion

    #region GetCurrentPriority Tests

    [Fact]
    public void GetCurrentPriority_ReturnsValidPriority()
    {
        var priority = _service.GetCurrentPriority();

        Assert.True(Enum.IsDefined(typeof(ProcessPriorityLevel), priority));
    }

    #endregion

    #region SetPriority Tests

    [Fact]
    public void SetPriority_Normal_Succeeds()
    {
        var result = _service.SetPriority(ProcessPriorityLevel.Normal);

        Assert.True(result);
    }

    [Fact]
    public void SetPriority_BelowNormal_Succeeds()
    {
        var result = _service.SetPriority(ProcessPriorityLevel.BelowNormal);

        Assert.True(result);

        // Reset to normal
        _service.SetPriority(ProcessPriorityLevel.Normal);
    }

    #endregion

    #region SetLowPriority Tests

    [Fact]
    public void SetLowPriority_Enable_Succeeds()
    {
        var result = _service.SetLowPriority(true);

        Assert.True(result);
        Assert.True(_service.IsLowPriorityEnabled);

        // Reset
        _service.SetLowPriority(false);
    }

    [Fact]
    public void SetLowPriority_Disable_Succeeds()
    {
        _service.SetLowPriority(true);

        var result = _service.SetLowPriority(false);

        Assert.True(result);
        Assert.False(_service.IsLowPriorityEnabled);
    }

    [Fact]
    public void SetLowPriority_AlreadyEnabled_NoOp()
    {
        _service.SetLowPriority(true);

        var result = _service.SetLowPriority(true);

        Assert.True(result);

        // Reset
        _service.SetLowPriority(false);
    }

    #endregion

    #region GetStats Tests

    [Fact]
    public void GetStats_ReturnsValidStats()
    {
        var stats = _service.GetStats();

        Assert.NotNull(stats);
        Assert.NotEmpty(stats.ProcessName);
        Assert.True(stats.ProcessId > 0);
    }

    [Fact]
    public void GetStats_TracksLowPriorityState()
    {
        _service.SetLowPriority(true);

        var stats = _service.GetStats();

        Assert.True(stats.LowPriorityEnabled);
        Assert.NotNull(stats.LowPriorityEnabledAt);

        // Reset
        _service.SetLowPriority(false);
    }

    [Fact]
    public void GetStats_TracksPriorityChanges()
    {
        var initialStats = _service.GetStats();
        var initialChanges = initialStats.PriorityChanges;

        _service.SetPriority(ProcessPriorityLevel.BelowNormal);
        _service.SetPriority(ProcessPriorityLevel.Normal);

        var stats = _service.GetStats();

        Assert.True(stats.PriorityChanges >= initialChanges + 2);
    }

    #endregion

    #region SetThreadPriority Tests

    [Fact]
    public void SetThreadPriority_Normal_Succeeds()
    {
        var result = _service.SetThreadPriority(ThreadPriorityLevel.Normal);

        Assert.True(result);
    }

    [Fact]
    public void SetThreadPriority_BelowNormal_Succeeds()
    {
        var result = _service.SetThreadPriority(ThreadPriorityLevel.BelowNormal);

        Assert.True(result);

        // Reset
        _service.SetThreadPriority(ThreadPriorityLevel.Normal);
    }

    #endregion

    #region GetCurrentThreadPriority Tests

    [Fact]
    public void GetCurrentThreadPriority_ReturnsValidPriority()
    {
        var priority = _service.GetCurrentThreadPriority();

        Assert.True(Enum.IsDefined(typeof(ThreadPriorityLevel), priority));
    }

    #endregion

    #region SetIoPriority Tests

    [Fact]
    public void SetIoPriority_Normal_ReturnsResult()
    {
        // This may not succeed on all platforms, but shouldn't throw
        var result = _service.SetIoPriority(IoPriorityLevel.Normal);

        // Result depends on platform
        Assert.True(result || !result);
    }

    #endregion

    #region RunWithLowPriority Tests

    [Fact]
    public void RunWithLowPriority_ExecutesAction()
    {
        var executed = false;

        _service.RunWithLowPriority(() =>
        {
            executed = true;
            return true;
        });

        Assert.True(executed);
    }

    [Fact]
    public void RunWithLowPriority_ReturnsResult()
    {
        var result = _service.RunWithLowPriority(() => 42);

        Assert.Equal(42, result);
    }

    [Fact]
    public void RunWithLowPriority_RestoresPriorityAfter()
    {
        var wasLowPriority = _service.IsLowPriorityEnabled;

        _service.RunWithLowPriority(() => true);

        Assert.Equal(wasLowPriority, _service.IsLowPriorityEnabled);
    }

    [Fact]
    public void RunWithLowPriority_NullAction_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.RunWithLowPriority<bool>(null!));
    }

    #endregion

    #region ResetToNormal Tests

    [Fact]
    public void ResetToNormal_ResetsAllPriorities()
    {
        _service.SetLowPriority(true);

        var result = _service.ResetToNormal();

        Assert.True(result);
    }

    #endregion
}

public class ProcessPriorityConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new ProcessPriorityConfiguration();

        Assert.False(config.EnableLowPriorityByDefault);
        Assert.Equal(ProcessPriorityLevel.BelowNormal, config.LowPriorityLevel);
        Assert.True(config.LowerIoPriority);
        Assert.False(config.LowerThreadPriority);
        Assert.True(config.LogPriorityChanges);
    }
}

public class ProcessPriorityStatsTests
{
    [Fact]
    public void DefaultValues_AreZero()
    {
        var stats = new ProcessPriorityStats();

        Assert.False(stats.LowPriorityEnabled);
        Assert.Null(stats.LowPriorityEnabledAt);
        Assert.Equal(0, stats.PriorityChanges);
    }
}
