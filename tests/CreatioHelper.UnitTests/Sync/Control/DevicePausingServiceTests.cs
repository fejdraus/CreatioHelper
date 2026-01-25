using System;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Infrastructure.Services.Sync.Control;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.Control;

public class DevicePausingServiceTests : IDisposable
{
    private readonly Mock<ILogger<DevicePausingService>> _loggerMock;
    private readonly DevicePausingConfiguration _config;
    private readonly DevicePausingService _service;

    public DevicePausingServiceTests()
    {
        _loggerMock = new Mock<ILogger<DevicePausingService>>();
        _config = new DevicePausingConfiguration();
        _service = new DevicePausingService(_loggerMock.Object, _config);
    }

    public void Dispose()
    {
        _service.Dispose();
    }

    #region IsDevicePaused Tests

    [Fact]
    public void IsDevicePaused_NotPaused_ReturnsFalse()
    {
        Assert.False(_service.IsDevicePaused("device1"));
    }

    [Fact]
    public async Task IsDevicePaused_AfterPause_ReturnsTrue()
    {
        await _service.PauseDeviceAsync("device1");

        Assert.True(_service.IsDevicePaused("device1"));
    }

    [Fact]
    public async Task IsDevicePaused_AfterResume_ReturnsFalse()
    {
        await _service.PauseDeviceAsync("device1");
        await _service.ResumeDeviceAsync("device1");

        Assert.False(_service.IsDevicePaused("device1"));
    }

