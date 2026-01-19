using CreatioHelper.Infrastructure.Services.Sync.DeviceManagement;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.DeviceManagement;

public class PendingServiceTests
{
    private readonly Mock<ILogger<PendingService>> _loggerMock;
    private readonly PendingServiceConfiguration _config;
    private readonly PendingService _service;

    public PendingServiceTests()
    {
        _loggerMock = new Mock<ILogger<PendingService>>();
        _config = new PendingServiceConfiguration();
        _service = new PendingService(_loggerMock.Object, _config);
    }

    #region Pending Device Tests

    [Fact]
    public void AddPendingDevice_ValidDevice_AddsToList()
    {
        var device = new PendingDevice { DeviceId = "device1", Name = "Test Device" };

        _service.AddPendingDevice(device);

        Assert.True(_service.IsDevicePending("device1"));
    }

    [Fact]
    public void AddPendingDevice_RaisesEvent()
    {
        var device = new PendingDevice { DeviceId = "device1", Name = "Test Device" };
        PendingDeviceEventArgs? eventArgs = null;
        _service.DevicePending += (s, e) => eventArgs = e;

        _service.AddPendingDevice(device);

        Assert.NotNull(eventArgs);
        Assert.Equal("device1", eventArgs.Device.DeviceId);
    }

    [Fact]
    public void AddPendingDevice_RejectedDevice_Ignores()
    {
        _config.RejectedDevices.Add("device1");
        var device = new PendingDevice { DeviceId = "device1", Name = "Test Device" };

        _service.AddPendingDevice(device);

        Assert.False(_service.IsDevicePending("device1"));
    }

    [Fact]
    public void AddPendingDevice_AutoAcceptIntroduced_ApprovesImmediately()
    {
        _config.AutoAcceptIntroducedDevices = true;
        _config.TrustedIntroducers.Add("introducer1");
        var device = new PendingDevice
        {
            DeviceId = "device1",
            Name = "Test Device",
            IntroducedBy = "introducer1"
        };
        PendingDeviceEventArgs? approvedArgs = null;
        _service.DeviceApproved += (s, e) => approvedArgs = e;

        _service.AddPendingDevice(device);

        Assert.NotNull(approvedArgs);
        Assert.False(_service.IsDevicePending("device1"));
    }

