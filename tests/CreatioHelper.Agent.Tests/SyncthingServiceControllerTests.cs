using CreatioHelper.Agent.Controllers;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.Agent.Tests;

public class SyncthingServiceControllerTests
{
    private readonly Mock<ISyncEngine> _syncEngineMock;
    private readonly Mock<ILogger<SyncthingServiceController>> _loggerMock;
    private readonly SyncthingServiceController _controller;

    public SyncthingServiceControllerTests()
    {
        _syncEngineMock = new Mock<ISyncEngine>();
        _loggerMock = new Mock<ILogger<SyncthingServiceController>>();

        _syncEngineMock.Setup(s => s.DeviceId).Returns("TEST-DEVICE-ID");
        _syncEngineMock.Setup(s => s.GetStatisticsAsync())
            .ReturnsAsync(new SyncStatistics
            {
                StartTime = DateTime.UtcNow.AddHours(-1),
                Uptime = TimeSpan.FromHours(1)
            });
        _syncEngineMock.Setup(s => s.GetDevicesAsync())
            .ReturnsAsync(new List<SyncDevice>());
        _syncEngineMock.Setup(s => s.GetFoldersAsync())
            .ReturnsAsync(new List<SyncFolder>());
        _syncEngineMock.Setup(s => s.GetConfigurationAsync())
            .ReturnsAsync(new SyncConfiguration());

        _controller = new SyncthingServiceController(_syncEngineMock.Object, _loggerMock.Object);
    }

    #region GetLanguages Tests

    [Fact]
    public void GetLanguages_ReturnsLanguageArray()
    {
        var result = _controller.GetLanguages();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var languages = Assert.IsType<string[]>(ok.Value);
        Assert.NotEmpty(languages);
        Assert.Contains("en", languages);
        Assert.Contains("ru", languages);
    }

    #endregion

    #region GetReport Tests

    [Fact]
    public async Task GetReport_ReturnsReportObject()
    {
        var result = await _controller.GetReport();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public async Task GetReport_ContainsExpectedFields()
    {
        var result = await _controller.GetReport();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var reportType = ok.Value!.GetType();

        Assert.NotNull(reportType.GetProperty("uniqueID"));
        Assert.NotNull(reportType.GetProperty("version"));
        Assert.NotNull(reportType.GetProperty("numDevices"));
        Assert.NotNull(reportType.GetProperty("numFolders"));
    }

    #endregion

    #region GetRandomString Tests

    [Fact]
    public void GetRandomString_ReturnsDefaultLength()
    {
        var result = _controller.GetRandomString();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var randomProp = ok.Value!.GetType().GetProperty("random");
        Assert.NotNull(randomProp);
        var randomValue = (string)randomProp.GetValue(ok.Value)!;
        Assert.Equal(32, randomValue.Length);
    }

    [Fact]
    public void GetRandomString_ReturnsRequestedLength()
    {
        var result = _controller.GetRandomString(64);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var randomProp = ok.Value!.GetType().GetProperty("random");
        Assert.NotNull(randomProp);
        var randomValue = (string)randomProp.GetValue(ok.Value)!;
        Assert.Equal(64, randomValue.Length);
    }

    [Fact]
    public void GetRandomString_ClampsLengthToMinimum()
    {
        var result = _controller.GetRandomString(0);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var randomProp = ok.Value!.GetType().GetProperty("random");
        Assert.NotNull(randomProp);
        var randomValue = (string)randomProp.GetValue(ok.Value)!;
        Assert.Equal(1, randomValue.Length);
    }

    [Fact]
    public void GetRandomString_ClampsLengthToMaximum()
    {
        var result = _controller.GetRandomString(2000);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var randomProp = ok.Value!.GetType().GetProperty("random");
        Assert.NotNull(randomProp);
        var randomValue = (string)randomProp.GetValue(ok.Value)!;
        Assert.Equal(1024, randomValue.Length);
    }

    [Fact]
    public void GetRandomString_ReturnsDifferentStrings()
    {
        var result1 = _controller.GetRandomString(32);
        var result2 = _controller.GetRandomString(32);

        var ok1 = Assert.IsType<OkObjectResult>(result1.Result);
        var ok2 = Assert.IsType<OkObjectResult>(result2.Result);

        var random1 = (string)ok1.Value!.GetType().GetProperty("random")!.GetValue(ok1.Value)!;
        var random2 = (string)ok2.Value!.GetType().GetProperty("random")!.GetValue(ok2.Value)!;

        Assert.NotEqual(random1, random2);
    }

    #endregion

    #region GetDeviceId Tests

    [Fact]
    public void GetDeviceId_ReturnsOwnDeviceId_WhenNoIdProvided()
    {
        var result = _controller.GetDeviceId(null);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var idProp = ok.Value!.GetType().GetProperty("id");
        Assert.NotNull(idProp);
        var idValue = (string)idProp.GetValue(ok.Value)!;
        Assert.Equal("TEST-DEVICE-ID", idValue);
    }

    [Fact]
    public void GetDeviceId_ReturnsNormalizedId_WhenIdProvided()
    {
        var result = _controller.GetDeviceId("ABCDEFG");

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);
    }

    [Fact]
    public void GetDeviceId_ReturnsBadRequest_WhenIdTooShort()
    {
        var result = _controller.GetDeviceId("ABC");

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    #endregion

    #region GetShutdownTime Tests

    [Fact]
    public void GetShutdownTime_ReturnsShutdownInfo()
    {
        var result = _controller.GetShutdownTime();

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotNull(ok.Value);

        var shutdownRequestedProp = ok.Value.GetType().GetProperty("shutdownRequested");
        Assert.NotNull(shutdownRequestedProp);
        var shutdownRequested = (bool)shutdownRequestedProp.GetValue(ok.Value)!;
        Assert.False(shutdownRequested);
    }

    #endregion
}
