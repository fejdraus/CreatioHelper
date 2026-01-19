using CreatioHelper.Infrastructure.Services.Sync.Transfer;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.Transfer;

public class PullerPauseServiceTests : IDisposable
{
    private readonly Mock<ILogger<PullerPauseService>> _loggerMock;
    private readonly PullerPauseService _service;

    public PullerPauseServiceTests()
    {
        _loggerMock = new Mock<ILogger<PullerPauseService>>();
        _service = new PullerPauseService(_loggerMock.Object);
    }

    public void Dispose()
    {
        _service.Dispose();
    }

    #region PauseFolder Tests

    [Fact]
    public async Task PauseFolderAsync_PausesFolder()
    {
        await _service.PauseFolderAsync("folder1");

        Assert.True(_service.IsFolderPaused("folder1"));
    }

    [Fact]
    public async Task PauseFolderAsync_MultipleFolders_PausesIndependently()
    {
        await _service.PauseFolderAsync("folder1");
        await _service.PauseFolderAsync("folder2");

        Assert.True(_service.IsFolderPaused("folder1"));
        Assert.True(_service.IsFolderPaused("folder2"));
        Assert.False(_service.IsFolderPaused("folder3"));
    }

    [Fact]
    public async Task PauseFolderAsync_NullFolderId_ThrowsArgumentNullException()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.PauseFolderAsync(null!));
    }

    #endregion

    #region ResumeFolder Tests

    [Fact]
    public async Task ResumeFolderAsync_ResumesFolder()
    {
        await _service.PauseFolderAsync("folder1");
        await _service.ResumeFolderAsync("folder1");

        Assert.False(_service.IsFolderPaused("folder1"));
    }

    [Fact]
    public async Task ResumeFolderAsync_NotPaused_NoError()
    {
        // Should not throw
        await _service.ResumeFolderAsync("folder1");

        Assert.False(_service.IsFolderPaused("folder1"));
    }

    #endregion

    #region PauseDevice Tests

    [Fact]
    public async Task PauseDeviceAsync_PausesDevice()
    {
        await _service.PauseDeviceAsync("device1");

        Assert.True(_service.IsDevicePaused("device1"));
    }

    [Fact]
    public async Task ResumeDeviceAsync_ResumesDevice()
    {
        await _service.PauseDeviceAsync("device1");
        await _service.ResumeDeviceAsync("device1");

        Assert.False(_service.IsDevicePaused("device1"));
    }

    #endregion

    #region PauseAll Tests

    [Fact]
    public async Task PauseAllAsync_PausesGlobally()
    {
        await _service.PauseAllAsync();

        Assert.True(_service.IsGloballyPaused);
    }

    [Fact]
    public async Task ResumeAllAsync_ResumesGlobally()
    {
        await _service.PauseAllAsync();
        await _service.ResumeAllAsync();

        Assert.False(_service.IsGloballyPaused);
    }

    #endregion

    #region ShouldPull Tests

    [Fact]
    public void ShouldPull_NothingPaused_ReturnsTrue()
    {
        Assert.True(_service.ShouldPull("folder1", "device1"));
    }

    [Fact]
    public async Task ShouldPull_GloballyPaused_ReturnsFalse()
    {
        await _service.PauseAllAsync();

        Assert.False(_service.ShouldPull("folder1", "device1"));
    }

    [Fact]
    public async Task ShouldPull_FolderPaused_ReturnsFalse()
    {
        await _service.PauseFolderAsync("folder1");

        Assert.False(_service.ShouldPull("folder1", "device1"));
        Assert.True(_service.ShouldPull("folder2", "device1"));
    }

    [Fact]
    public async Task ShouldPull_DevicePaused_ReturnsFalse()
    {
        await _service.PauseDeviceAsync("device1");

        Assert.False(_service.ShouldPull("folder1", "device1"));
        Assert.True(_service.ShouldPull("folder1", "device2"));
    }

    #endregion

    #region WaitForResume Tests

    [Fact]
    public async Task WaitForResumeAsync_NotPaused_ReturnsImmediately()
    {
        var result = await _service.WaitForResumeAsync("folder1", "device1");

        Assert.True(result);
    }

    [Fact]
    public async Task WaitForResumeAsync_Cancelled_ReturnsFalse()
    {
        await _service.PauseAllAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await _service.WaitForResumeAsync("folder1", "device1", cts.Token);

        Assert.False(result);
    }

    [Fact]
    public async Task WaitForResumeAsync_ResumedDuringWait_ReturnsTrue()
    {
        await _service.PauseFolderAsync("folder1");

        var waitTask = _service.WaitForResumeAsync("folder1", "device1");

        // Resume after a short delay
        await Task.Delay(50);
        await _service.ResumeFolderAsync("folder1");

        var result = await waitTask;
        Assert.True(result);
    }

    #endregion

    #region GetState Tests

    [Fact]
    public async Task GetState_ReturnsCorrectState()
    {
        await _service.PauseFolderAsync("folder1");
        await _service.PauseDeviceAsync("device1");

        var state = _service.GetState();

        Assert.False(state.GloballyPaused);
        Assert.Contains("folder1", state.PausedFolders);
        Assert.Contains("device1", state.PausedDevices);
    }

    [Fact]
    public async Task GetState_GloballyPaused_IncludesTimestamp()
    {
        var before = DateTime.UtcNow;
        await _service.PauseAllAsync();
        var after = DateTime.UtcNow;

        var state = _service.GetState();

        Assert.True(state.GloballyPaused);
        Assert.NotNull(state.GlobalPausedAt);
        Assert.True(state.GlobalPausedAt >= before);
        Assert.True(state.GlobalPausedAt <= after);
    }

    #endregion

    #region OnStateChanged Tests

    [Fact]
    public async Task OnStateChanged_NotifiesOnPause()
    {
        var callCount = 0;
        PullerPauseState? lastState = null;

        using var subscription = _service.OnStateChanged(state =>
        {
            callCount++;
            lastState = state;
        });

        await _service.PauseFolderAsync("folder1");

        Assert.True(callCount > 0);
        Assert.NotNull(lastState);
        Assert.Contains("folder1", lastState.PausedFolders);
    }

    #endregion
}

public class ScheduledPauseConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new ScheduledPauseConfiguration();

        Assert.False(config.Enabled);
        Assert.Empty(config.DaysOfWeek);
        Assert.Empty(config.FolderIds);
    }
}
