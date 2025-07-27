using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services;

public class ServerStatusService : IServerStatusService
{
    private readonly IRemoteIisManager _remoteIisManager;
    private readonly ICacheService _cache;
    private readonly IMetricsService _metrics;
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromSeconds(30);

    public ServerStatusService(
        IRemoteIisManager remoteIisManager,
        ICacheService cache,
        IMetricsService metrics)
    {
        _remoteIisManager = remoteIisManager ?? throw new ArgumentNullException(nameof(remoteIisManager));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    public async Task<ServerInfo> RefreshServerStatusAsync(ServerInfo server, CancellationToken cancellationToken = default)
    {
        if (server == null) throw new ArgumentNullException(nameof(server));

        return await _metrics.MeasureAsync("server_status_refresh", async () =>
        {
            server.IsStatusLoading = true;

            try
            {
                // Обновляем статус пула с кэшированием
                if (!string.IsNullOrEmpty(server.PoolName))
                {
                    var poolStatus = await _cache.GetOrSetAsync(
                        $"pool_status_{server.PoolName}",
                        () => GetPoolStatusAsync(server.PoolName, cancellationToken),
                        CacheExpiration,
                        cancellationToken);

                    server.PoolStatus = poolStatus ?? "Unknown";
                }

                // Обновляем статус сайта с кэшированием
                if (!string.IsNullOrEmpty(server.SiteName))
                {
                    var siteStatus = await _cache.GetOrSetAsync(
                        $"site_status_{server.SiteName}",
                        () => GetWebsiteStatusAsync(server.SiteName, cancellationToken),
                        CacheExpiration,
                        cancellationToken);

                    server.SiteStatus = siteStatus ?? "Unknown";
                }

                // Обновляем статус сервиса с кэшированием
                if (!string.IsNullOrEmpty(server.ServiceName))
                {
                    var serviceStatus = await _cache.GetOrSetAsync(
                        $"service_status_{server.ServiceName}",
                        () => GetServiceStatusAsync(server.ServiceName, cancellationToken),
                        CacheExpiration,
                        cancellationToken);

                    server.ServiceStatus = serviceStatus ?? "Unknown";
                }

                _metrics.IncrementCounter("server_status_refresh_success", new() 
                { 
                    ["server"] = server.UniqueKey 
                });

                return server;
            }
            catch (OperationCanceledException)
            {
                _metrics.IncrementCounter("server_status_refresh_cancelled", new() 
                { 
                    ["server"] = server.UniqueKey 
                });
                throw;
            }
            catch (Exception ex)
            {
                _metrics.IncrementCounter("server_status_refresh_error", new() 
                { 
                    ["server"] = server.UniqueKey,
                    ["error_type"] = ex.GetType().Name
                });
                
                // При ошибке устанавливаем статусы как неизвестные
                server.PoolStatus = "Error";
                server.SiteStatus = "Error";
                server.ServiceStatus = "Error";
                
                return server;
            }
            finally
            {
                server.IsStatusLoading = false;
            }
        }, new() { ["server"] = server.UniqueKey });
    }

    public async Task RefreshMultipleServerStatusAsync(ServerInfo[] servers, CancellationToken cancellationToken = default)
    {
        if (servers == null) throw new ArgumentNullException(nameof(servers));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var tasks = new Task[servers.Length];
            for (int i = 0; i < servers.Length; i++)
            {
                tasks[i] = RefreshServerStatusAsync(servers[i], cancellationToken);
            }
            
            await Task.WhenAll(tasks);
            
            stopwatch.Stop();
            _metrics.RecordDuration("server_status_refresh_multiple", stopwatch.Elapsed, new() { ["server_count"] = servers.Length.ToString() });
            _metrics.IncrementCounter("server_status_refresh_multiple_success", new() { ["server_count"] = servers.Length.ToString() });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.IncrementCounter("server_status_refresh_multiple_error", new() 
            { 
                ["server_count"] = servers.Length.ToString(),
                ["error_type"] = ex.GetType().Name
            });
            throw;
        }
    }

    public async Task ClearServerStatusCacheAsync(ServerInfo server, CancellationToken cancellationToken = default)
    {
        if (server == null) throw new ArgumentNullException(nameof(server));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var tasks = new List<Task>();

            if (!string.IsNullOrEmpty(server.PoolName))
            {
                tasks.Add(_cache.RemoveAsync($"pool_status_{server.PoolName}", cancellationToken));
            }

            if (!string.IsNullOrEmpty(server.SiteName))
            {
                tasks.Add(_cache.RemoveAsync($"site_status_{server.SiteName}", cancellationToken));
            }

            if (!string.IsNullOrEmpty(server.ServiceName))
            {
                tasks.Add(_cache.RemoveAsync($"service_status_{server.ServiceName}", cancellationToken));
            }

            await Task.WhenAll(tasks);

            stopwatch.Stop();
            _metrics.RecordDuration("server_status_cache_clear", stopwatch.Elapsed, new() { ["server"] = server.UniqueKey });
            _metrics.IncrementCounter("server_status_cache_cleared", new()
            {
                ["server"] = server.UniqueKey
            });
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.IncrementCounter("server_status_cache_clear_error", new()
            {
                ["server"] = server.UniqueKey,
                ["error_type"] = ex.GetType().Name
            });
            throw;
        }
    }

    private async Task<string?> GetPoolStatusAsync(string poolName, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _remoteIisManager.GetAppPoolStatusAsync(poolName, cancellationToken);
            return result.IsSuccess ? result.Value : "Unknown";
        }
        catch
        {
            return "Error";
        }
    }

    private async Task<string?> GetWebsiteStatusAsync(string siteName, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _remoteIisManager.GetWebsiteStatusAsync(siteName, cancellationToken);
            return result.IsSuccess ? result.Value : "Unknown";
        }
        catch
        {
            return "Error";
        }
    }

    private async Task<string?> GetServiceStatusAsync(string serviceName, CancellationToken cancellationToken)
    {
        try
        {
            // Предполагаем, что есть метод для получения статуса сервиса
            // Если его нет, можно добавить в IRemoteIisManager
            return "Unknown"; // Заглушка
        }
        catch
        {
            return "Error";
        }
    }
}
