using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Common;
using CreatioHelper.Domain.ValueObjects;
using CreatioHelper.Infrastructure.Services.Site;
using CreatioHelper.Infrastructure.Logging;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.Tests;

public class WindowsSiteSynchronizerTests
{
    [Fact]
    public async Task SynchronizeAsync_ThrowsArgumentNullException_When_SiteInfoIsNull()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var remote = new Mock<IRemoteIisManager>();
        var copy = new Mock<IFileCopyHelper>();
        var metrics = new Mock<IMetricsService>();
        var logger = new Mock<ILogger<ServerStatusService>>();
        var status = new ServerStatusService(remote.Object, metrics.Object, logger.Object);
        var sync = new WindowsSiteSynchronizer(writer, remote.Object, copy.Object, status);

        await Assert.ThrowsAsync<ArgumentNullException>(() => sync.SynchronizeAsync(null!, new List<ServerInfo>()));
    }

    [Fact]
    public async Task SynchronizeAsync_ReturnsFalse_When_StopFails()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var remote = new Mock<IRemoteIisManager>();
        
        // Настройка моков для новых методов с именами вместо ServerId
        remote.Setup(r => r.StopAppPoolAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Failure("Stop failed"));
        remote.Setup(r => r.StopWebsiteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());
        remote.Setup(r => r.StartAppPoolAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());
        remote.Setup(r => r.StartWebsiteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());

        var copy = new Mock<IFileCopyHelper>();
        var metrics = new Mock<IMetricsService>();
        
        // Настройка мока для метрик - убираем Task<Task>, используем ServerInfo
        metrics.Setup(m => m.MeasureAsync(It.IsAny<string>(), It.IsAny<Func<Task<ServerInfo>>>(), It.IsAny<Dictionary<string, string>>()))
              .Returns((string _, Func<Task<ServerInfo>> operation, Dictionary<string, string> _) => operation());
        
        var status = new ServerStatusService(remote.Object, metrics.Object, new Mock<ILogger<ServerStatusService>>().Object);
        var sync = new WindowsSiteSynchronizer(writer, remote.Object, copy.Object, status);

        var servers = new List<ServerInfo>
        {
            new ServerInfo
            {
                Name = new ServerName("TestServer"),
                NetworkPath = new NetworkPath(@"\\testserver\share"),
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
        
        // Настройка моков для новых методов с именами
        remote.Setup(r => r.StopAppPoolAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());
        remote.Setup(r => r.StopWebsiteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());
        remote.Setup(r => r.StartAppPoolAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());
        remote.Setup(r => r.StartWebsiteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());
        remote.Setup(r => r.GetAppPoolStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<string>.Success("Stopped"));
        remote.Setup(r => r.GetWebsiteStatusAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result<string>.Success("Stopped"));

        var copy = new Mock<IFileCopyHelper>();
        copy.Setup(c => c.CopyAsync(It.IsAny<ServerInfo>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult(0));

        var metrics = new Mock<IMetricsService>();
        
        // Настройка мока для метрик - явно указываем все параметры без опциональных
        metrics.Setup(m => m.MeasureAsync<ServerInfo>(It.IsAny<string>(), It.IsAny<Func<Task<ServerInfo>>>(), It.IsAny<Dictionary<string, string>>()))
              .Returns((string _, Func<Task<ServerInfo>> operation, Dictionary<string, string> _) => operation());

        metrics.Setup(m => m.MeasureAsync<object>(It.IsAny<string>(), It.IsAny<Func<Task<object>>>(), It.IsAny<Dictionary<string, string>>()))
              .Returns((string _, Func<Task<object>> operation, Dictionary<string, string> _) => operation());

        // Также добавляем настройку для версии без tags (когда tags = null)
        metrics.Setup(m => m.MeasureAsync<ServerInfo>(It.IsAny<string>(), It.IsAny<Func<Task<ServerInfo>>>(), null))
              .Returns((string _, Func<Task<ServerInfo>> operation, Dictionary<string, string> _) => operation());

        metrics.Setup(m => m.MeasureAsync<object>(It.IsAny<string>(), It.IsAny<Func<Task<object>>>(), null))
              .Returns((string _, Func<Task<object>> operation, Dictionary<string, string> _) => operation());

        var logger = new Mock<ILogger<ServerStatusService>>();
        var statusService = new Mock<IServerStatusService>();
        
        // Настройка мока для обновления статусов - правильно обновляем статусы серверов
        statusService.Setup(s => s.RefreshMultipleServerStatusAsync(It.IsAny<ServerInfo[]>(), It.IsAny<CancellationToken>()))
                    .Callback<ServerInfo[], CancellationToken>((servers, _) =>
                    {
                        // Устанавливаем статусы как "Stopped" для всех серверов
                        foreach (var server in servers)
                        {
                            server.PoolStatus = "Stopped";
                            server.SiteStatus = "Stopped";
                        }
                    })
                    .Returns(Task.CompletedTask);
                    
        statusService.Setup(s => s.RefreshServerStatusAsync(It.IsAny<ServerInfo>(), It.IsAny<CancellationToken>()))
                    .Callback<ServerInfo, CancellationToken>((server, _) => 
                    {
                        // Устанавливаем статусы как "Started" для проверки после запуска
                        server.PoolStatus = "Started";
                        server.SiteStatus = "Started";
                    })
                    .Returns((ServerInfo server, CancellationToken _) => Task.FromResult(server));

        var sync = new WindowsSiteSynchronizer(writer, remote.Object, copy.Object, statusService.Object);

        var servers = new List<ServerInfo>
        {
            new ServerInfo
            {
                Name = new ServerName("TestServer"),
                NetworkPath = new NetworkPath(@"\\testserver\share"),
                PoolName = "TestPool",
                SiteName = "TestSite"
            }
        };

        var result = await sync.SynchronizeAsync(@"C:\TestSite", servers);

        Assert.True(result);
    }

    [Fact]
    public async Task SynchronizeAsync_HandlesCancellation()
    {
        var writer = new BufferingOutputWriter(_ => { });
        var remote = new Mock<IRemoteIisManager>();
        
        remote.Setup(r => r.StopAppPoolAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());
        remote.Setup(r => r.StopWebsiteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(Result.Success());

        var copy = new Mock<IFileCopyHelper>();
        var metrics = new Mock<IMetricsService>();
        
        // Настройка мока для метрик - исправляем тип
        metrics.Setup(m => m.MeasureAsync(It.IsAny<string>(), It.IsAny<Func<Task<ServerInfo>>>(), It.IsAny<Dictionary<string, string>>()))
              .Returns((string _, Func<Task<ServerInfo>> operation, Dictionary<string, string> _) => operation());
        
        var status = new ServerStatusService(remote.Object, metrics.Object, new Mock<ILogger<ServerStatusService>>().Object);
        var sync = new WindowsSiteSynchronizer(writer, remote.Object, copy.Object, status);

        var servers = new List<ServerInfo>
        {
            new ServerInfo
            {
                Name = new ServerName("TestServer"),
                NetworkPath = new NetworkPath(@"\\testserver\share"),
                PoolName = "TestPool"
            }
        };

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await sync.SynchronizeAsync(@"C:\TestSite", servers, cts.Token);

        Assert.False(result);
    }
}