    [Fact]
    public void AddPendingDevice_NullDevice_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.AddPendingDevice(null!));
    }

    [Fact]
    public void AddPendingDevice_EmptyDeviceId_ThrowsArgumentException()
    {
        var device = new PendingDevice { DeviceId = "", Name = "Test" };

        Assert.Throws<ArgumentException>(() =>
            _service.AddPendingDevice(device));
    }

    [Fact]
    public void GetPendingDevices_ReturnsAll()
    {
        _service.AddPendingDevice(new PendingDevice { DeviceId = "device1", Name = "Device 1" });
        _service.AddPendingDevice(new PendingDevice { DeviceId = "device2", Name = "Device 2" });

        var devices = _service.GetPendingDevices();

        Assert.Equal(2, devices.Count);
    }

    [Fact]
    public void GetPendingDevice_Exists_ReturnsDevice()
    {
        _service.AddPendingDevice(new PendingDevice { DeviceId = "device1", Name = "Device 1" });

        var device = _service.GetPendingDevice("device1");

        Assert.NotNull(device);
        Assert.Equal("Device 1", device.Name);
    }

    [Fact]
    public void GetPendingDevice_NotExists_ReturnsNull()
    {
        var device = _service.GetPendingDevice("nonexistent");

        Assert.Null(device);
    }

    [Fact]
    public void ApprovePendingDevice_Exists_RemovesAndReturnsTrue()
    {
        _service.AddPendingDevice(new PendingDevice { DeviceId = "device1", Name = "Device 1" });

        var result = _service.ApprovePendingDevice("device1");

        Assert.True(result);
        Assert.False(_service.IsDevicePending("device1"));
    }

    [Fact]
    public void ApprovePendingDevice_RaisesEvent()
    {
        _service.AddPendingDevice(new PendingDevice { DeviceId = "device1", Name = "Device 1" });
        PendingDeviceEventArgs? eventArgs = null;
        _service.DeviceApproved += (s, e) => eventArgs = e;

        _service.ApprovePendingDevice("device1");

        Assert.NotNull(eventArgs);
        Assert.Equal("device1", eventArgs.Device.DeviceId);
    }

    [Fact]
    public void ApprovePendingDevice_NotExists_ReturnsFalse()
    {
        var result = _service.ApprovePendingDevice("nonexistent");

        Assert.False(result);
    }

    [Fact]
    public void RejectPendingDevice_Exists_RemovesAndAddsToRejected()
    {
        _service.AddPendingDevice(new PendingDevice { DeviceId = "device1", Name = "Device 1" });

        var result = _service.RejectPendingDevice("device1");

        Assert.True(result);
        Assert.False(_service.IsDevicePending("device1"));
        Assert.Contains("device1", _config.RejectedDevices);
    }

    [Fact]
    public void RejectPendingDevice_NotExists_ReturnsFalse()
    {
        var result = _service.RejectPendingDevice("nonexistent");

        Assert.False(result);
    }

    #endregion

    #region Pending Folder Tests

    [Fact]
    public void AddPendingFolder_ValidFolder_AddsToList()
    {
        var folder = new PendingFolder
        {
            FolderId = "folder1",
            Label = "Test Folder",
            OfferedByDeviceId = "device1"
        };

        _service.AddPendingFolder(folder);

        Assert.True(_service.IsFolderPending("folder1", "device1"));
    }

    [Fact]
    public void AddPendingFolder_RaisesEvent()
    {
        var folder = new PendingFolder
        {
            FolderId = "folder1",
            Label = "Test Folder",
            OfferedByDeviceId = "device1"
        };
        PendingFolderEventArgs? eventArgs = null;
        _service.FolderPending += (s, e) => eventArgs = e;

        _service.AddPendingFolder(folder);

        Assert.NotNull(eventArgs);
        Assert.Equal("folder1", eventArgs.Folder.FolderId);
    }

    [Fact]
    public void AddPendingFolder_RejectedFolder_Ignores()
    {
        _config.RejectedFolders.Add("folder1:device1");
        var folder = new PendingFolder
        {
            FolderId = "folder1",
            Label = "Test Folder",
            OfferedByDeviceId = "device1"
        };

        _service.AddPendingFolder(folder);

        Assert.False(_service.IsFolderPending("folder1", "device1"));
    }

    [Fact]
    public void AddPendingFolder_AutoAccept_AcceptsImmediately()
    {
        _config.AutoAcceptFolders = true;
        _config.AutoAcceptFromDevices.Add("device1");
        var folder = new PendingFolder
        {
            FolderId = "folder1",
            Label = "Test Folder",
            OfferedByDeviceId = "device1"
        };
        PendingFolderEventArgs? acceptedArgs = null;
        _service.FolderAccepted += (s, e) => acceptedArgs = e;

        _service.AddPendingFolder(folder);

        Assert.NotNull(acceptedArgs);
        Assert.False(_service.IsFolderPending("folder1", "device1"));
    }

    [Fact]
    public void AddPendingFolder_NullFolder_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.AddPendingFolder(null!));
    }

    [Fact]
    public void AddPendingFolder_EmptyFolderId_ThrowsArgumentException()
    {
        var folder = new PendingFolder { FolderId = "", OfferedByDeviceId = "device1" };

        Assert.Throws<ArgumentException>(() =>
            _service.AddPendingFolder(folder));
    }

    [Fact]
    public void GetPendingFolders_ReturnsAll()
    {
        _service.AddPendingFolder(new PendingFolder { FolderId = "folder1", OfferedByDeviceId = "device1" });
        _service.AddPendingFolder(new PendingFolder { FolderId = "folder2", OfferedByDeviceId = "device2" });

        var folders = _service.GetPendingFolders();

        Assert.Equal(2, folders.Count);
    }

    [Fact]
    public void GetPendingFoldersFromDevice_ReturnsFiltered()
    {
        _service.AddPendingFolder(new PendingFolder { FolderId = "folder1", OfferedByDeviceId = "device1" });
        _service.AddPendingFolder(new PendingFolder { FolderId = "folder2", OfferedByDeviceId = "device1" });
        _service.AddPendingFolder(new PendingFolder { FolderId = "folder3", OfferedByDeviceId = "device2" });

        var folders = _service.GetPendingFoldersFromDevice("device1");

        Assert.Equal(2, folders.Count);
    }

    [Fact]
    public void GetPendingFolder_Exists_ReturnsFolder()
    {
        _service.AddPendingFolder(new PendingFolder
        {
            FolderId = "folder1",
            Label = "Test",
            OfferedByDeviceId = "device1"
        });

        var folder = _service.GetPendingFolder("folder1", "device1");

        Assert.NotNull(folder);
        Assert.Equal("Test", folder.Label);
    }

    [Fact]
    public void GetPendingFolder_NotExists_ReturnsNull()
    {
        var folder = _service.GetPendingFolder("nonexistent", "device1");

        Assert.Null(folder);
    }

    [Fact]
    public void AcceptPendingFolder_Exists_RemovesAndReturnsTrue()
    {
        _service.AddPendingFolder(new PendingFolder
        {
            FolderId = "folder1",
            OfferedByDeviceId = "device1"
        });

        var result = _service.AcceptPendingFolder("folder1", "device1", "/local/path");

        Assert.True(result);
        Assert.False(_service.IsFolderPending("folder1", "device1"));
    }

    [Fact]
    public void AcceptPendingFolder_RaisesEvent()
    {
        _service.AddPendingFolder(new PendingFolder
        {
            FolderId = "folder1",
            OfferedByDeviceId = "device1"
        });
        PendingFolderEventArgs? eventArgs = null;
        _service.FolderAccepted += (s, e) => eventArgs = e;

        _service.AcceptPendingFolder("folder1", "device1", "/local/path");

        Assert.NotNull(eventArgs);
        Assert.Equal("/local/path", eventArgs.AcceptedLocalPath);
    }

    [Fact]
    public void AcceptPendingFolder_NotExists_ReturnsFalse()
    {
        var result = _service.AcceptPendingFolder("nonexistent", "device1");

        Assert.False(result);
    }

    [Fact]
    public void RejectPendingFolder_Exists_RemovesAndAddsToRejected()
    {
        _service.AddPendingFolder(new PendingFolder
        {
            FolderId = "folder1",
            OfferedByDeviceId = "device1"
        });

        var result = _service.RejectPendingFolder("folder1", "device1");

        Assert.True(result);
        Assert.False(_service.IsFolderPending("folder1", "device1"));
        Assert.Contains("folder1:device1", _config.RejectedFolders);
    }

    [Fact]
    public void RejectPendingFolder_NotExists_ReturnsFalse()
    {
        var result = _service.RejectPendingFolder("nonexistent", "device1");

        Assert.False(result);
    }

    #endregion
}

public class PendingServiceConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new PendingServiceConfiguration();

        Assert.False(config.AutoAcceptIntroducedDevices);
        Assert.False(config.AutoAcceptFolders);
        Assert.Equal(TimeSpan.FromDays(30), config.PendingExpiration);
        Assert.Empty(config.TrustedIntroducers);
        Assert.Empty(config.AutoAcceptFromDevices);
        Assert.Empty(config.RejectedDevices);
        Assert.Empty(config.RejectedFolders);
    }
}
