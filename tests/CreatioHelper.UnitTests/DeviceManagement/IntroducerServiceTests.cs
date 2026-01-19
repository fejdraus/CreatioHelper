using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.DeviceManagement;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests.DeviceManagement;

public class IntroducerServiceTests
{
    private readonly Mock<ILogger<IntroducerService>> _loggerMock;
    private readonly Mock<IDeviceInfoRepository> _deviceRepoMock;
    private readonly Mock<IFolderConfigRepository> _folderRepoMock;
    private readonly Mock<IEventLogger> _eventLoggerMock;
    private readonly IntroducerService _service;

    public IntroducerServiceTests()
    {
        _loggerMock = new Mock<ILogger<IntroducerService>>();
        _deviceRepoMock = new Mock<IDeviceInfoRepository>();
        _folderRepoMock = new Mock<IFolderConfigRepository>();
        _eventLoggerMock = new Mock<IEventLogger>();

        _service = new IntroducerService(
            _loggerMock.Object,
            _deviceRepoMock.Object,
            _folderRepoMock.Object,
            _eventLoggerMock.Object);
    }

    [Fact]
    public async Task ProcessIntroductionAsync_ReturnsError_WhenIntroducerNotFound()
    {
        // Arrange
        _deviceRepoMock.Setup(r => r.GetAsync("introducer-1"))
            .ReturnsAsync((SyncDevice?)null);

        // Act
        var result = await _service.ProcessIntroductionAsync(
            "introducer-1",
            Array.Empty<IntroducedDevice>(),
            Array.Empty<IntroducedFolderShare>());

        // Assert
        Assert.Contains("not found", result.Errors.FirstOrDefault() ?? "");
    }

    [Fact]
    public async Task ProcessIntroductionAsync_ReturnsEmpty_WhenDeviceIsNotIntroducer()
    {
        // Arrange
        var device = new SyncDevice("device-1", "Not Introducer") { Introducer = false };
        _deviceRepoMock.Setup(r => r.GetAsync("device-1")).ReturnsAsync(device);

        // Act
        var result = await _service.ProcessIntroductionAsync(
            "device-1",
            Array.Empty<IntroducedDevice>(),
            Array.Empty<IntroducedFolderShare>());

        // Assert
        Assert.False(result.ChangesMade);
        Assert.Empty(result.AddedDevices);
    }

    [Fact]
    public async Task ProcessIntroductionAsync_AddsNewDevice()
    {
        // Arrange
        var introducer = new SyncDevice("introducer-1", "Introducer") { Introducer = true };
        _deviceRepoMock.Setup(r => r.GetAsync("introducer-1")).ReturnsAsync(introducer);
        _deviceRepoMock.Setup(r => r.GetAsync("new-device")).ReturnsAsync((SyncDevice?)null);
        _deviceRepoMock.Setup(r => r.UpsertAsync(It.IsAny<SyncDevice>())).Returns(Task.CompletedTask);

        var introducedDevices = new[]
        {
            new IntroducedDevice
            {
                DeviceId = "new-device",
                DeviceName = "New Device",
                Addresses = new List<string> { "tcp://192.168.1.100:22000" }
            }
        };

        // Act
        var result = await _service.ProcessIntroductionAsync(
            "introducer-1",
            introducedDevices,
            Array.Empty<IntroducedFolderShare>());

        // Assert
        Assert.True(result.ChangesMade);
        Assert.Contains("new-device", result.AddedDevices);
        _deviceRepoMock.Verify(r => r.UpsertAsync(It.Is<SyncDevice>(d => d.DeviceId == "new-device")), Times.Once);
    }

