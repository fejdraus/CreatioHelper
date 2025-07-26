#if WINDOWS
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Site;
using CreatioHelper.Infrastructure.Logging;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Application.Interfaces;
using Moq;
namespace CreatioHelper.Tests;

public class SiteSynchronizerTests
{
    [Fact]
    public async Task SynchronizeAsync_Throws_When_SitePathNull()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var remote = new Mock<IRemoteIisManager>();
        var copy = new Mock<IFileCopyHelper>();
        var status = new ServerStatusService(writer, remote.Object);
        var sync = new SiteSynchronizer(writer, remote.Object, copy.Object, status);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sync.SynchronizeAsync(null!, new List<ServerInfo>()));
    }

    [Fact]
    public async Task SynchronizeAsync_ReturnsFalse_When_StopFails()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var remote = new Mock<IRemoteIisManager>();
        remote.Setup(r => r.StopAppPoolAsync(It.IsAny<ServerInfo>())).ReturnsAsync(false);
        remote.Setup(r => r.StopWebsiteAsync(It.IsAny<ServerInfo>())).ReturnsAsync(true);
        remote.Setup(r => r.StartAppPoolAsync(It.IsAny<ServerInfo>())).ReturnsAsync(true);
        remote.Setup(r => r.StartWebsiteAsync(It.IsAny<ServerInfo>())).ReturnsAsync(true);
        remote.Setup(r => r.GetAppPoolStatusAsync(It.IsAny<ServerInfo>())).Returns(Task.CompletedTask);
        remote.Setup(r => r.GetWebsiteStatusAsync(It.IsAny<ServerInfo>())).Returns(Task.CompletedTask);

        var copy = new Mock<IFileCopyHelper>();
        var server = new ServerInfo { Name = "srv", NetworkPath = "\\srv" , PoolName = "p", SiteName = "s"};
        var status = new ServerStatusService(writer, remote.Object);
        var sync = new SiteSynchronizer(writer, remote.Object, copy.Object, status);

        var result = await sync.SynchronizeAsync("c:/site", new List<ServerInfo> { server });

        Assert.False(result);
    }

    [Fact]
    public async Task SynchronizeAsync_ReturnsTrue_When_AllOperationsSucceed()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Test relies on Windows-specific behavior
        }
        var writer = new BufferingOutputWriter(_ => { });
        var remote = new Mock<IRemoteIisManager>();
        remote.Setup(r => r.StopAppPoolAsync(It.IsAny<ServerInfo>())).ReturnsAsync(true);
        remote.Setup(r => r.StopWebsiteAsync(It.IsAny<ServerInfo>())).ReturnsAsync(true);
        remote.Setup(r => r.StartAppPoolAsync(It.IsAny<ServerInfo>())).ReturnsAsync(true);
        remote.Setup(r => r.StartWebsiteAsync(It.IsAny<ServerInfo>())).ReturnsAsync(true);

        int statusCall = 0;
        remote.Setup(r => r.GetAppPoolStatusAsync(It.IsAny<ServerInfo>())).Returns<ServerInfo>(s =>
        {
            s.PoolStatus = statusCall < 2 ? "Stopped" : "Started";
            statusCall++;
            return Task.CompletedTask;
        });
        remote.Setup(r => r.GetWebsiteStatusAsync(It.IsAny<ServerInfo>())).Returns<ServerInfo>(s =>
        {
            s.SiteStatus = statusCall < 4 ? "Stopped" : "Started";
            statusCall++;
            return Task.CompletedTask;
        });

        var copy = new Mock<IFileCopyHelper>();
        copy.Setup(c => c.CopyAsync(It.IsAny<ServerInfo>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);

        var server = new ServerInfo { Name = "srv", NetworkPath = "\\srv" , PoolName = "p", SiteName = "s"};
        var status = new ServerStatusService(writer, remote.Object);
        var sync = new SiteSynchronizer(writer, remote.Object, copy.Object, status);

        var result = await sync.SynchronizeAsync("c:/site", new List<ServerInfo> { server });

        Assert.True(result);
    }
}
#endif
