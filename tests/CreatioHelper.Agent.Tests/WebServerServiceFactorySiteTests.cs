using CreatioHelper.Agent.Services;
using CreatioHelper.Agent.Services.Windows;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace CreatioHelper.Agent.Tests;

public class WebServerServiceFactorySiteTests
{
    [Theory]
    [InlineData(WebServerKind.Auto, "IIS", WebServerKind.Iis)]
    [InlineData(WebServerKind.Auto, "Systemd", WebServerKind.Service)]
    [InlineData(WebServerKind.Auto, "WindowsService", WebServerKind.Service)]
    [InlineData(WebServerKind.Iis, "Systemd", WebServerKind.Iis)]
    [InlineData(WebServerKind.Service, "IIS", WebServerKind.Service)]
    public void EffectiveKind_ResolvesFromTypeAndOverride(WebServerKind declared, string type, WebServerKind expected)
    {
        var site = new WebSiteInfo { WebServerType = declared, Type = type };
        Assert.Equal(expected, site.EffectiveKind);
    }

    [Fact]
    public async Task CreateForSite_IisSite_ReturnsIisManager()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var factory = BuildWindowsFactory();
        var service = await factory.CreateWebServerServiceForSiteAsync(
            new WebSiteInfo { WebServerType = WebServerKind.Iis, Type = "IIS" });

        Assert.IsType<IisManagerService>(service);
    }

    [Fact]
    public async Task CreateForSite_ServiceSite_ReturnsWindowsServiceManager()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var factory = BuildWindowsFactory();
        var service = await factory.CreateWebServerServiceForSiteAsync(
            new WebSiteInfo { WebServerType = WebServerKind.Service, Type = "IIS" });

        Assert.IsType<WindowsServiceManager>(service);
    }

    private static WebServerServiceFactory BuildWindowsFactory()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<WebServerAccessStatus>();
        services.AddTransient<IisManagerService>();
        services.AddTransient<WindowsServiceManager>();
        var provider = services.BuildServiceProvider();

        var platform = new Mock<IPlatformService>();
        platform.Setup(p => p.IsFeatureSupported(FeatureNames.IisManagement)).Returns(true);
        var configuration = new Mock<IConfigurationService>();

        return new WebServerServiceFactory(provider, platform.Object, configuration.Object);
    }
}
