using System.Text.Json;
using CreatioHelper.Agent.Services;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Enums;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace CreatioHelper.Agent.Tests;

public class WebSiteRegistryOverrideTests : IDisposable
{
    private readonly string _root;

    public WebSiteRegistryOverrideTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "chtest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    private WebSiteRegistryService NewService()
    {
        var env = new Mock<IWebHostEnvironment>();
        env.SetupGet(e => e.ContentRootPath).Returns(_root);
        return new WebSiteRegistryService(
            NullLogger<WebSiteRegistryService>.Instance,
            new WebServerAccessStatus(),
            env.Object);
    }

    private WebSiteRegistry ReadRegistry()
    {
        var json = File.ReadAllText(Path.Combine(_root, "website-registry.json"));
        return JsonSerializer.Deserialize<WebSiteRegistry>(json)!;
    }

    [Fact]
    public async Task SetWebServerType_PersistsOverride()
    {
        var service = NewService();
        await service.RegisterWebSiteAsync("SiteX", "WindowsService", "SvcX");

        await service.SetWebServerTypeAsync("SiteX", WebServerKind.Service);

        var registry = ReadRegistry();
        Assert.True(registry.WebServerTypeOverrides.TryGetValue("SiteX", out var kind));
        Assert.Equal(WebServerKind.Service, kind);
    }

    [Fact]
    public async Task SetWebServerType_Auto_RemovesOverride()
    {
        var service = NewService();
        await service.RegisterWebSiteAsync("SiteX", "WindowsService", "SvcX");
        await service.SetWebServerTypeAsync("SiteX", WebServerKind.Iis);

        await service.SetWebServerTypeAsync("SiteX", WebServerKind.Auto);

        Assert.DoesNotContain("SiteX", ReadRegistry().WebServerTypeOverrides.Keys);
    }

    [Fact]
    public async Task Unregister_RemovesSiteAndOverride()
    {
        var service = NewService();
        await service.RegisterWebSiteAsync("SiteX", "WindowsService", "SvcX");
        await service.SetWebServerTypeAsync("SiteX", WebServerKind.Service);

        await service.UnregisterWebSiteAsync("SiteX");

        var registry = ReadRegistry();
        Assert.DoesNotContain("SiteX", registry.WebServerTypeOverrides.Keys);
        Assert.DoesNotContain(registry.Sites, s => s.Name == "SiteX");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }
}
