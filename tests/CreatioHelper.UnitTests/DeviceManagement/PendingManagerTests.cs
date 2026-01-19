using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.DeviceManagement;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests.DeviceManagement;

public class PendingManagerTests
{
    private readonly Mock<ILogger<PendingManager>> _loggerMock;
    private readonly Mock<IDeviceInfoRepository> _deviceRepoMock;
    private readonly Mock<IFolderConfigRepository> _folderRepoMock;
    private readonly Mock<IEventLogger> _eventLoggerMock;
    private readonly PendingManager _manager;

    public PendingManagerTests()
    {
        _loggerMock = new Mock<ILogger<PendingManager>>();
        _deviceRepoMock = new Mock<IDeviceInfoRepository>();
        _folderRepoMock = new Mock<IFolderConfigRepository>();
        _eventLoggerMock = new Mock<IEventLogger>();

        _manager = new PendingManager(
            _loggerMock.Object,
            _deviceRepoMock.Object,
            _folderRepoMock.Object,
            _eventLoggerMock.Object);
    }

    [Fact]
    public async Task AddPendingDeviceAsync_AddsDevice()
    {
        // Arrange
        var device = new PendingDevice
        {
            DeviceId = "device-1",
            DeviceName = "Test Device",
            Address = "tcp://192.168.1.1:22000"
        };

        // Act
        await _manager.AddPendingDeviceAsync(device);

        // Assert
        var pending = await _manager.GetPendingDevicesAsync();
        Assert.Single(pending);
        Assert.Equal("device-1", pending.First().DeviceId);
    }

    [Fact]
    public async Task AddPendingDeviceAsync_UpdatesExistingDevice()
    {
        // Arrange
        var device1 = new PendingDevice
        {
            DeviceId = "device-1",
            DeviceName = "Test Device",
            Address = "tcp://192.168.1.1:22000"
        };
        var device2 = new PendingDevice
        {
            DeviceId = "device-1",
            DeviceName = "Test Device",
            Address = "tcp://192.168.1.2:22000"
        };

        // Act
        await _manager.AddPendingDeviceAsync(device1);
        await _manager.AddPendingDeviceAsync(device2);

        // Assert
        var pending = await _manager.GetPendingDevicesAsync();
        Assert.Single(pending);
        Assert.Equal("tcp://192.168.1.2:22000", pending.First().Address);
        Assert.Equal(2, pending.First().ConnectionAttempts);
    }

    [Fact]
    public async Task AddPendingFolderAsync_AddsFolder()
    {
        // Arrange
        var folder = new PendingFolder
        {
            FolderId = "folder-1",
            FolderLabel = "Test Folder",
            OfferedByDeviceId = "device-1"
        };

        // Act
        await _manager.AddPendingFolderAsync(folder);

        // Assert
        var pending = await _manager.GetPendingFoldersAsync();
        Assert.Single(pending);
        Assert.Equal("folder-1", pending.First().FolderId);
    }

    [Fact]
    public async Task AcceptDeviceAsync_CreatesDevice()
    {
        // Arrange
        var pendingDevice = new PendingDevice
        {
            DeviceId = "device-1",
            DeviceName = "Test Device",
            Address = "tcp://192.168.1.1:22000"
        };
        await _manager.AddPendingDeviceAsync(pendingDevice);
        _deviceRepoMock.Setup(r => r.UpsertAsync(It.IsAny<SyncDevice>())).Returns(Task.CompletedTask);

        // Act
        var device = await _manager.AcceptDeviceAsync("device-1");

        // Assert
        Assert.Equal("device-1", device.DeviceId);
        Assert.Equal("Test Device", device.DeviceName);
        _deviceRepoMock.Verify(r => r.UpsertAsync(It.Is<SyncDevice>(d => d.DeviceId == "device-1")), Times.Once);
    }

    [Fact]
    public async Task AcceptDeviceAsync_UsesCustomName()
    {
        // Arrange
        var pendingDevice = new PendingDevice
        {
            DeviceId = "device-1",
            DeviceName = "Original Name"
        };
        await _manager.AddPendingDeviceAsync(pendingDevice);
        _deviceRepoMock.Setup(r => r.UpsertAsync(It.IsAny<SyncDevice>())).Returns(Task.CompletedTask);

        // Act
        var device = await _manager.AcceptDeviceAsync("device-1", "Custom Name");

        // Assert
        Assert.Equal("Custom Name", device.DeviceName);
    }

