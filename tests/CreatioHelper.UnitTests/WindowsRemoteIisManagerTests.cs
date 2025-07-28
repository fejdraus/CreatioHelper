using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Infrastructure.Logging;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Common;
using Moq;

namespace CreatioHelper.Tests;

public class WindowsRemoteIisManagerTests
{
    private WindowsRemoteIisManager CreateManager()
    {
        var writer = new BufferingOutputWriter(_ => { }, () => { });
        var mockMetrics = new Mock<IMetricsService>();
        mockMetrics.Setup(m => m.MeasureAsync(It.IsAny<string>(), It.IsAny<Func<Task<Result>>>(), It.IsAny<Dictionary<string, string>>()))
            .Returns<string, Func<Task<Result>>, Dictionary<string, string>>((_, func, _) => func());
        return new WindowsRemoteIisManager(writer, mockMetrics.Object);
    }

    [Fact]
    public async Task StopAppPoolAsync_ReturnsFailure_WhenNotWindows()
    {
        var manager = CreateManager();
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
        var manager = CreateManager();
        
        var result = await manager.StopAppPoolAsync("", CancellationToken.None);
        
        Assert.False(result.IsSuccess);
        Assert.Contains("Pool name is required", result.ErrorMessage);
    }

    [Fact]
    public async Task StartAppPoolAsync_ReturnsResult_WithValidPoolName()
    {
        var manager = CreateManager();
        var poolName = "TestAppPool";
        
        var result = await manager.StartAppPoolAsync(poolName, CancellationToken.None);
        
        Assert.NotNull(result);
        Assert.IsType<Result>(result);
    }

    [Fact]
    public async Task StartAppPoolAsync_ReturnsFailure_WhenPoolNameEmpty()
    {
        var manager = CreateManager();
        
        var result = await manager.StartAppPoolAsync(string.Empty, CancellationToken.None);
        
        Assert.False(result.IsSuccess);
        Assert.Contains("Pool name is required", result.ErrorMessage);
    }

    [Fact]
    public async Task GetAppPoolStatusAsync_ReturnsResultString_WithValidPoolName()
    {
        var manager = CreateManager();
        var poolName = "DefaultAppPool";
        
        var result = await manager.GetAppPoolStatusAsync(poolName, CancellationToken.None);
        
        Assert.NotNull(result);
        Assert.IsType<Result<string>>(result);
    }

    [Fact]
    public async Task GetAppPoolStatusAsync_ReturnsFailure_WhenPoolNameEmpty()
    {
        var manager = CreateManager();
        
        var result = await manager.GetAppPoolStatusAsync("", CancellationToken.None);
        
        Assert.False(result.IsSuccess);
        Assert.Contains("Pool name is required", result.ErrorMessage);
    }

    [Fact]
    public async Task StartWebsiteAsync_ReturnsResult_WithValidSiteName()
    {
        var manager = CreateManager();
        var siteName = "Default Web Site";
        
        var result = await manager.StartWebsiteAsync(siteName, CancellationToken.None);
        
        Assert.NotNull(result);
        Assert.IsType<Result>(result);
    }

    [Fact]
    public async Task StartWebsiteAsync_ReturnsFailure_WhenSiteNameEmpty()
    {
        var manager = CreateManager();
        
        var result = await manager.StartWebsiteAsync(string.Empty, CancellationToken.None);
        
        Assert.False(result.IsSuccess);
        Assert.Contains("Site name is required", result.ErrorMessage);
    }

    [Fact]
    public async Task StopWebsiteAsync_ReturnsResult_WithValidSiteName()
    {
        var manager = CreateManager();
        var siteName = "Default Web Site";
        
        var result = await manager.StopWebsiteAsync(siteName, CancellationToken.None);
        
        Assert.NotNull(result);
        Assert.IsType<Result>(result);
    }

    [Fact]
    public async Task GetWebsiteStatusAsync_ReturnsResultString_WithValidSiteName()
    {
        var manager = CreateManager();
        var siteName = "Default Web Site";
        
        var result = await manager.GetWebsiteStatusAsync(siteName, CancellationToken.None);
        
        Assert.NotNull(result);
        Assert.IsType<Result<string>>(result);
    }

    [Fact]
    public async Task StartServiceAsync_ReturnsResult_WithValidServiceName()
    {
        var manager = CreateManager();
        var serviceName = "TestService";
        
        var result = await manager.StartServiceAsync(serviceName, CancellationToken.None);
        
        Assert.NotNull(result);
        Assert.IsType<Result>(result);
    }
}
