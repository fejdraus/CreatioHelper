using CreatioHelper.Infrastructure.Services.Sync.DeviceManagement;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.DeviceManagement;

public class SkipIntroductionRemovalsServiceTests
{
    private readonly Mock<ILogger<SkipIntroductionRemovalsService>> _loggerMock;
    private readonly SkipIntroductionRemovalsConfiguration _config;
    private readonly SkipIntroductionRemovalsService _service;

    public SkipIntroductionRemovalsServiceTests()
    {
        _loggerMock = new Mock<ILogger<SkipIntroductionRemovalsService>>();
        _config = new SkipIntroductionRemovalsConfiguration();
        _service = new SkipIntroductionRemovalsService(_loggerMock.Object, _config);
    }

    #region ShouldSkipRemovals Tests

    [Fact]
    public void ShouldSkipRemovals_Default_ReturnsFalse()
    {
        Assert.False(_service.ShouldSkipRemovals("device1"));
    }

    [Fact]
    public void ShouldSkipRemovals_AfterEnabled_ReturnsTrue()
    {
        _service.SetSkipRemovals("device1", true);

        Assert.True(_service.ShouldSkipRemovals("device1"));
    }

    [Fact]
    public void ShouldSkipRemovals_GlobalEnabled_ReturnsTrue()
    {
        var config = new SkipIntroductionRemovalsConfiguration { GlobalSkipRemovals = true };
        var service = new SkipIntroductionRemovalsService(_loggerMock.Object, config);

        Assert.True(service.ShouldSkipRemovals("device1"));
    }

