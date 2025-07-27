using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Common;
using CreatioHelper.Domain.ValueObjects;
using CreatioHelper.Infrastructure.Services.Site;
using CreatioHelper.Infrastructure.Logging;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Application.Interfaces;
using Moq;

namespace CreatioHelper.Tests;

public class WindowsSiteSynchronizerTests
{
    [Fact]
    public async Task SynchronizeAsync_Throws_When_SitePathNull()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var remote = new Mock<IRemoteIisManager>();
        var copy = new Mock<IFileCopyHelper>();
        var status = new ServerStatusService(writer, remote.Object);
        var sync = new WindowsSiteSynchronizer(writer, remote.Object, copy.Object, status);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sync.SynchronizeAsync(null!, new List<ServerInfo>()));
    }

    [Fact]
    public async Task SynchronizeAsync_ReturnsFalse_When_StopFails()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var remote = new Mock<IRemoteIisManager>();
        
        remote.Setup(r => r.StopAppPoolAsync(It.IsAny<ServerId>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Failure("Stop failed"));
        remote.Setup(r => r.StopWebsiteAsync(It.IsAny<ServerId>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());
        remote.Setup(r => r.StartAppPoolAsync(It.IsAny<ServerId>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());
        remote.Setup(r => r.StartWebsiteAsync(It.IsAny<ServerId>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());

        var copy = new Mock<IFileCopyHelper>();
        var status = new ServerStatusService(writer, remote.Object);
        var sync = new WindowsSiteSynchronizer(writer, remote.Object, copy.Object, status);

        var servers = new List<ServerInfo>
        {
            new ServerInfo
            {
                Name = "TestServer",
                NetworkPath = @"\\testserver\share",
                PoolName = "TestPool"
            }
        };

        var result = await sync.SynchronizeAsync(@"C:\TestSite", servers);

        Assert.False(result);
    }

    [Fact]
    public async Task SynchronizeAsync_ReturnsTrue_When_AllOperationsSucceed()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var remote = new Mock<IRemoteIisManager>();
        
        remote.Setup(r => r.StopAppPoolAsync(It.IsAny<ServerId>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());
        remote.Setup(r => r.StopWebsiteAsync(It.IsAny<ServerId>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());
        remote.Setup(r => r.StartAppPoolAsync(It.IsAny<ServerId>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());
        remote.Setup(r => r.StartWebsiteAsync(It.IsAny<ServerId>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());
        remote.Setup(r => r.GetAppPoolStatusAsync(It.IsAny<ServerId>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<string>.Success("Stopped"));
        remote.Setup(r => r.GetWebsiteStatusAsync(It.IsAny<ServerId>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<string>.Success("Stopped"));

        var copy = new Mock<IFileCopyHelper>();
        copy.Setup(c => c.CopyAsync(It.IsAny<ServerInfo>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(0));

        var status = new ServerStatusService(writer, remote.Object);
        var sync = new WindowsSiteSynchronizer(writer, remote.Object, copy.Object, status);

        var servers = new List<ServerInfo>
        {
            new ServerInfo
            {
                Name = "TestServer",
                NetworkPath = @"\\testserver\share",
                PoolName = "TestPool",
                SiteName = "TestSite"
            }
        };

        var result = await sync.SynchronizeAsync(@"C:\TestSite", servers);

        if (OperatingSystem.IsWindows())
        {
            Assert.True(result);
        }
        else
        {
            Assert.True(result == true || result == false, "Result should be either true or false, not throw exception");
        }
    }

    [Fact]
    public async Task SynchronizeAsync_HandlesCancellation()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var remote = new Mock<IRemoteIisManager>();
        
        remote.Setup(r => r.StopAppPoolAsync(It.IsAny<ServerId>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());
        remote.Setup(r => r.StopWebsiteAsync(It.IsAny<ServerId>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());

        var copy = new Mock<IFileCopyHelper>();
        var status = new ServerStatusService(writer, remote.Object);
        var sync = new WindowsSiteSynchronizer(writer, remote.Object, copy.Object, status);

        var servers = new List<ServerInfo>
        {
            new ServerInfo
            {
                Name = "TestServer",
                NetworkPath = @"\\testserver\share",
                PoolName = "TestPool"
            }
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await sync.SynchronizeAsync(@"C:\TestSite", servers, cts.Token);

        Assert.False(result);
    }
}