    [Fact]
    public async Task ProcessIntroductionAsync_UpdatesExistingDevice()
    {
        // Arrange
        var introducer = new SyncDevice("introducer-1", "Introducer") { Introducer = true };
        var existingDevice = new SyncDevice("existing-device", "Existing");
        existingDevice.UpdateAddresses(new List<string> { "tcp://192.168.1.1:22000" });

        _deviceRepoMock.Setup(r => r.GetAsync("introducer-1")).ReturnsAsync(introducer);
        _deviceRepoMock.Setup(r => r.GetAsync("existing-device")).ReturnsAsync(existingDevice);
        _deviceRepoMock.Setup(r => r.UpsertAsync(It.IsAny<SyncDevice>())).Returns(Task.CompletedTask);

        var introducedDevices = new[]
        {
            new IntroducedDevice
            {
                DeviceId = "existing-device",
                DeviceName = "Existing",
                Addresses = new List<string> { "tcp://192.168.1.2:22000" } // New address
            }
        };

        // Act
        var result = await _service.ProcessIntroductionAsync(
            "introducer-1",
            introducedDevices,
            Array.Empty<IntroducedFolderShare>());

        // Assert
        Assert.True(result.ChangesMade);
        Assert.Contains("existing-device", result.UpdatedDevices);
    }

    [Fact]
    public async Task ProcessIntroductionAsync_SkipsIntroducerItself()
    {
        // Arrange
        var introducer = new SyncDevice("introducer-1", "Introducer") { Introducer = true };
        _deviceRepoMock.Setup(r => r.GetAsync("introducer-1")).ReturnsAsync(introducer);

        var introducedDevices = new[]
        {
            new IntroducedDevice
            {
                DeviceId = "introducer-1", // Same as introducer
                DeviceName = "Self"
            }
        };

        // Act
        var result = await _service.ProcessIntroductionAsync(
            "introducer-1",
            introducedDevices,
            Array.Empty<IntroducedFolderShare>());

        // Assert
        Assert.False(result.ChangesMade);
        Assert.Empty(result.AddedDevices);
    }

    [Fact]
    public async Task ProcessIntroductionAsync_AddsFolderShares()
    {
        // Arrange
        var introducer = new SyncDevice("introducer-1", "Introducer") { Introducer = true };
        var folder = new SyncFolder("folder-1", "Test Folder", "/path/to/folder");
        folder.AddDevice("introducer-1"); // Folder shared with introducer

        _deviceRepoMock.Setup(r => r.GetAsync("introducer-1")).ReturnsAsync(introducer);
        _deviceRepoMock.Setup(r => r.GetAsync("new-device")).ReturnsAsync((SyncDevice?)null);
        _deviceRepoMock.Setup(r => r.UpsertAsync(It.IsAny<SyncDevice>())).Returns(Task.CompletedTask);
        _folderRepoMock.Setup(r => r.GetAsync("folder-1")).ReturnsAsync(folder);
        _folderRepoMock.Setup(r => r.UpsertAsync(It.IsAny<SyncFolder>())).Returns(Task.CompletedTask);

        var introducedDevices = new[]
        {
            new IntroducedDevice { DeviceId = "new-device", DeviceName = "New" }
        };

        var folderShares = new[]
        {
            new IntroducedFolderShare
            {
                FolderId = "folder-1",
                SharedWithDevices = new List<string> { "new-device" }
            }
        };

        // Act
        var result = await _service.ProcessIntroductionAsync(
            "introducer-1",
            introducedDevices,
            folderShares);

        // Assert
        Assert.True(result.ChangesMade);
        Assert.NotEmpty(result.FolderShareChanges);
        Assert.Contains(result.FolderShareChanges, c => c.FolderId == "folder-1" && c.DeviceId == "new-device");
    }

    [Fact]
    public async Task SetIntroducerAsync_UpdatesDeviceProperty()
    {
        // Arrange
        var device = new SyncDevice("device-1", "Test Device") { Introducer = false };
        _deviceRepoMock.Setup(r => r.GetAsync("device-1")).ReturnsAsync(device);
        _deviceRepoMock.Setup(r => r.UpsertAsync(It.IsAny<SyncDevice>())).Returns(Task.CompletedTask);

        // Act
        await _service.SetIntroducerAsync("device-1", true);

        // Assert
        _deviceRepoMock.Verify(r => r.UpsertAsync(It.Is<SyncDevice>(d => d.Introducer)), Times.Once);
    }

