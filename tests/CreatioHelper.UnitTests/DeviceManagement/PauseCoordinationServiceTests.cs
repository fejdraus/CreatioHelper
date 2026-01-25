using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.DeviceManagement;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests.DeviceManagement;

public class PauseCoordinationServiceTests
{
    private readonly Mock<ILogger<PauseCoordinationService>> _loggerMock;
    private readonly Mock<IConfigurationManager> _configManagerMock;
    private readonly Mock<IEventLogger> _eventLoggerMock;
    private readonly PauseCoordinationService _service;

    public PauseCoordinationServiceTests()
    {
        _loggerMock = new Mock<ILogger<PauseCoordinationService>>();
        _configManagerMock = new Mock<IConfigurationManager>();
        _eventLoggerMock = new Mock<IEventLogger>();

        _service = new PauseCoordinationService(
            _loggerMock.Object,
            _configManagerMock.Object,
            _eventLoggerMock.Object,
            TimeSpan.FromMilliseconds(100)); // Short timeout for tests
    }

    [Fact]
    public async Task PauseDeviceAsync_ReturnsError_WhenDeviceNotFound()
    {
        // Arrange
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync((SyncDevice?)null);

        // Act
        var result = await _service.PauseDeviceAsync("device-1");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task PauseDeviceAsync_ReturnsAlreadyInState_WhenAlreadyPaused()
    {
        // Arrange
        var device = new SyncDevice("device-1", "Test") { Paused = true };
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync(device);

        // Act
        var result = await _service.PauseDeviceAsync("device-1");

        // Assert
        Assert.True(result.Success);
        Assert.True(result.AlreadyInState);
        Assert.True(result.IsPaused);
    }

    [Fact]
    public async Task PauseDeviceAsync_PausesDevice()
    {
        // Arrange
        var device = new SyncDevice("device-1", "Test") { Paused = false };
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync(device);
        _configManagerMock.Setup(r => r.UpsertDeviceAsync(It.IsAny<SyncDevice>())).Returns(Task.CompletedTask);

        // Act
        var result = await _service.PauseDeviceAsync("device-1");

        // Assert
        Assert.True(result.Success);
        Assert.False(result.AlreadyInState);
        Assert.True(result.IsPaused);
        _configManagerMock.Verify(r => r.UpsertDeviceAsync(It.Is<SyncDevice>(d => d.Paused)), Times.Once);
    }

    [Fact]
    public async Task PauseDeviceAsync_RecordsDuration()
    {
        // Arrange
        var device = new SyncDevice("device-1", "Test") { Paused = false };
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync(device);
        _configManagerMock.Setup(r => r.UpsertDeviceAsync(It.IsAny<SyncDevice>())).Returns(Task.CompletedTask);

        // Act
        var result = await _service.PauseDeviceAsync("device-1");

        // Assert
        Assert.True(result.Duration >= TimeSpan.Zero);
    }

    [Fact]
    public async Task ResumeDeviceAsync_ReturnsError_WhenDeviceNotFound()
    {
        // Arrange
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync((SyncDevice?)null);

        // Act
        var result = await _service.ResumeDeviceAsync("device-1");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task ResumeDeviceAsync_ReturnsAlreadyInState_WhenNotPaused()
    {
        // Arrange
        var device = new SyncDevice("device-1", "Test") { Paused = false };
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync(device);

        // Act
        var result = await _service.ResumeDeviceAsync("device-1");

        // Assert
        Assert.True(result.Success);
        Assert.True(result.AlreadyInState);
        Assert.False(result.IsPaused);
    }

    [Fact]
    public async Task ResumeDeviceAsync_ResumesDevice()
    {
        // Arrange
        var device = new SyncDevice("device-1", "Test") { Paused = true };
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync(device);
        _configManagerMock.Setup(r => r.UpsertDeviceAsync(It.IsAny<SyncDevice>())).Returns(Task.CompletedTask);

        // Act
        var result = await _service.ResumeDeviceAsync("device-1");

        // Assert
        Assert.True(result.Success);
        Assert.False(result.IsPaused);
        _configManagerMock.Verify(r => r.UpsertDeviceAsync(It.Is<SyncDevice>(d => !d.Paused)), Times.Once);
    }

    [Fact]
    public async Task PauseFolderAsync_ReturnsError_WhenFolderNotFound()
    {
        // Arrange
        _configManagerMock.Setup(r => r.GetFolderAsync("folder-1")).ReturnsAsync((SyncFolder?)null);

        // Act
        var result = await _service.PauseFolderAsync("folder-1");

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task PauseFolderAsync_PausesFolder()
    {
        // Arrange
        var folder = new SyncFolder("folder-1", "Test", "/path");
        _configManagerMock.Setup(r => r.GetFolderAsync("folder-1")).ReturnsAsync(folder);
        _configManagerMock.Setup(r => r.UpsertFolderAsync(It.IsAny<SyncFolder>())).Returns(Task.CompletedTask);

        // Act
        var result = await _service.PauseFolderAsync("folder-1");

        // Assert
        Assert.True(result.Success);
        Assert.True(result.IsPaused);
    }

    [Fact]
    public async Task ResumeFolderAsync_ResumesFolder()
    {
        // Arrange
        var folder = new SyncFolder("folder-1", "Test", "/path");
        folder.SetPaused(true);
        _configManagerMock.Setup(r => r.GetFolderAsync("folder-1")).ReturnsAsync(folder);
        _configManagerMock.Setup(r => r.UpsertFolderAsync(It.IsAny<SyncFolder>())).Returns(Task.CompletedTask);

        // Act
        var result = await _service.ResumeFolderAsync("folder-1");

        // Assert
        Assert.True(result.Success);
        Assert.False(result.IsPaused);
    }

    [Fact]
    public async Task PauseAllDevicesAsync_PausesAllDevices()
    {
        // Arrange
        var devices = new List<SyncDevice>
        {
            new SyncDevice("device-1", "Device 1"),
            new SyncDevice("device-2", "Device 2")
        };
        _configManagerMock.Setup(r => r.GetAllDevicesAsync()).ReturnsAsync(devices);
        _configManagerMock.Setup(r => r.GetDeviceAsync(It.IsAny<string>()))
            .Returns<string>(id => Task.FromResult(devices.FirstOrDefault(d => d.DeviceId == id)));
        _configManagerMock.Setup(r => r.UpsertDeviceAsync(It.IsAny<SyncDevice>())).Returns(Task.CompletedTask);

        // Act
        var result = await _service.PauseAllDevicesAsync();

        // Assert
        Assert.Equal(2, result.SuccessCount);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public async Task ResumeAllDevicesAsync_ResumesOnlyPausedDevices()
    {
        // Arrange
        var devices = new List<SyncDevice>
        {
            new SyncDevice("device-1", "Device 1") { Paused = true },
            new SyncDevice("device-2", "Device 2") { Paused = false }
        };
        _configManagerMock.Setup(r => r.GetAllDevicesAsync()).ReturnsAsync(devices);
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync(devices[0]);
        _configManagerMock.Setup(r => r.UpsertDeviceAsync(It.IsAny<SyncDevice>())).Returns(Task.CompletedTask);

        // Act
        var result = await _service.ResumeAllDevicesAsync();

        // Assert
        Assert.Equal(1, result.SuccessCount);
    }

    [Fact]
    public async Task PauseAllFoldersAsync_PausesAllFolders()
    {
        // Arrange
        var folders = new List<SyncFolder>
        {
            new SyncFolder("folder-1", "Folder 1", "/path1"),
            new SyncFolder("folder-2", "Folder 2", "/path2")
        };
        _configManagerMock.Setup(r => r.GetAllFoldersAsync()).ReturnsAsync(folders);
        _configManagerMock.Setup(r => r.GetFolderAsync(It.IsAny<string>()))
            .Returns<string>(id => Task.FromResult(folders.FirstOrDefault(f => f.Id == id)));
        _configManagerMock.Setup(r => r.UpsertFolderAsync(It.IsAny<SyncFolder>())).Returns(Task.CompletedTask);

        // Act
        var result = await _service.PauseAllFoldersAsync();

        // Assert
        Assert.Equal(2, result.SuccessCount);
    }

    [Fact]
    public async Task IsDevicePausedAsync_ReturnsTrueWhenPaused()
    {
        // Arrange
        var device = new SyncDevice("device-1", "Test") { Paused = true };
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync(device);

        // Act
        var result = await _service.IsDevicePausedAsync("device-1");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task IsDevicePausedAsync_ReturnsFalseWhenNotPaused()
    {
        // Arrange
        var device = new SyncDevice("device-1", "Test") { Paused = false };
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync(device);

        // Act
        var result = await _service.IsDevicePausedAsync("device-1");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task IsFolderPausedAsync_ReturnsTrueWhenPaused()
    {
        // Arrange
        var folder = new SyncFolder("folder-1", "Test", "/path");
        folder.SetPaused(true);
        _configManagerMock.Setup(r => r.GetFolderAsync("folder-1")).ReturnsAsync(folder);

        // Act
        var result = await _service.IsFolderPausedAsync("folder-1");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task GetPausedDevicesAsync_ReturnsOnlyPausedDevices()
    {
        // Arrange
        var devices = new List<SyncDevice>
        {
            new SyncDevice("device-1", "Device 1") { Paused = true },
            new SyncDevice("device-2", "Device 2") { Paused = false },
            new SyncDevice("device-3", "Device 3") { Paused = true }
        };
        _configManagerMock.Setup(r => r.GetAllDevicesAsync()).ReturnsAsync(devices);

        // Act
        var result = await _service.GetPausedDevicesAsync();

        // Assert
        Assert.Equal(2, result.Count());
        Assert.All(result, d => Assert.True(d.Paused));
    }

    [Fact]
    public async Task GetPausedFoldersAsync_ReturnsOnlyPausedFolders()
    {
        // Arrange
        var folder1 = new SyncFolder("folder-1", "Folder 1", "/path1");
        var folder2 = new SyncFolder("folder-2", "Folder 2", "/path2");
        folder1.SetPaused(true);

        var folders = new List<SyncFolder> { folder1, folder2 };
        _configManagerMock.Setup(r => r.GetAllFoldersAsync()).ReturnsAsync(folders);

        // Act
        var result = await _service.GetPausedFoldersAsync();

        // Assert
        Assert.Single(result);
        Assert.True(result.First().Paused);
    }

    [Fact]
    public void RegisterPauseCallback_AddsCallback()
    {
        // Arrange
        Func<CancellationToken, Task> callback = _ => Task.CompletedTask;

        // Act
        _service.RegisterPauseCallback("test-callback", callback);
        var status = _service.GetStatus();

        // Assert
        Assert.Equal(1, status.RegisteredCallbacks);
    }

    [Fact]
    public void UnregisterPauseCallback_RemovesCallback()
    {
        // Arrange
        _service.RegisterPauseCallback("test-callback", ct => Task.CompletedTask);

        // Act
        _service.UnregisterPauseCallback("test-callback");
        var status = _service.GetStatus();

        // Assert
        Assert.Equal(0, status.RegisteredCallbacks);
    }

    [Fact]
    public async Task PauseDeviceAsync_WaitsForCallbacksWhenGraceful()
    {
        // Arrange
        var callbackExecuted = false;
        _service.RegisterPauseCallback("device-1-transfer", async ct =>
        {
            await Task.Delay(10, ct);
            callbackExecuted = true;
        });

        var device = new SyncDevice("device-1", "Test") { Paused = false };
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync(device);
        _configManagerMock.Setup(r => r.UpsertDeviceAsync(It.IsAny<SyncDevice>())).Returns(Task.CompletedTask);

        // Act
        var result = await _service.PauseDeviceAsync("device-1", graceful: true);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(1, result.WaitedForOperations);
        Assert.True(callbackExecuted);
    }

    [Fact]
    public async Task PauseDeviceAsync_DoesNotWaitForCallbacksWhenNotGraceful()
    {
        // Arrange
        _service.RegisterPauseCallback("device-1-transfer", async ct =>
        {
            await Task.Delay(1000, ct); // Long delay
        });

        var device = new SyncDevice("device-1", "Test") { Paused = false };
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync(device);
        _configManagerMock.Setup(r => r.UpsertDeviceAsync(It.IsAny<SyncDevice>())).Returns(Task.CompletedTask);

        // Act
        var result = await _service.PauseDeviceAsync("device-1", graceful: false);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(0, result.WaitedForOperations);
    }

    [Fact]
    public void GetStatus_ReturnsCorrectStatus()
    {
        // Act
        var status = _service.GetStatus();

        // Assert
        Assert.Equal(0, status.PausedDeviceCount); // Not tracked in status, would need repo call
        Assert.Equal(TimeSpan.FromMilliseconds(100), status.GracefulPauseTimeout);
        Assert.NotNull(status.Statistics);
    }

    [Fact]
    public void PauseResult_HasCorrectDefaults()
    {
        // Arrange
        var result = new PauseResult();

        // Assert
        Assert.False(result.Success);
        Assert.False(result.IsPaused);
        Assert.False(result.AlreadyInState);
        Assert.Equal(0, result.WaitedForOperations);
        Assert.Equal(TimeSpan.Zero, result.Duration);
        Assert.Null(result.Error);
    }

    [Fact]
    public void PauseAllResult_TracksAllMetrics()
    {
        // Arrange
        var result = new PauseAllResult
        {
            SuccessCount = 5,
            FailedCount = 2,
            AlreadyInStateCount = 1,
            Duration = TimeSpan.FromSeconds(3)
        };
        result.Errors["device-1"] = "error";

        // Assert
        Assert.Equal(5, result.SuccessCount);
        Assert.Equal(2, result.FailedCount);
        Assert.Equal(1, result.AlreadyInStateCount);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void PauseServiceStatistics_TracksAllOperations()
    {
        // Arrange
        var stats = new PauseServiceStatistics
        {
            TotalDevicePauses = 10,
            TotalDeviceResumes = 8,
            TotalFolderPauses = 5,
            TotalFolderResumes = 4,
            TotalGracefulWaits = 3
        };

        // Assert
        Assert.Equal(10, stats.TotalDevicePauses);
        Assert.Equal(8, stats.TotalDeviceResumes);
        Assert.Equal(5, stats.TotalFolderPauses);
        Assert.Equal(4, stats.TotalFolderResumes);
        Assert.Equal(3, stats.TotalGracefulWaits);
    }
}