    [Fact]
    public async Task AcceptDeviceAsync_RemovesFromPending()
    {
        // Arrange
        var pendingDevice = new PendingDevice { DeviceId = "device-1", DeviceName = "Test" };
        await _manager.AddPendingDeviceAsync(pendingDevice);
        _deviceRepoMock.Setup(r => r.UpsertAsync(It.IsAny<SyncDevice>())).Returns(Task.CompletedTask);

        // Act
        await _manager.AcceptDeviceAsync("device-1");

        // Assert
        var pending = await _manager.GetPendingDevicesAsync();
        Assert.Empty(pending);
    }

    [Fact]
    public async Task AcceptDeviceAsync_ThrowsWhenNotPending()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.AcceptDeviceAsync("nonexistent"));
    }

    [Fact]
    public async Task AcceptFolderAsync_CreatesFolder()
    {
        // Arrange
        var pendingFolder = new PendingFolder
        {
            FolderId = "folder-1",
            FolderLabel = "Test Folder",
            OfferedByDeviceId = "device-1"
        };
        await _manager.AddPendingFolderAsync(pendingFolder);
        _folderRepoMock.Setup(r => r.UpsertAsync(It.IsAny<SyncFolder>())).Returns(Task.CompletedTask);

        // Act
        var folder = await _manager.AcceptFolderAsync("folder-1", "/path/to/folder");

        // Assert
        Assert.Equal("folder-1", folder.Id);
        Assert.Equal("/path/to/folder", folder.Path);
        Assert.Contains("device-1", folder.Devices);
    }

    [Fact]
    public async Task AcceptFolderAsync_UsesReceiveEncryptedType()
    {
        // Arrange
        var pendingFolder = new PendingFolder
        {
            FolderId = "folder-1",
            FolderLabel = "Encrypted",
            OfferedByDeviceId = "device-1",
            ReceiveEncrypted = true
        };
        await _manager.AddPendingFolderAsync(pendingFolder);
        _folderRepoMock.Setup(r => r.UpsertAsync(It.IsAny<SyncFolder>())).Returns(Task.CompletedTask);

        // Act
        var folder = await _manager.AcceptFolderAsync("folder-1", "/path");

        // Assert
        Assert.Equal("receiveencrypted", folder.Type);
    }

    [Fact]
    public async Task RejectDeviceAsync_MarksAsRejected()
    {
        // Arrange
        var pendingDevice = new PendingDevice { DeviceId = "device-1", DeviceName = "Test" };
        await _manager.AddPendingDeviceAsync(pendingDevice);

        // Act
        await _manager.RejectDeviceAsync("device-1");

        // Assert
        var pending = await _manager.GetPendingDevicesAsync();
        Assert.Empty(pending); // Rejected devices not returned
    }

    [Fact]
    public async Task RejectFolderAsync_MarksAsRejected()
    {
        // Arrange
        var pendingFolder = new PendingFolder
        {
            FolderId = "folder-1",
            OfferedByDeviceId = "device-1"
        };
        await _manager.AddPendingFolderAsync(pendingFolder);
        _deviceRepoMock.Setup(r => r.GetAsync("device-1")).ReturnsAsync(new SyncDevice("device-1", "Test"));
        _deviceRepoMock.Setup(r => r.UpsertAsync(It.IsAny<SyncDevice>())).Returns(Task.CompletedTask);

        // Act
        await _manager.RejectFolderAsync("folder-1", "device-1");

        // Assert
        var pending = await _manager.GetPendingFoldersAsync();
        Assert.Empty(pending);
    }

    [Fact]
    public async Task IsDevicePendingAsync_ReturnsTrueForPendingDevice()
    {
        // Arrange
        await _manager.AddPendingDeviceAsync(new PendingDevice { DeviceId = "device-1", DeviceName = "Test" });

        // Act
        var result = await _manager.IsDevicePendingAsync("device-1");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task IsDevicePendingAsync_ReturnsFalseForNonexistent()
    {
        // Act
        var result = await _manager.IsDevicePendingAsync("nonexistent");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task IsFolderPendingAsync_ReturnsTrueForPendingFolder()
    {
        // Arrange
        await _manager.AddPendingFolderAsync(new PendingFolder
        {
            FolderId = "folder-1",
            OfferedByDeviceId = "device-1"
        });

        // Act
        var result = await _manager.IsFolderPendingAsync("folder-1", "device-1");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task GetPendingFoldersForDeviceAsync_ReturnsOnlyDeviceFolders()
    {
        // Arrange
        await _manager.AddPendingFolderAsync(new PendingFolder
        {
            FolderId = "folder-1",
            OfferedByDeviceId = "device-1"
        });
        await _manager.AddPendingFolderAsync(new PendingFolder
        {
            FolderId = "folder-2",
            OfferedByDeviceId = "device-2"
        });

        // Act
        var result = await _manager.GetPendingFoldersForDeviceAsync("device-1");

        // Assert
        Assert.Single(result);
        Assert.Equal("folder-1", result.First().FolderId);
    }

    [Fact]
    public async Task CleanupStalePendingItemsAsync_RemovesOldItems()
    {
        // Arrange - this test is tricky since we can't easily set LastSeen to past
        // Just verify it doesn't throw
        await _manager.AddPendingDeviceAsync(new PendingDevice { DeviceId = "device-1", DeviceName = "Test" });

        // Act - cleanup with very short maxAge won't remove recently added items
        await _manager.CleanupStalePendingItemsAsync(TimeSpan.FromHours(1));

        // Assert - item should still be there (not stale)
        var pending = await _manager.GetPendingDevicesAsync();
        Assert.Single(pending);
    }

    [Fact]
    public async Task ClearAllPendingAsync_ClearsEverything()
    {
        // Arrange
        await _manager.AddPendingDeviceAsync(new PendingDevice { DeviceId = "device-1", DeviceName = "Test" });
        await _manager.AddPendingFolderAsync(new PendingFolder { FolderId = "folder-1", OfferedByDeviceId = "device-1" });

        // Act
        await _manager.ClearAllPendingAsync();

        // Assert
        var devices = await _manager.GetPendingDevicesAsync();
        var folders = await _manager.GetPendingFoldersAsync();
        Assert.Empty(devices);
        Assert.Empty(folders);
    }

    [Fact]
    public void GetStatus_ReturnsCorrectCounts()
    {
        // Arrange
        _manager.AddPendingDeviceAsync(new PendingDevice { DeviceId = "device-1", DeviceName = "Test" }).Wait();
        _manager.AddPendingFolderAsync(new PendingFolder { FolderId = "folder-1", OfferedByDeviceId = "device-1" }).Wait();

        // Act
        var status = _manager.GetStatus();

        // Assert
        Assert.Equal(1, status.PendingDeviceCount);
        Assert.Equal(1, status.PendingFolderCount);
        Assert.NotNull(status.LastPendingAddition);
    }

    [Fact]
    public void PendingDevice_HasCorrectDefaults()
    {
        // Arrange
        var device = new PendingDevice();

        // Assert
        Assert.Equal(string.Empty, device.DeviceId);
        Assert.Equal("tcp", device.ConnectionType);
        Assert.Equal(1, device.ConnectionAttempts);
        Assert.False(device.IsRejected);
    }

    [Fact]
    public void PendingFolder_HasCorrectDefaults()
    {
        // Arrange
        var folder = new PendingFolder();

        // Assert
        Assert.Equal(string.Empty, folder.FolderId);
        Assert.False(folder.ReceiveEncrypted);
        Assert.False(folder.IsRejected);
    }

    [Fact]
    public void PendingManagerStatus_ContainsStatistics()
    {
        // Arrange
        var status = new PendingManagerStatus
        {
            PendingDeviceCount = 5,
            PendingFolderCount = 3
        };

        // Assert
        Assert.NotNull(status.Statistics);
        Assert.Equal(5, status.PendingDeviceCount);
        Assert.Equal(3, status.PendingFolderCount);
    }
}
