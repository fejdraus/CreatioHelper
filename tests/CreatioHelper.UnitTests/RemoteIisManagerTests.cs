using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Infrastructure.Logging;

namespace CreatioHelper.Tests;

public class RemoteIisManagerTests
{
    [Fact]
    public async Task StopAppPoolAsync_NonWindows_ReturnsFalse()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // test relevant only when not Windows
        }
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new RemoteIisManager(writer);
        var server = new ServerInfo { Name = "srv", PoolName = "pool" };
        var result = await manager.StopAppPoolAsync(server);
        Assert.False(result);
    }

    [Fact]
    public async Task StartAppPoolAsync_Throws_WhenPoolNameMissing()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new RemoteIisManager(writer);
        var server = new ServerInfo { Name = "srv" };
        await Assert.ThrowsAsync<ArgumentNullException>(() => manager.StartAppPoolAsync(server));
    }

    [Fact]
    public async Task StartServiceAsync_ReturnsFalse_WhenServiceNameMissing()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new RemoteIisManager(writer);
        var server = new ServerInfo { Name = "srv" };
        var result = await manager.StartServiceAsync(server);
        Assert.False(result);
    }
}
