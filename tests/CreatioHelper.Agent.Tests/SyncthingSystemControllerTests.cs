using CreatioHelper.Agent.Controllers;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.Agent.Tests;

public class SyncthingSystemControllerTests
{
    private readonly Mock<ISyncEngine> _syncEngineMock;
    private readonly Mock<ILogger<SyncthingSystemController>> _loggerMock;
    private readonly IConfiguration _configuration;
    private readonly SyncthingSystemController _controller;

    public SyncthingSystemControllerTests()
    {
        _syncEngineMock = new Mock<ISyncEngine>();
        _loggerMock = new Mock<ILogger<SyncthingSystemController>>();
        _configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        _syncEngineMock.Setup(s => s.DeviceId).Returns("TEST-DEVICE-ID");
        _syncEngineMock.Setup(s => s.GetStatisticsAsync())
            .ReturnsAsync(new SyncStatistics
            {
                StartTime = DateTime.UtcNow.AddHours(-1),
                Uptime = TimeSpan.FromHours(1),
                TotalBytesIn = 1000,
                TotalBytesOut = 500
            });
        _syncEngineMock.Setup(s => s.GetDevicesAsync())
            .ReturnsAsync(new List<SyncDevice>());
        _syncEngineMock.Setup(s => s.GetFoldersAsync())
            .ReturnsAsync(new List<SyncFolder>());
        _syncEngineMock.Setup(s => s.GetConfigurationAsync())
            .ReturnsAsync(new SyncConfiguration());

        _controller = new SyncthingSystemController(
            _syncEngineMock.Object,
            _loggerMock.Object,
            _configuration);
    }

    #region Browse Tests

    [Fact]
    public void Browse_ReturnsArray_WhenPathIsValid()
    {
        var result = _controller.Browse(null);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
        Assert.IsType<string[]>(ok.Value);
    }