    [Fact]
    public async Task SetIntroducerAsync_ThrowsWhenDeviceNotFound()
    {
        // Arrange
        _deviceRepoMock.Setup(r => r.GetAsync("nonexistent")).ReturnsAsync((SyncDevice?)null);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SetIntroducerAsync("nonexistent", true));
    }

    [Fact]
    public async Task GetIntroducedDevicesAsync_ReturnsDevicesWithIntroducedBy()
    {
        // Arrange
        var devices = new List<SyncDevice>
        {
            new SyncDevice("device-1", "Device 1", "metadata", false, false, "introducer-1", false, false, 0, 0, 0, false, 0, 1, ""),
            new SyncDevice("device-2", "Device 2", "metadata", false, false, "", false, false, 0, 0, 0, false, 0, 1, ""),
            new SyncDevice("device-3", "Device 3", "metadata", false, false, "introducer-1", false, false, 0, 0, 0, false, 0, 1, "")
        };
        _deviceRepoMock.Setup(r => r.GetAllAsync()).ReturnsAsync(devices);

        // Act
        var result = await _service.GetIntroducedDevicesAsync("introducer-1");

        // Assert
        Assert.Equal(2, result.Count());
        Assert.All(result, d => Assert.Equal("introducer-1", d.IntroducedBy));
    }

    [Fact]
    public async Task IsIntroducedDeviceAsync_ReturnsTrueForIntroducedDevice()
    {
        // Arrange
        var device = new SyncDevice("device-1", "Device", "metadata", false, false, "introducer-1", false, false, 0, 0, 0, false, 0, 1, "");
        _deviceRepoMock.Setup(r => r.GetAsync("device-1")).ReturnsAsync(device);

        // Act
        var result = await _service.IsIntroducedDeviceAsync("device-1");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task IsIntroducedDeviceAsync_ReturnsFalseForManuallyAddedDevice()
    {
        // Arrange
        var device = new SyncDevice("device-1", "Device");
        _deviceRepoMock.Setup(r => r.GetAsync("device-1")).ReturnsAsync(device);

        // Act
        var result = await _service.IsIntroducedDeviceAsync("device-1");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void GetStatus_ReturnsCorrectStatus()
    {
        // Act
        var status = _service.GetStatus();

        // Assert
        Assert.True(status.IsRunning);
        Assert.NotNull(status.Statistics);
    }

    [Fact]
    public void IntroducedDevice_HasCorrectDefaults()
    {
        // Arrange
        var device = new IntroducedDevice();

        // Assert
        Assert.Equal(string.Empty, device.DeviceId);
        Assert.Equal("metadata", device.Compression);
        Assert.Equal(2048, device.MaxRequestKiB);
        Assert.False(device.IsIntroducer);
    }

    [Fact]
    public void IntroducedFolderShare_HasCorrectStructure()
    {
        // Arrange
        var share = new IntroducedFolderShare
        {
            FolderId = "folder-1",
            FolderLabel = "Test",
            SharedWithDevices = new List<string> { "device-1", "device-2" },
            Encrypted = true
        };

        // Assert
        Assert.Equal("folder-1", share.FolderId);
        Assert.Equal("Test", share.FolderLabel);
        Assert.Equal(2, share.SharedWithDevices.Count);
        Assert.True(share.Encrypted);
    }

    [Fact]
    public void IntroductionResult_TracksAllChangeTypes()
    {
        // Arrange
        var result = new IntroductionResult
        {
            ChangesMade = true,
            AddedDevices = new List<string> { "device-1" },
            UpdatedDevices = new List<string> { "device-2" },
            RemovedDevices = new List<string> { "device-3" },
            FolderShareChanges = new List<FolderShareChange>
            {
                new() { FolderId = "folder-1", DeviceId = "device-1", ChangeType = FolderShareChangeType.Added }
            },
            Errors = new List<string> { "error-1" }
        };

        // Assert
        Assert.True(result.ChangesMade);
        Assert.Single(result.AddedDevices);
        Assert.Single(result.UpdatedDevices);
        Assert.Single(result.RemovedDevices);
        Assert.Single(result.FolderShareChanges);
        Assert.Single(result.Errors);
    }
}
