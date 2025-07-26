using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Infrastructure.Logging;

namespace CreatioHelper.Tests;

public class WindowsRemoteIisManagerTests
{
    [Fact]
    public async Task StartAppPoolAsync_Throws_WhenPoolNameMissing()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new WindowsRemoteIisManager(writer);
        var server = new ServerInfo { Name = "srv" };
        await Assert.ThrowsAsync<ArgumentNullException>(() => manager.StartAppPoolAsync(server));
    }

    [Fact]
    public async Task StartServiceAsync_ReturnsFalse_WhenServiceNameMissing()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new WindowsRemoteIisManager(writer);
        var server = new ServerInfo { Name = "srv" };
        var result = await manager.StartServiceAsync(server);
        Assert.False(result);
    }
}
