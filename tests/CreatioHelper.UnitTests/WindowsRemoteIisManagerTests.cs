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
        
        // На не-Windows платформах должен возвращать ошибку
        var result = await manager.StopAppPoolAsync(serverId, CancellationToken.None);
        
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(result.IsSuccess);
            Assert.Contains("Windows", result.ErrorMessage);
        }
    }

    [Fact]
    public async Task StartAppPoolAsync_ReturnsResult_WithValidServerId()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var manager = new WindowsRemoteIisManager(writer);
        var serverId = ServerId.Create();
        
        var result = await manager.StartAppPoolAsync(serverId, CancellationToken.None);
        
        // Результат должен быть типа Result
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
        
        // Результат должен быть типа Result<string>
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
        
        // Результат должен быть типа Result
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
        cts.Cancel(); // Отменяем сразу
        
        var result = await manager.StartAppPoolAsync(serverId, cts.Token);
        
        // При отмене должен возвращаться Result с ошибкой
        Assert.False(result.IsSuccess);
        Assert.Contains("cancelled", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }
}
