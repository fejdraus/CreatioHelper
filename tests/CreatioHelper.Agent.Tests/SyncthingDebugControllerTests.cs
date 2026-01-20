using CreatioHelper.Agent.Controllers;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.Agent.Tests;

public class SyncthingDebugControllerTests
{
    private readonly Mock<ISyncEngine> _syncEngineMock;
    private readonly Mock<ILogger<SyncthingDebugController>> _loggerMock;
    private readonly SyncthingDebugController _controller;

    public SyncthingDebugControllerTests()
    {
        _syncEngineMock = new Mock<ISyncEngine>();
        _loggerMock = new Mock<ILogger<SyncthingDebugController>>();

        _syncEngineMock.Setup(s => s.DeviceId).Returns("TEST-DEVICE-ID");
        _syncEngineMock.Setup(s => s.GetStatisticsAsync())
            .ReturnsAsync(new SyncStatistics
            {
                StartTime = DateTime.UtcNow.AddHours(-1),
                Uptime = TimeSpan.FromHours(1),
                ConnectedDevices = 1,
                TotalDevices = 2
            });
        _syncEngineMock.Setup(s => s.GetDevicesAsync())
            .ReturnsAsync(new List<SyncDevice>());
        _syncEngineMock.Setup(s => s.GetFoldersAsync())
            .ReturnsAsync(new List<SyncFolder>());
        _syncEngineMock.Setup(s => s.GetConfigurationAsync())
            .ReturnsAsync(new SyncConfiguration());

        _controller = new SyncthingDebugController(_syncEngineMock.Object, _loggerMock.Object);
    }

    #region GetCpuProfile Tests

    [Fact]
    public void GetCpuProfile_ReturnsOk()
    {
        var result = _controller.GetCpuProfile();

        Assert.IsType<OkObjectResult>(result);
    }

    [Fact]
    public void GetCpuProfile_ClampsDuration()
    {
        // Test with duration exceeding max (300)
        var result = _controller.GetCpuProfile(500);

        Assert.IsType<OkObjectResult>(result);
    }

    #endregion

    #region GetHeapProfile Tests

    [Fact]
    public void GetHeapProfile_ReturnsMemoryInfo()
    {
        var result = _controller.GetHeapProfile();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);

        var memoryInfoType = ok.Value.GetType();
        Assert.NotNull(memoryInfoType.GetProperty("totalMemory"));
        Assert.NotNull(memoryInfoType.GetProperty("workingSet"));
    }

    #endregion

    #region GetSupportBundle Tests

    [Fact]
    public async Task GetSupportBundle_ReturnsSupportBundle()
    {
        var result = await _controller.GetSupportBundle();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task GetSupportBundle_ContainsExpectedSections()
    {
        var result = await _controller.GetSupportBundle();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var bundleType = ok.Value!.GetType();

        Assert.NotNull(bundleType.GetProperty("timestamp"));
        Assert.NotNull(bundleType.GetProperty("version"));
        Assert.NotNull(bundleType.GetProperty("system"));
        Assert.NotNull(bundleType.GetProperty("config"));
        Assert.NotNull(bundleType.GetProperty("statistics"));
    }

    #endregion

    #region GetDebugFile Tests

    [Fact]
    public async Task GetDebugFile_ReturnsBadRequest_WhenParametersEmpty()
    {
        var result = await _controller.GetDebugFile(string.Empty, "folder");
        Assert.IsType<BadRequestObjectResult>(result.Result);

        result = await _controller.GetDebugFile("file", string.Empty);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetDebugFile_ReturnsBadRequest_WhenPathContainsTraversal()
    {
        var result = await _controller.GetDebugFile("../../../etc/passwd", "folder");

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetDebugFile_ReturnsNotFound_WhenFolderNotExists()
    {
        _syncEngineMock.Setup(s => s.GetFolderAsync("unknown"))
            .ReturnsAsync((SyncFolder?)null);

        var result = await _controller.GetDebugFile("file.txt", "unknown");

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    #endregion

    #region GetPprofIndex Tests

    [Fact]
    public void GetPprofIndex_ReturnsProfilesList()
    {
        var result = _controller.GetPprofIndex();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);

        var profilesProp = ok.Value.GetType().GetProperty("profiles");
        Assert.NotNull(profilesProp);
    }

    #endregion

    #region GetGoroutines Tests

    [Fact]
    public void GetGoroutines_ReturnsThreadInfo()
    {
        var result = _controller.GetGoroutines();

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);

        var countProp = ok.Value.GetType().GetProperty("count");
        Assert.NotNull(countProp);
        var threadsProp = ok.Value.GetType().GetProperty("threads");
        Assert.NotNull(threadsProp);
    }

    [Fact]
    public void GetGoroutines_CountIsPositive()
    {
        var result = _controller.GetGoroutines();

        var ok = Assert.IsType<OkObjectResult>(result);
        var count = (int)ok.Value!.GetType().GetProperty("count")!.GetValue(ok.Value)!;
        Assert.True(count > 0);
    }

    #endregion
}
