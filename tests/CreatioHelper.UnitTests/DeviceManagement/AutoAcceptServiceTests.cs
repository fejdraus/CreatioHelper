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

public class AutoAcceptServiceTests
{
    private readonly Mock<ILogger<AutoAcceptService>> _loggerMock;
    private readonly Mock<IConfigurationManager> _configManagerMock;
    private readonly Mock<IPendingManager> _pendingManagerMock;
    private readonly Mock<IEventLogger> _eventLoggerMock;
    private readonly AutoAcceptService _service;

    public AutoAcceptServiceTests()
    {
        _loggerMock = new Mock<ILogger<AutoAcceptService>>();
        _configManagerMock = new Mock<IConfigurationManager>();
        _pendingManagerMock = new Mock<IPendingManager>();
        _eventLoggerMock = new Mock<IEventLogger>();

        _service = new AutoAcceptService(
            _loggerMock.Object,
            _configManagerMock.Object,
            _pendingManagerMock.Object,
            _eventLoggerMock.Object,
            "/test/default/path");
    }

    [Fact]
    public async Task ProcessFolderOfferAsync_ReturnsFolderAlreadyExists_WhenFolderExists()
    {
        // Arrange
        var existingFolder = new SyncFolder("folder-1", "Existing", "/path");
        _configManagerMock.Setup(r => r.GetFolderAsync("folder-1")).ReturnsAsync(existingFolder);
        _configManagerMock.Setup(r => r.UpsertFolderAsync(It.IsAny<SyncFolder>())).Returns(Task.CompletedTask);

        // Act
        var result = await _service.ProcessFolderOfferAsync("device-1", "folder-1", "Test");

        // Assert
        Assert.True(result.FolderAlreadyExists);
        Assert.False(result.WasAutoAccepted);
    }

    [Fact]
    public async Task ProcessFolderOfferAsync_AddsDeviceToExistingFolder()
    {
        // Arrange
        var existingFolder = new SyncFolder("folder-1", "Existing", "/path");
        _configManagerMock.Setup(r => r.GetFolderAsync("folder-1")).ReturnsAsync(existingFolder);
        _configManagerMock.Setup(r => r.UpsertFolderAsync(It.IsAny<SyncFolder>())).Returns(Task.CompletedTask);

        // Act
        await _service.ProcessFolderOfferAsync("device-1", "folder-1", "Test");

        // Assert
        _configManagerMock.Verify(r => r.UpsertFolderAsync(It.Is<SyncFolder>(f => f.Devices.Contains("device-1"))), Times.Once);
    }

    [Fact]
    public async Task ProcessFolderOfferAsync_ReturnsIsIgnored_WhenFolderIgnored()
    {
        // Arrange
        var device = new SyncDevice("device-1", "Test");
        device.IgnoredFolders.Add("folder-1");

        _configManagerMock.Setup(r => r.GetFolderAsync("folder-1")).ReturnsAsync((SyncFolder?)null);
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync(device);

        // Act
        var result = await _service.ProcessFolderOfferAsync("device-1", "folder-1", "Test");

        // Assert
        Assert.True(result.IsIgnored);
        Assert.False(result.WasAutoAccepted);
    }

