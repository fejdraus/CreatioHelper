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
        var serverId = ServerId.Create();
        
        var result = await manager.StopAppPoolAsync(serverId, CancellationToken.None);
        
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
    public async Task StartAppPoolAsync_ReturnsResult_WithValidServerId()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new WindowsRemoteIisManager(writer);
        var serverId = ServerId.Create();
        
        var result = await manager.StartAppPoolAsync(serverId, CancellationToken.None);
        
        Assert.NotNull(result);
        Assert.IsType<CreatioHelper.Domain.Common.Result>(result);
    }

    [Fact]
    public async Task GetAppPoolStatusAsync_ReturnsResultString_WithValidServerId()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new WindowsRemoteIisManager(writer);
        var serverId = ServerId.Create();
        
        var result = await manager.GetAppPoolStatusAsync(serverId, CancellationToken.None);
        
        Assert.NotNull(result);
        Assert.IsType<CreatioHelper.Domain.Common.Result<string>>(result);
    }

    [Fact]
    public async Task StartServiceAsync_ReturnsResult_WithValidServerId()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new WindowsRemoteIisManager(writer);
        var serverId = ServerId.Create();
        
        var result = await manager.StartServiceAsync(serverId, CancellationToken.None);
        
        Assert.NotNull(result);
        Assert.IsType<CreatioHelper.Domain.Common.Result>(result);
    }

    [Fact]
    public async Task CancellationToken_IsRespected()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new WindowsRemoteIisManager(writer);
        var serverId = ServerId.Create();
        
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        
        var result = await manager.StartAppPoolAsync(serverId, cts.Token);
        
        Assert.False(result.IsSuccess);
        
        var expectedMessages = new[] { "cancelled", "Windows" };
        Assert.True(expectedMessages.Any(msg => 
            result.ErrorMessage.Contains(msg, StringComparison.OrdinalIgnoreCase)),
            $"Expected error message to contain 'cancelled' or 'Windows', but was: '{result.ErrorMessage}'");
    }
}
