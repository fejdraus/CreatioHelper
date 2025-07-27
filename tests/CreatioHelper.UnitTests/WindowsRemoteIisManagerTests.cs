using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.ValueObjects;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Infrastructure.Logging;

namespace CreatioHelper.Tests;

public class WindowsRemoteIisManagerTests
{
    [Fact]
    public async Task StopAppPoolAsync_ReturnsFailure_WhenNotWindows()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new WindowsRemoteIisManager(writer);
        var poolName = "TestAppPool";
        
        var result = await manager.StopAppPoolAsync(poolName, CancellationToken.None);
        
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(result.IsSuccess);
            Assert.Contains("Windows", result.ErrorMessage);
        }
        else
        {
            Assert.NotNull(result);
        }
    }

    [Fact]
    public async Task StopAppPoolAsync_ReturnsFailure_WhenPoolNameEmpty()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new WindowsRemoteIisManager(writer);
        
        var result = await manager.StopAppPoolAsync("", CancellationToken.None);
        
        Assert.False(result.IsSuccess);
        Assert.Contains("Pool name is required", result.ErrorMessage);
    }

    [Fact]
    public async Task StartAppPoolAsync_ReturnsResult_WithValidPoolName()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new WindowsRemoteIisManager(writer);
        var poolName = "TestAppPool";
        
        var result = await manager.StartAppPoolAsync(poolName, CancellationToken.None);
        
        Assert.NotNull(result);
        Assert.IsType<CreatioHelper.Domain.Common.Result>(result);
    }

    [Fact]
    public async Task StartAppPoolAsync_ReturnsFailure_WhenPoolNameEmpty()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new WindowsRemoteIisManager(writer);
        
        var result = await manager.StartAppPoolAsync(null!, CancellationToken.None);
        
        Assert.False(result.IsSuccess);
        Assert.Contains("Pool name is required", result.ErrorMessage);
    }

    [Fact]
    public async Task GetAppPoolStatusAsync_ReturnsResultString_WithValidPoolName()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new WindowsRemoteIisManager(writer);
        var poolName = "DefaultAppPool";
        
        var result = await manager.GetAppPoolStatusAsync(poolName, CancellationToken.None);
        
        Assert.NotNull(result);
        Assert.IsType<CreatioHelper.Domain.Common.Result<string>>(result);
    }

    [Fact]
    public async Task GetAppPoolStatusAsync_ReturnsFailure_WhenPoolNameEmpty()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new WindowsRemoteIisManager(writer);
        
        var result = await manager.GetAppPoolStatusAsync("", CancellationToken.None);
        
        Assert.False(result.IsSuccess);
        Assert.Contains("Pool name is required", result.ErrorMessage);
    }

    [Fact]
    public async Task StartWebsiteAsync_ReturnsResult_WithValidSiteName()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new WindowsRemoteIisManager(writer);
        var siteName = "Default Web Site";
        
        var result = await manager.StartWebsiteAsync(siteName, CancellationToken.None);
        
        Assert.NotNull(result);
        Assert.IsType<CreatioHelper.Domain.Common.Result>(result);
    }

    [Fact]
    public async Task StartWebsiteAsync_ReturnsFailure_WhenSiteNameEmpty()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new WindowsRemoteIisManager(writer);
        
        var result = await manager.StartWebsiteAsync(null!, CancellationToken.None);
        
        Assert.False(result.IsSuccess);
        Assert.Contains("Site name is required", result.ErrorMessage);
    }

    [Fact]
    public async Task StopWebsiteAsync_ReturnsResult_WithValidSiteName()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new WindowsRemoteIisManager(writer);
        var siteName = "Default Web Site";
        
        var result = await manager.StopWebsiteAsync(siteName, CancellationToken.None);
        
        Assert.NotNull(result);
        Assert.IsType<CreatioHelper.Domain.Common.Result>(result);
    }

    [Fact]
    public async Task GetWebsiteStatusAsync_ReturnsResultString_WithValidSiteName()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new WindowsRemoteIisManager(writer);
        var siteName = "Default Web Site";
        
        var result = await manager.GetWebsiteStatusAsync(siteName, CancellationToken.None);
        
        Assert.NotNull(result);
        Assert.IsType<CreatioHelper.Domain.Common.Result<string>>(result);
    }

    [Fact]
    public async Task StartServiceAsync_ReturnsResult_WithValidServiceName()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new WindowsRemoteIisManager(writer);
        var serviceName = "TestService";
        
        var result = await manager.StartServiceAsync(serviceName, CancellationToken.None);
        
        Assert.NotNull(result);
        Assert.IsType<CreatioHelper.Domain.Common.Result>(result);
    }
}