    [Fact]
    public void IsDevicePaused_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.IsDevicePaused(null!));
    }

    #endregion

    #region PauseDeviceAsync Tests

    [Fact]
    public async Task PauseDeviceAsync_NewDevice_Pauses()
    {
        await _service.PauseDeviceAsync("device1");

        Assert.True(_service.IsDevicePaused("device1"));
    }

    [Fact]
    public async Task PauseDeviceAsync_WithReason_StoresReason()
    {
        await _service.PauseDeviceAsync("device1", "Network issues");

        var info = _service.GetPauseInfo("device1");
        Assert.Equal("Network issues", info?.PauseReason);
    }

    [Fact]
    public async Task PauseDeviceAsync_AlreadyPaused_NoOp()
    {
        await _service.PauseDeviceAsync("device1");
        var firstPauseTime = _service.GetPauseInfo("device1")?.PausedAt;

        await Task.Delay(10);
        await _service.PauseDeviceAsync("device1");
        var secondPauseTime = _service.GetPauseInfo("device1")?.PausedAt;

        Assert.Equal(firstPauseTime, secondPauseTime);
    }

    [Fact]
    public async Task PauseDeviceAsync_NullDeviceId_ThrowsArgumentNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.PauseDeviceAsync(null!));
    }

    #endregion

    #region ResumeDeviceAsync Tests

    [Fact]
    public async Task ResumeDeviceAsync_PausedDevice_Resumes()
    {
        await _service.PauseDeviceAsync("device1");
        await _service.ResumeDeviceAsync("device1");

        Assert.False(_service.IsDevicePaused("device1"));
    }

    [Fact]
    public async Task ResumeDeviceAsync_NotPaused_NoOp()
    {
        // Should not throw
        await _service.ResumeDeviceAsync("device1");

        Assert.False(_service.IsDevicePaused("device1"));
    }

    [Fact]
    public async Task ResumeDeviceAsync_ClearsReasonAndTime()
    {
        await _service.PauseDeviceAsync("device1", "Some reason");
        await _service.ResumeDeviceAsync("device1");

        var info = _service.GetPauseInfo("device1");
        Assert.Null(info?.PauseReason);
        Assert.Null(info?.PausedAt);
    }

    #endregion

    #region ToggleDevicePauseAsync Tests

    [Fact]
    public async Task ToggleDevicePauseAsync_NotPaused_Pauses()
    {
        var result = await _service.ToggleDevicePauseAsync("device1");

        Assert.True(result); // Now paused
        Assert.True(_service.IsDevicePaused("device1"));
    }

    [Fact]
    public async Task ToggleDevicePauseAsync_Paused_Resumes()
    {
        await _service.PauseDeviceAsync("device1");

        var result = await _service.ToggleDevicePauseAsync("device1");

        Assert.False(result); // Now not paused
        Assert.False(_service.IsDevicePaused("device1"));
    }

    #endregion

    #region GetPausedDevices Tests

    [Fact]
    public void GetPausedDevices_NoPaused_ReturnsEmpty()
    {
        var paused = _service.GetPausedDevices();

        Assert.Empty(paused);
    }

    [Fact]
    public async Task GetPausedDevices_SomePaused_ReturnsOnlyPaused()
    {
        await _service.PauseDeviceAsync("device1");
        await _service.PauseDeviceAsync("device2");
        await _service.PauseDeviceAsync("device3");
        await _service.ResumeDeviceAsync("device2");

        var paused = _service.GetPausedDevices();

        Assert.Equal(2, paused.Count);
        Assert.Contains("device1", paused);
        Assert.Contains("device3", paused);
        Assert.DoesNotContain("device2", paused);
    }

    #endregion

    #region GetPauseInfo Tests

    [Fact]
    public void GetPauseInfo_NotPaused_ReturnsNotPausedInfo()
    {
        var info = _service.GetPauseInfo("device1");

        Assert.NotNull(info);
        Assert.Equal("device1", info.DeviceId);
        Assert.False(info.IsPaused);
        Assert.Null(info.PausedAt);
    }

    [Fact]
    public async Task GetPauseInfo_Paused_ReturnsFullInfo()
    {
        await _service.PauseDeviceAsync("device1", "Test reason");

        var info = _service.GetPauseInfo("device1");

        Assert.NotNull(info);
        Assert.True(info.IsPaused);
        Assert.Equal("Test reason", info.PauseReason);
        Assert.NotNull(info.PausedAt);
        Assert.NotNull(info.PauseDuration);
        Assert.True(info.PauseDuration.Value >= TimeSpan.Zero);
    }

    [Fact]
    public void GetPauseInfo_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetPauseInfo(null!));
    }

    #endregion

    #region WaitUntilResumedAsync Tests

    [Fact]
    public async Task WaitUntilResumedAsync_NotPaused_ReturnsImmediately()
    {
        var task = _service.WaitUntilResumedAsync("device1");

        await task;
        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitUntilResumedAsync_Paused_WaitsForResume()
    {
        await _service.PauseDeviceAsync("device1");
        var waitTask = _service.WaitUntilResumedAsync("device1");

        await Task.Delay(50);
        Assert.False(waitTask.IsCompleted);

        await _service.ResumeDeviceAsync("device1");

        await waitTask;
        Assert.True(waitTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WaitUntilResumedAsync_Cancellation_ThrowsOperationCanceled()
    {
        await _service.PauseDeviceAsync("device1");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // TaskCanceledException inherits from OperationCanceledException
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.WaitUntilResumedAsync("device1", cts.Token));
    }

    #endregion

    #region ShouldSync Tests

    [Fact]
    public void ShouldSync_DeviceNotPaused_ReturnsTrue()
    {
        Assert.True(_service.ShouldSync("device1", "folder1"));
    }

    [Fact]
    public async Task ShouldSync_DevicePaused_ReturnsFalse()
    {
        await _service.PauseDeviceAsync("device1");

        Assert.False(_service.ShouldSync("device1", "folder1"));
    }

    [Fact]
    public async Task ShouldSync_FolderPaused_ReturnsFalse()
    {
        var folderServiceMock = new Mock<IFolderPausingService>();
        folderServiceMock.Setup(f => f.IsFolderPaused("folder1")).Returns(true);

        Assert.False(_service.ShouldSync("device1", "folder1", folderServiceMock.Object));
    }

    [Fact]
    public async Task ShouldSync_BothActive_ReturnsTrue()
    {
        var folderServiceMock = new Mock<IFolderPausingService>();
        folderServiceMock.Setup(f => f.IsFolderPaused("folder1")).Returns(false);

        Assert.True(_service.ShouldSync("device1", "folder1", folderServiceMock.Object));
    }

    [Fact]
    public void ShouldSync_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ShouldSync(null!, "folder1"));
    }

    [Fact]
    public void ShouldSync_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ShouldSync("device1", null!));
    }

    #endregion

    #region UpdatePendingStats Tests

    [Fact]
    public async Task UpdatePendingStats_Updates()
    {
        await _service.PauseDeviceAsync("device1");
        _service.UpdatePendingStats("device1", 10, 1024);

        var info = _service.GetPauseInfo("device1");

        Assert.Equal(10, info?.PendingItems);
        Assert.Equal(1024, info?.PendingBytes);
    }

    [Fact]
    public void UpdatePendingStats_NoState_NoOp()
    {
        // Should not throw
        _service.UpdatePendingStats("device1", 10, 1024);
    }

    #endregion

    #region Events Tests

    [Fact]
    public async Task DevicePaused_RaisedOnPause()
    {
        var raised = false;
        string? raisedDeviceId = null;

        _service.DevicePaused += (s, e) =>
        {
            raised = true;
            raisedDeviceId = e.DeviceId;
        };

        await _service.PauseDeviceAsync("device1");

        Assert.True(raised);
        Assert.Equal("device1", raisedDeviceId);
    }

    [Fact]
    public async Task DeviceResumed_RaisedOnResume()
    {
        var raised = false;
        string? raisedDeviceId = null;

        _service.DeviceResumed += (s, e) =>
        {
            raised = true;
            raisedDeviceId = e.DeviceId;
        };

        await _service.PauseDeviceAsync("device1");
        await _service.ResumeDeviceAsync("device1");

        Assert.True(raised);
        Assert.Equal("device1", raisedDeviceId);
    }

    [Fact]
    public async Task DevicePaused_EventArgsCorrect()
    {
        DevicePauseEventArgs? capturedArgs = null;

        _service.DevicePaused += (s, e) => capturedArgs = e;

        await _service.PauseDeviceAsync("device1", "Test reason");

        Assert.NotNull(capturedArgs);
        Assert.Equal("device1", capturedArgs.DeviceId);
        Assert.True(capturedArgs.IsPaused);
        Assert.Equal("Test reason", capturedArgs.Reason);
    }

    #endregion

    #region InitiallyPausedDevices Tests

    [Fact]
    public void InitiallyPausedDevices_ArePausedOnStart()
    {
        var config = new DevicePausingConfiguration();
        config.InitiallyPausedDevices.Add("device1");
        config.InitiallyPausedDevices.Add("device2");

        using var service = new DevicePausingService(_loggerMock.Object, config);

        Assert.True(service.IsDevicePaused("device1"));
        Assert.True(service.IsDevicePaused("device2"));
        Assert.False(service.IsDevicePaused("device3"));
    }

    #endregion

    #region AutoResume Tests

    [Fact]
    public async Task AutoResumeAfter_ResumesAfterDelay()
    {
        var config = new DevicePausingConfiguration
        {
            AutoResumeAfter = TimeSpan.FromMilliseconds(100)
        };
        using var service = new DevicePausingService(_loggerMock.Object, config);

        await service.PauseDeviceAsync("device1");
        Assert.True(service.IsDevicePaused("device1"));

        await Task.Delay(200);

        Assert.False(service.IsDevicePaused("device1"));
    }

    #endregion
}

public class DevicePausingConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new DevicePausingConfiguration();

        Assert.Empty(config.InitiallyPausedDevices);
        Assert.True(config.PersistPauseState);
        Assert.False(config.DisconnectOnPause);
        Assert.Null(config.AutoResumeAfter);
    }
}

public class DevicePauseInfoTests
{
    [Fact]
    public void PauseDuration_NotPaused_ReturnsNull()
    {
        var info = new DevicePauseInfo
        {
            DeviceId = "device1",
            IsPaused = false,
            PausedAt = null
        };

        Assert.Null(info.PauseDuration);
    }

    [Fact]
    public void PauseDuration_Paused_ReturnsPositive()
    {
        var info = new DevicePauseInfo
        {
            DeviceId = "device1",
            IsPaused = true,
            PausedAt = DateTime.UtcNow.AddSeconds(-10)
        };

        Assert.NotNull(info.PauseDuration);
        Assert.True(info.PauseDuration.Value >= TimeSpan.FromSeconds(10));
    }
}
