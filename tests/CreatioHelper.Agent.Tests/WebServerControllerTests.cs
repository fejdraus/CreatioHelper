using System.Collections.Generic;
using System.Threading.Tasks;
using CreatioHelper.Agent.Controllers;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Enums;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Contracts.Requests;
using SetWebServerTypeRequestDto = CreatioHelper.Contracts.Requests.SetWebServerTypeRequest;
using Microsoft.AspNetCore.Mvc;
using WebServerResultDto = CreatioHelper.Contracts.Responses.WebServerResult;
using WebServerStatusDto = CreatioHelper.Contracts.Responses.WebServerStatus;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.Agent.Tests;

public class WebServerControllerTests
{
    private static WebServerController CreateController(string supportedType,
        List<string>? availableTypes = null,
        List<WebServerStatus>? sites = null,
        List<WebServerStatus>? appPools = null)
    {
        availableTypes ??= new List<string> { supportedType };
        sites ??= new List<WebServerStatus>();
        appPools ??= new List<WebServerStatus>();

        var factoryMock = new Mock<IWebServerServiceFactory>();
        factoryMock.Setup(f => f.GetSupportedWebServerTypeAsync())
            .ReturnsAsync(supportedType);
        factoryMock.Setup(f => f.GetAvailableWebServerTypes())
            .Returns(availableTypes);
        factoryMock.Setup(f => f.IsWebServerSupported()).Returns(true);

        var serviceMock = new Mock<IWebServerService>();
        serviceMock.Setup(s => s.GetAllSitesAsync()).ReturnsAsync(sites);
        serviceMock.Setup(s => s.GetAllAppPoolsAsync()).ReturnsAsync(appPools);
        factoryMock.Setup(f => f.CreateWebServerServiceAsync()).ReturnsAsync(serviceMock.Object);

        var platformMock = new Mock<IPlatformService>();
        platformMock.Setup(p => p.GetPlatform()).Returns(PlatformType.Windows);

        var loggerMock = new Mock<ILogger<WebServerController>>();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        return new WebServerController(factoryMock.Object, platformMock.Object, loggerMock.Object, configuration);
    }

    [Fact]
    public async Task SetWebServerType_ReturnsStringCurrentType()
    {
        var controller = CreateController("IIS");
        var request = new SetWebServerTypeRequestDto { Type = "IIS" };

        var result = await controller.SetWebServerType(request);

        var ok = Assert.IsType<OkObjectResult>(result);
        var currentType = ok.Value!.GetType().GetProperty("CurrentType")!.GetValue(ok.Value);
        Assert.Equal("IIS", currentType);
    }

    [Fact]
    public async Task GetIisOverview_ReturnsStringPlatform()
    {
        var sites = new List<WebServerStatus> { new WebServerStatus { Name = "Default", Status = "Running", IsRunning = true, Port = "80" } };
        var pools = new List<WebServerStatus> { new WebServerStatus { Name = "DefaultAppPool", Status = "Running", IsRunning = true } };
        var controller = CreateController("IIS", sites: sites, appPools: pools);

        var result = await controller.GetIisOverview();

        var ok = Assert.IsType<OkObjectResult>(result);
        var platform = ok.Value!.GetType().GetProperty("Platform")!.GetValue(ok.Value);
        Assert.Equal("Windows/IIS", platform);
    }
    private static WebServerController CreateControllerWithService(Mock<IWebServerService> serviceMock, string supportedType = "IIS")
    {
        var factoryMock = new Mock<IWebServerServiceFactory>();
        factoryMock.Setup(f => f.GetSupportedWebServerTypeAsync()).ReturnsAsync(supportedType);
        factoryMock.Setup(f => f.GetAvailableWebServerTypes()).Returns(new List<string> { supportedType });
        factoryMock.Setup(f => f.IsWebServerSupported()).Returns(true);
        factoryMock.Setup(f => f.CreateWebServerServiceAsync()).ReturnsAsync(serviceMock.Object);

        var platformMock = new Mock<IPlatformService>();
        platformMock.Setup(p => p.GetPlatform()).Returns(PlatformType.Windows);

        var loggerMock = new Mock<ILogger<WebServerController>>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        return new WebServerController(factoryMock.Object, platformMock.Object, loggerMock.Object, configuration);
    }

    [Fact]
    public async Task StopSite_ReturnsOk_WhenSuccess()
    {
        var serviceMock = new Mock<IWebServerService>();
        serviceMock.Setup(s => s.StopSiteAsync("site"))
            .ReturnsAsync(new WebServerResult { Success = true, Message = "ok", Data = new Data { Status = "Stopped" } });
        var controller = CreateControllerWithService(serviceMock);

        var result = await controller.StopSite("site");

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsType<WebServerResultDto>(ok.Value);
        Assert.True(dto.Success);
        Assert.Equal("Stopped", dto.Data!.Status);
    }

    [Fact]
    public async Task StopSite_ReturnsBadRequest_WhenFailure()
    {
        var serviceMock = new Mock<IWebServerService>();
        serviceMock.Setup(s => s.StopSiteAsync("site"))
            .ReturnsAsync(new WebServerResult { Success = false, Message = "err" });
        var controller = CreateControllerWithService(serviceMock);

        var result = await controller.StopSite("site");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task GetAllSites_ReturnsMappedDtos()
    {
        var serviceMock = new Mock<IWebServerService>();
        serviceMock.Setup(s => s.GetAllSitesAsync())
            .ReturnsAsync(new List<WebServerStatus>
            {
                new WebServerStatus { Name = "Default", Status = "Running", IsRunning = true, Type = "IIS", Port = "80" }
            });
        var controller = CreateControllerWithService(serviceMock);

        var result = await controller.GetAllSites();

        var ok = Assert.IsType<OkObjectResult>(result);
        var dtoList = Assert.IsAssignableFrom<IEnumerable<WebServerStatusDto>>(ok.Value);
        var dto = Assert.Single(dtoList);
        Assert.Equal("Default", dto.Name);
        Assert.Equal("80", dto.Port);
    }
}