    [Fact]
    public void Browse_ReturnsBadRequest_WhenPathContainsTraversal()
    {
        var result = _controller.Browse("../../../etc");

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    #endregion

    #region Connections Tests

    [Fact]
    public async Task GetConnections_ReturnsConnectionsObject()
    {
        var devices = new List<SyncDevice>
        {
            CreateTestDevice("DEVICE-1", "Device 1")
        };
        _syncEngineMock.Setup(s => s.GetDevicesAsync()).ReturnsAsync(devices);

        var result = await _controller.GetConnections();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    #endregion

    #region Discovery Tests

    [Fact]
    public async Task GetDiscovery_ReturnsDiscoveryStatus()
    {
        var devices = new List<SyncDevice>
        {
            CreateTestDevice("DEVICE-1", "Device 1", new List<string> { "tcp://192.168.1.100:22000" })
        };
        _syncEngineMock.Setup(s => s.GetDevicesAsync()).ReturnsAsync(devices);

        var result = await _controller.GetDiscovery();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    #endregion

    #region Error Tests

    [Fact]
    public void GetErrors_ReturnsErrorsObject()
    {
        var result = _controller.GetErrors();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void PostError_ReturnsBadRequest_WhenMessageIsEmpty()
    {
        var result = _controller.PostError(string.Empty);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void PostError_ReturnsOk_WhenMessageIsValid()
    {
        var result = _controller.PostError("Test error message");

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void ClearErrors_ReturnsOk()
    {
        var result = _controller.ClearErrors();

        Assert.IsType<OkObjectResult>(result);
    }

    #endregion

    #region Paths Tests

    [Fact]
    public void GetPaths_ReturnsPathsObject()
    {
        var result = _controller.GetPaths();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    #endregion

    #region Upgrade Tests

    [Fact]
    public void GetUpgrade_ReturnsUpgradeInfo()
    {
        var result = _controller.GetUpgrade();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void DoUpgrade_ReturnsOk()
    {
        var result = _controller.DoUpgrade();

        Assert.IsType<OkObjectResult>(result);
    }

    #endregion

    #region Ping Tests

    [Fact]
    public void Ping_ReturnsPong()
    {
        var result = _controller.Ping();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    #endregion

    #region Pause/Resume Tests

    [Fact]
    public async Task Pause_ReturnsOk_WhenDeviceSpecified()
    {
        var result = await _controller.Pause("DEVICE-1");

        Assert.IsType<OkObjectResult>(result);
        _syncEngineMock.Verify(s => s.PauseDeviceAsync("DEVICE-1"), Times.Once);
    }

    [Fact]
    public async Task Pause_PausesAllDevices_WhenNoDeviceSpecified()
    {
        var devices = new List<SyncDevice>
        {
            CreateTestDevice("DEVICE-1", "Device 1"),
            CreateTestDevice("DEVICE-2", "Device 2")
        };
        _syncEngineMock.Setup(s => s.GetDevicesAsync()).ReturnsAsync(devices);

        var result = await _controller.Pause(null);

        Assert.IsType<OkObjectResult>(result);
        _syncEngineMock.Verify(s => s.PauseDeviceAsync(It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Resume_ReturnsOk_WhenDeviceSpecified()
    {
        var result = await _controller.Resume("DEVICE-1");

        Assert.IsType<OkObjectResult>(result);
        _syncEngineMock.Verify(s => s.ResumeDeviceAsync("DEVICE-1"), Times.Once);
    }

    #endregion

    #region Reset Tests

    [Fact]
    public async Task Reset_ReturnsOk_WhenFolderSpecified()
    {
        var result = await _controller.Reset("folder-1");

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public async Task Reset_ReturnsOk_WhenNoFolderSpecified()
    {
        var result = await _controller.Reset(null);

        Assert.IsType<OkObjectResult>(result);
    }

    #endregion

    #region Debug Tests

    [Fact]
    public void GetDebug_ReturnsDebugInfo()
    {
        var result = _controller.GetDebug();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void SetDebug_ReturnsOk()
    {
        var request = new DebugRequest { Enable = new[] { "main" } };

        var result = _controller.SetDebug(request);

        Assert.IsType<OkObjectResult>(result);
    }

    #endregion

    #region Existing Endpoints Tests

    [Fact]
    public async Task GetStatus_ReturnsStatusObject()
    {
        var result = await _controller.GetStatus();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void GetVersion_ReturnsVersionObject()
    {
        var result = _controller.GetVersion();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void Restart_ReturnsOk()
    {
        var result = _controller.Restart();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void Shutdown_ReturnsOk()
    {
        var result = _controller.Shutdown();

        Assert.IsType<OkObjectResult>(result);
    }

    #endregion

    #region Log Text Tests

    [Fact]
    public void GetLogText_ReturnsPlainTextContent()
    {
        var result = _controller.GetLogText();

        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal("text/plain", content.ContentType);
        Assert.NotNull(content.Content);
        Assert.Contains("INFO:", content.Content);
    }

    [Fact]
    public void GetLogText_ReturnsMultipleLogLines()
    {
        var result = _controller.GetLogText();

        var content = Assert.IsType<ContentResult>(result);
        var lines = content.Content!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.True(lines.Length > 0);
    }

    [Fact]
    public void GetLogText_IncludesTimestamps()
    {
        var result = _controller.GetLogText();

        var content = Assert.IsType<ContentResult>(result);
        Assert.Matches(@"\[\d{4}-\d{2}-\d{2}", content.Content!);
    }

    #endregion

    #region Log Levels Tests

    [Fact]
    public void GetLogLevels_ReturnsLogLevelsObject()
    {
        var result = _controller.GetLogLevels();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void GetLogLevels_ContainsFacilities()
    {
        var result = _controller.GetLogLevels();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var value = ok.Value;
        Assert.NotNull(value);

        // Check that the object has facilities property
        var facilitiesProperty = value.GetType().GetProperty("facilities");
        Assert.NotNull(facilitiesProperty);
    }

    [Fact]
    public void SetLogLevels_ReturnsOk_WhenEnablingFacility()
    {
        var result = _controller.SetLogLevels("main,model", null);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void SetLogLevels_ReturnsOk_WhenDisablingFacility()
    {
        // First enable some facilities
        _controller.SetLogLevels("main,model", null);

        // Then disable them
        var result = _controller.SetLogLevels(null, "main");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void SetLogLevels_ReturnsOk_WhenBothEnableAndDisable()
    {
        var result = _controller.SetLogLevels("protocol", "model");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void SetLogLevels_ReturnsOk_WithNoParameters()
    {
        var result = _controller.SetLogLevels(null, null);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    #endregion

    private static SyncDevice CreateTestDevice(string id, string name, List<string>? addresses = null)
    {
        var device = new SyncDevice(id, name);
        if (addresses != null)
        {
            device.UpdateAddresses(addresses);
        }
        return device;
    }
}
