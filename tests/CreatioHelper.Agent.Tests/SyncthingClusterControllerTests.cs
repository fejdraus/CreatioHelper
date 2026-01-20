using CreatioHelper.Agent.Controllers;
using CreatioHelper.Infrastructure.Services.Sync.DeviceManagement;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.Agent.Tests;

public class SyncthingClusterControllerTests
{
    private readonly Mock<IPendingService> _pendingServiceMock;
    private readonly Mock<ILogger<SyncthingClusterController>> _loggerMock;
    private readonly SyncthingClusterController _controller;

    public SyncthingClusterControllerTests()
    {
        _pendingServiceMock = new Mock<IPendingService>();
        _loggerMock = new Mock<ILogger<SyncthingClusterController>>();
        _controller = new SyncthingClusterController(_pendingServiceMock.Object, _loggerMock.Object);
    }

    #region GetPendingDevices Tests

    [Fact]
    public void GetPendingDevices_ReturnsEmptyDictionary_WhenNoPendingDevices()
    {
        _pendingServiceMock.Setup(s => s.GetPendingDevices())
            .Returns(new List<PendingDevice>());

        var result = _controller.GetPendingDevices();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var devices = Assert.IsType<Dictionary<string, object>>(ok.Value);
        Assert.Empty(devices);
    }

    [Fact]
    public void GetPendingDevices_ReturnsDevices_WhenPendingDevicesExist()
    {
        var pendingDevices = new List<PendingDevice>
        {
            new PendingDevice
            {
                DeviceId = "DEVICE-1",
                Name = "Test Device",
                Address = "192.168.1.100",
                DiscoveredAt = DateTime.UtcNow
            }
        };
        _pendingServiceMock.Setup(s => s.GetPendingDevices())
            .Returns(pendingDevices);

        var result = _controller.GetPendingDevices();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var devices = Assert.IsType<Dictionary<string, object>>(ok.Value);
        Assert.Single(devices);
        Assert.True(devices.ContainsKey("DEVICE-1"));
    }

    #endregion

    #region DeletePendingDevice Tests

    [Fact]
    public void DeletePendingDevice_ReturnsBadRequest_WhenDeviceIsEmpty()
    {
        var result = _controller.DeletePendingDevice(string.Empty);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("device parameter required", badRequest.Value?.ToString());
    }

    [Fact]
    public void DeletePendingDevice_ReturnsNotFound_WhenDeviceNotPending()
    {
        _pendingServiceMock.Setup(s => s.RejectPendingDevice("unknown"))
            .Returns(false);

        var result = _controller.DeletePendingDevice("unknown");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void DeletePendingDevice_ReturnsOk_WhenDeviceRejected()
    {
        _pendingServiceMock.Setup(s => s.RejectPendingDevice("DEVICE-1"))
            .Returns(true);

        var result = _controller.DeletePendingDevice("DEVICE-1");

        Assert.IsType<OkObjectResult>(result);
    }

    #endregion

    #region GetPendingFolders Tests

    [Fact]
    public void GetPendingFolders_ReturnsEmptyDictionary_WhenNoPendingFolders()
    {
        _pendingServiceMock.Setup(s => s.GetPendingFolders())
            .Returns(new List<PendingFolder>());

        var result = _controller.GetPendingFolders();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var folders = Assert.IsType<Dictionary<string, Dictionary<string, object>>>(ok.Value);
        Assert.Empty(folders);
    }

    [Fact]
    public void GetPendingFolders_ReturnsFolders_WhenPendingFoldersExist()
    {
        var pendingFolders = new List<PendingFolder>
        {
            new PendingFolder
            {
                FolderId = "folder-1",
                Label = "Test Folder",
                OfferedByDeviceId = "DEVICE-1",
                OfferedAt = DateTime.UtcNow
            }
        };
        _pendingServiceMock.Setup(s => s.GetPendingFolders())
            .Returns(pendingFolders);

        var result = _controller.GetPendingFolders();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var folders = Assert.IsType<Dictionary<string, Dictionary<string, object>>>(ok.Value);
        Assert.Single(folders);
        Assert.True(folders.ContainsKey("DEVICE-1"));
        Assert.True(folders["DEVICE-1"].ContainsKey("folder-1"));
    }

    #endregion

    #region DeletePendingFolder Tests

    [Fact]
    public void DeletePendingFolder_ReturnsBadRequest_WhenParametersEmpty()
    {
        var result = _controller.DeletePendingFolder(string.Empty, "folder");
        Assert.IsType<BadRequestObjectResult>(result);

        result = _controller.DeletePendingFolder("device", string.Empty);
        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void DeletePendingFolder_ReturnsNotFound_WhenFolderNotPending()
    {
        _pendingServiceMock.Setup(s => s.RejectPendingFolder("folder-1", "DEVICE-1"))
            .Returns(false);

        var result = _controller.DeletePendingFolder("DEVICE-1", "folder-1");

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public void DeletePendingFolder_ReturnsOk_WhenFolderRejected()
    {
        _pendingServiceMock.Setup(s => s.RejectPendingFolder("folder-1", "DEVICE-1"))
            .Returns(true);

        var result = _controller.DeletePendingFolder("DEVICE-1", "folder-1");

        Assert.IsType<OkObjectResult>(result);
    }

    #endregion
}