    [Fact]
    public void ShouldSkipRemovals_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ShouldSkipRemovals(null!));
    }

    #endregion

    #region SetSkipRemovals Tests

    [Fact]
    public void SetSkipRemovals_True_EnablesSkipping()
    {
        _service.SetSkipRemovals("device1", true);

        Assert.True(_service.ShouldSkipRemovals("device1"));
    }

    [Fact]
    public void SetSkipRemovals_False_DisablesSkipping()
    {
        _service.SetSkipRemovals("device1", true);
        _service.SetSkipRemovals("device1", false);

        Assert.False(_service.ShouldSkipRemovals("device1"));
    }

    [Fact]
    public void SetSkipRemovals_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.SetSkipRemovals(null!, true));
    }

    #endregion

    #region GetDevicesWithSkipRemovals Tests

    [Fact]
    public void GetDevicesWithSkipRemovals_None_ReturnsEmpty()
    {
        var devices = _service.GetDevicesWithSkipRemovals();

        Assert.Empty(devices);
    }

    [Fact]
    public void GetDevicesWithSkipRemovals_SomeEnabled_ReturnsEnabled()
    {
        _service.SetSkipRemovals("device1", true);
        _service.SetSkipRemovals("device2", false);
        _service.SetSkipRemovals("device3", true);

        var devices = _service.GetDevicesWithSkipRemovals();

        Assert.Equal(2, devices.Count);
        Assert.Contains("device1", devices);
        Assert.Contains("device3", devices);
    }

    #endregion

    #region ShouldSkipRemoval Tests

    [Fact]
    public void ShouldSkipRemoval_SkipDisabled_ReturnsApply()
    {
        var decision = _service.ShouldSkipRemoval("device1", "introducer1", RemovalType.DeviceRemoval);

        Assert.Equal(RemovalDecision.Apply, decision);
    }

    [Fact]
    public void ShouldSkipRemoval_SkipEnabled_ReturnsSkipDeviceSetting()
    {
        _service.SetSkipRemovals("device1", true);

        var decision = _service.ShouldSkipRemoval("device1", "introducer1", RemovalType.DeviceRemoval);

        Assert.Equal(RemovalDecision.SkipDeviceSetting, decision);
    }

    [Fact]
    public void ShouldSkipRemoval_GlobalSkip_ReturnsSkipGlobalSetting()
    {
        var config = new SkipIntroductionRemovalsConfiguration { GlobalSkipRemovals = true };
        var service = new SkipIntroductionRemovalsService(_loggerMock.Object, config);

        var decision = service.ShouldSkipRemoval("device1", "introducer1", RemovalType.DeviceRemoval);

        Assert.Equal(RemovalDecision.SkipGlobalSetting, decision);
    }

    [Fact]
    public void ShouldSkipRemoval_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ShouldSkipRemoval(null!, "introducer1", RemovalType.DeviceRemoval));
    }

    [Fact]
    public void ShouldSkipRemoval_NullIntroducerId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ShouldSkipRemoval("device1", null!, RemovalType.DeviceRemoval));
    }

    #endregion

    #region RecordSkippedRemoval Tests

    [Fact]
    public void RecordSkippedRemoval_UpdatesStats()
    {
        _service.SetSkipRemovals("device1", true);
        _service.RecordSkippedRemoval("device1", "introducer1", RemovalType.DeviceRemoval);

        var stats = _service.GetStats("device1");

        Assert.Equal(1, stats.RemovalsSkipped);
        Assert.NotNull(stats.LastRemovalSkipped);
    }

    [Fact]
    public void RecordSkippedRemoval_AddsToPending()
    {
        _service.SetSkipRemovals("device1", true);
        _service.RecordSkippedRemoval("device1", "introducer1", RemovalType.DeviceRemoval);

        var pending = _service.GetPendingRemovals("device1");

        Assert.Single(pending);
        Assert.Equal("device1", pending[0].DeviceId);
        Assert.Equal("introducer1", pending[0].IntroducerDeviceId);
    }

    [Fact]
    public void RecordSkippedRemoval_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.RecordSkippedRemoval(null!, "introducer1", RemovalType.DeviceRemoval));
    }

    #endregion

    #region RecordAppliedRemoval Tests

    [Fact]
    public void RecordAppliedRemoval_UpdatesStats()
    {
        _service.RecordAppliedRemoval("device1", "introducer1", RemovalType.DeviceRemoval);

        var stats = _service.GetStats("device1");

        Assert.Equal(1, stats.RemovalsApplied);
        Assert.NotNull(stats.LastRemovalApplied);
    }

    [Fact]
    public void RecordAppliedRemoval_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.RecordAppliedRemoval(null!, "introducer1", RemovalType.DeviceRemoval));
    }

    #endregion

    #region GetStats Tests

    [Fact]
    public void GetStats_Initial_ReturnsEmptyStats()
    {
        var stats = _service.GetStats("device1");

        Assert.Equal("device1", stats.DeviceId);
        Assert.Equal(0, stats.TotalRemovalsReceived);
        Assert.Equal(0, stats.RemovalsApplied);
        Assert.Equal(0, stats.RemovalsSkipped);
    }

    [Fact]
    public void GetStats_SkipRate_CalculatesCorrectly()
    {
        _service.ShouldSkipRemoval("device1", "introducer1", RemovalType.DeviceRemoval);
        _service.RecordAppliedRemoval("device1", "introducer1", RemovalType.DeviceRemoval);

        _service.SetSkipRemovals("device1", true);
        _service.ShouldSkipRemoval("device1", "introducer1", RemovalType.DeviceRemoval);
        _service.RecordSkippedRemoval("device1", "introducer1", RemovalType.DeviceRemoval);

        var stats = _service.GetStats("device1");

        Assert.Equal(50.0, stats.SkipRate);
    }

    [Fact]
    public void GetStats_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetStats(null!));
    }

    #endregion

    #region GetAllStats Tests

    [Fact]
    public void GetAllStats_ReturnsAllStats()
    {
        _service.ShouldSkipRemoval("device1", "introducer1", RemovalType.DeviceRemoval);
        _service.ShouldSkipRemoval("device2", "introducer1", RemovalType.FolderRemoval);

        var allStats = _service.GetAllStats();

        Assert.Equal(2, allStats.Count);
    }

    #endregion

    #region GetPendingRemovals Tests

    [Fact]
    public void GetPendingRemovals_NoPending_ReturnsEmpty()
    {
        var pending = _service.GetPendingRemovals("device1");

        Assert.Empty(pending);
    }

    [Fact]
    public void GetPendingRemovals_HasPending_ReturnsPending()
    {
        _service.SetSkipRemovals("device1", true);
        _service.RecordSkippedRemoval("device1", "introducer1", RemovalType.DeviceRemoval);
        _service.RecordSkippedRemoval("device1", "introducer1", RemovalType.FolderRemoval);

        var pending = _service.GetPendingRemovals("device1");

        Assert.Equal(2, pending.Count);
    }

    [Fact]
    public void GetPendingRemovals_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetPendingRemovals(null!));
    }

    #endregion

    #region ApplyPendingRemoval Tests

    [Fact]
    public void ApplyPendingRemoval_Exists_ReturnsTrue()
    {
        _service.SetSkipRemovals("device1", true);
        _service.RecordSkippedRemoval("device1", "introducer1", RemovalType.DeviceRemoval);
        var pending = _service.GetPendingRemovals("device1");
        var removalId = pending[0].Id;

        var result = _service.ApplyPendingRemoval(removalId);

        Assert.True(result);
    }

    [Fact]
    public void ApplyPendingRemoval_NotExists_ReturnsFalse()
    {
        var result = _service.ApplyPendingRemoval("nonexistent");

        Assert.False(result);
    }

    [Fact]
    public void ApplyPendingRemoval_UpdatesStats()
    {
        _service.SetSkipRemovals("device1", true);
        _service.RecordSkippedRemoval("device1", "introducer1", RemovalType.DeviceRemoval);
        var pending = _service.GetPendingRemovals("device1");

        _service.ApplyPendingRemoval(pending[0].Id);

        var stats = _service.GetStats("device1");
        Assert.Equal(1, stats.RemovalsApplied);
        Assert.Equal(0, stats.PendingRemovals);
    }

    [Fact]
    public void ApplyPendingRemoval_NullRemovalId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ApplyPendingRemoval(null!));
    }

    #endregion

    #region ClearPendingRemovals Tests

    [Fact]
    public void ClearPendingRemovals_ClearsAll()
    {
        _service.SetSkipRemovals("device1", true);
        _service.RecordSkippedRemoval("device1", "introducer1", RemovalType.DeviceRemoval);
        _service.RecordSkippedRemoval("device1", "introducer1", RemovalType.FolderRemoval);

        _service.ClearPendingRemovals("device1");

        var pending = _service.GetPendingRemovals("device1");
        Assert.Empty(pending);
    }

    [Fact]
    public void ClearPendingRemovals_UpdatesStats()
    {
        _service.SetSkipRemovals("device1", true);
        _service.RecordSkippedRemoval("device1", "introducer1", RemovalType.DeviceRemoval);

        _service.ClearPendingRemovals("device1");

        var stats = _service.GetStats("device1");
        Assert.Equal(0, stats.PendingRemovals);
    }

    [Fact]
    public void ClearPendingRemovals_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ClearPendingRemovals(null!));
    }

    #endregion

    #region Subscribe Tests

    [Fact]
    public void Subscribe_ReceivesEvents()
    {
        RemovalEvent? receivedEvent = null;
        using var sub = _service.Subscribe(e => receivedEvent = e);

        _service.RecordAppliedRemoval("device1", "introducer1", RemovalType.DeviceRemoval);

        Assert.NotNull(receivedEvent);
        Assert.Equal(RemovalEventType.RemovalApplied, receivedEvent.EventType);
    }

    [Fact]
    public void Subscribe_Dispose_StopsReceiving()
    {
        RemovalEvent? receivedEvent = null;
        var sub = _service.Subscribe(e => receivedEvent = e);

        sub.Dispose();

        _service.RecordAppliedRemoval("device1", "introducer1", RemovalType.DeviceRemoval);

        Assert.Null(receivedEvent);
    }

    [Fact]
    public void Subscribe_NullHandler_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.Subscribe(null!));
    }

    #endregion
}

public class RemovalStatsTests
{
    [Fact]
    public void SkipRate_NoRemovals_ReturnsZero()
    {
        var stats = new RemovalStats();

        Assert.Equal(0.0, stats.SkipRate);
    }

    [Fact]
    public void SkipRate_WithRemovals_CalculatesCorrectly()
    {
        var stats = new RemovalStats
        {
            TotalRemovalsReceived = 10,
            RemovalsSkipped = 4
        };

        Assert.Equal(40.0, stats.SkipRate);
    }
}

public class SkipIntroductionRemovalsConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new SkipIntroductionRemovalsConfiguration();

        Assert.False(config.DefaultSkipRemovals);
        Assert.False(config.GlobalSkipRemovals);
        Assert.Equal(100, config.MaxPendingRemovalsPerDevice);
        Assert.Null(config.AutoApplyAfter);
        Assert.True(config.LogSkippedRemovals);
    }
}