    [Fact]
    public async Task ProcessFolderOfferAsync_AddsToPending_WhenAutoAcceptDisabled()
    {
        // Arrange
        var device = new SyncDevice("device-1", "Test") { AutoAcceptFolders = false };
        _configManagerMock.Setup(r => r.GetFolderAsync("folder-1")).ReturnsAsync((SyncFolder?)null);
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync(device);
        _pendingManagerMock.Setup(m => m.AddPendingFolderAsync(It.IsAny<PendingFolder>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _service.ProcessFolderOfferAsync("device-1", "folder-1", "Test Folder");

        // Assert
        Assert.True(result.AddedToPending);
        Assert.False(result.WasAutoAccepted);
        _pendingManagerMock.Verify(m => m.AddPendingFolderAsync(
            It.Is<PendingFolder>(f => f.FolderId == "folder-1"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProcessFolderOfferAsync_AutoAccepts_WhenEnabled()
    {
        // Arrange
        var device = new SyncDevice("device-1", "Test") { AutoAcceptFolders = true };
        _configManagerMock.Setup(r => r.GetFolderAsync("folder-1")).ReturnsAsync((SyncFolder?)null);
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync(device);
        _configManagerMock.Setup(r => r.UpsertFolderAsync(It.IsAny<SyncFolder>())).Returns(Task.CompletedTask);

        // Act
        var result = await _service.ProcessFolderOfferAsync("device-1", "folder-1", "Test Folder");

        // Assert
        Assert.True(result.WasAutoAccepted);
        Assert.NotNull(result.AcceptedFolder);
        Assert.Equal("folder-1", result.AcceptedFolder.Id);
        Assert.Contains("/test/default/path", result.FolderPath);
    }

    [Fact]
    public async Task ProcessFolderOfferAsync_CreatesReceiveEncryptedFolder()
    {
        // Arrange
        var device = new SyncDevice("device-1", "Test") { AutoAcceptFolders = true };
        _configManagerMock.Setup(r => r.GetFolderAsync("folder-1")).ReturnsAsync((SyncFolder?)null);
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync(device);
        _configManagerMock.Setup(r => r.UpsertFolderAsync(It.IsAny<SyncFolder>())).Returns(Task.CompletedTask);

        // Act
        var result = await _service.ProcessFolderOfferAsync("device-1", "folder-1", "Test", receiveEncrypted: true);

        // Assert
        Assert.True(result.WasAutoAccepted);
        Assert.Equal("receiveencrypted", result.AcceptedFolder?.Type);
    }

    [Fact]
    public async Task ProcessFolderOfferAsync_SanitizesFolderLabel()
    {
        // Arrange
        var device = new SyncDevice("device-1", "Test") { AutoAcceptFolders = true };
        _configManagerMock.Setup(r => r.GetFolderAsync("folder-1")).ReturnsAsync((SyncFolder?)null);
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync(device);
        _configManagerMock.Setup(r => r.UpsertFolderAsync(It.IsAny<SyncFolder>())).Returns(Task.CompletedTask);

        // Act
        var result = await _service.ProcessFolderOfferAsync("device-1", "folder-1", "Test/Folder:Name");

        // Assert
        Assert.True(result.WasAutoAccepted);
        Assert.DoesNotContain(":", result.FolderPath);
    }

    [Fact]
    public async Task SetAutoAcceptAsync_UpdatesDevice()
    {
        // Arrange
        var device = new SyncDevice("device-1", "Test") { AutoAcceptFolders = false };
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync(device);
        _configManagerMock.Setup(r => r.UpsertDeviceAsync(It.IsAny<SyncDevice>())).Returns(Task.CompletedTask);

        // Act
        await _service.SetAutoAcceptAsync("device-1", true);

        // Assert
        _configManagerMock.Verify(r => r.UpsertDeviceAsync(It.Is<SyncDevice>(d => d.AutoAcceptFolders)), Times.Once);
    }

    [Fact]
    public async Task SetAutoAcceptAsync_ThrowsWhenDeviceNotFound()
    {
        // Arrange
        _configManagerMock.Setup(r => r.GetDeviceAsync("nonexistent")).ReturnsAsync((SyncDevice?)null);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SetAutoAcceptAsync("nonexistent", true));
    }

    [Fact]
    public async Task IsAutoAcceptEnabledAsync_ReturnsTrueWhenEnabled()
    {
        // Arrange
        var device = new SyncDevice("device-1", "Test") { AutoAcceptFolders = true };
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync(device);

        // Act
        var result = await _service.IsAutoAcceptEnabledAsync("device-1");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task IsAutoAcceptEnabledAsync_ReturnsFalseWhenDisabled()
    {
        // Arrange
        var device = new SyncDevice("device-1", "Test") { AutoAcceptFolders = false };
        _configManagerMock.Setup(r => r.GetDeviceAsync("device-1")).ReturnsAsync(device);

        // Act
        var result = await _service.IsAutoAcceptEnabledAsync("device-1");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task IsAutoAcceptEnabledAsync_ReturnsFalseWhenDeviceNotFound()
    {
        // Arrange
        _configManagerMock.Setup(r => r.GetDeviceAsync("nonexistent")).ReturnsAsync((SyncDevice?)null);

        // Act
        var result = await _service.IsAutoAcceptEnabledAsync("nonexistent");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task GetAutoAcceptDevicesAsync_ReturnsOnlyAutoAcceptDevices()
    {
        // Arrange
        var devices = new List<SyncDevice>
        {
            new SyncDevice("device-1", "Device 1") { AutoAcceptFolders = true },
            new SyncDevice("device-2", "Device 2") { AutoAcceptFolders = false },
            new SyncDevice("device-3", "Device 3") { AutoAcceptFolders = true }
        };
        _configManagerMock.Setup(r => r.GetAllDevicesAsync()).ReturnsAsync(devices);

        // Act
        var result = await _service.GetAutoAcceptDevicesAsync();

        // Assert
        Assert.Equal(2, result.Count());
        Assert.All(result, d => Assert.True(d.AutoAcceptFolders));
    }

    [Fact]
    public void SetDefaultFolderPath_UpdatesPath()
    {
        // Act
        _service.SetDefaultFolderPath("/new/path");
        var status = _service.GetStatus();

        // Assert
        Assert.Equal("/new/path", status.DefaultFolderPath);
    }

    [Fact]
    public void GetStatus_ReturnsCorrectStatus()
    {
        // Act
        var status = _service.GetStatus();

        // Assert
        Assert.True(status.IsActive);
        Assert.Equal("/test/default/path", status.DefaultFolderPath);
        Assert.NotNull(status.Statistics);
    }

    [Fact]
    public void AutoAcceptResult_HasCorrectDefaults()
    {
        // Arrange
        var result = new AutoAcceptResult();

        // Assert
        Assert.False(result.WasAutoAccepted);
        Assert.False(result.AddedToPending);
        Assert.False(result.FolderAlreadyExists);
        Assert.False(result.IsIgnored);
        Assert.Null(result.AcceptedFolder);
        Assert.Null(result.FolderPath);
        Assert.Null(result.Reason);
    }

    [Fact]
    public void AutoAcceptStatistics_TracksMetrics()
    {
        // Arrange
        var stats = new AutoAcceptStatistics
        {
            TotalFoldersAutoAccepted = 10,
            TotalOffersProcessed = 50,
            OffersIgnored = 5,
            OffersAddedToPending = 35
        };

        // Assert
        Assert.Equal(10, stats.TotalFoldersAutoAccepted);
        Assert.Equal(50, stats.TotalOffersProcessed);
        Assert.Equal(5, stats.OffersIgnored);
        Assert.Equal(35, stats.OffersAddedToPending);
    }
}
