using CreatioHelper.Domain.Entities;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.Infrastructure.Services
{
    public class ServerStatusService
    {
        private readonly IOutputWriter _output;
        private readonly IRemoteIisManager _remoteIisManager;
        private readonly ICacheService _cache;
        private readonly IMetricsService _metrics;
        private static readonly TimeSpan CacheExpiration = TimeSpan.FromSeconds(30);

        public ServerStatusService(
            IOutputWriter output, 
            IRemoteIisManager remoteIisManager,
            ICacheService cache,
            IMetricsService metrics)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
            _remoteIisManager = remoteIisManager ?? throw new ArgumentNullException(nameof(remoteIisManager));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        }
        
        public async Task RefreshServerStatusAsync(ServerInfo server)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));

            await _metrics.MeasureAsync("server_status_refresh", async () =>
            {
                // Check the platform before executing
                if (!OperatingSystem.IsWindows())
                {
                    server.PoolStatus = "Unsupported";
                    server.SiteStatus = "Unsupported";
                    _output.WriteLine("[ERROR] Status check is only available on Windows.");
                    return Task.CompletedTask;
                }

                server.IsStatusLoading = true;
                server.PoolStatus = "Checking...";
                server.SiteStatus = "Checking...";

                try
                {
                    // Проверяем статус пула только если указано имя пула
                    if (!string.IsNullOrWhiteSpace(server.PoolName))
                    {
                        var poolCacheKey = $"pool_status_{server.PoolName}";
                        var cachedPoolStatus = await _cache.GetAsync<string>(poolCacheKey);
                        
                        if (cachedPoolStatus != null)
                        {
                            server.PoolStatus = cachedPoolStatus;
                            _metrics.IncrementCounter("server_status_cache_hit", new() { ["type"] = "pool" });
                        }
                        else
                        {
                            var poolStatusResult = await _remoteIisManager.GetAppPoolStatusAsync(server.PoolName, CancellationToken.None);
                            if (poolStatusResult.IsSuccess)
                            {
                                server.PoolStatus = poolStatusResult.Value ?? "Unknown";
                                await _cache.SetAsync(poolCacheKey, server.PoolStatus, CacheExpiration);
                                _metrics.IncrementCounter("server_status_cache_miss", new() { ["type"] = "pool" });
                            }
                            else
                            {
                                server.PoolStatus = "Error";
                                _output.WriteLine($"[ERROR] Failed to get pool status for '{server.PoolName}': {poolStatusResult.ErrorMessage}");
                                _metrics.IncrementCounter("server_status_error", new() { ["type"] = "pool" });
                            }
                        }
                    }
                    else
                    {
                        server.PoolStatus = "Not configured";
                    }

                    // Проверяем статус сайта только если указано имя сайта
                    if (!string.IsNullOrWhiteSpace(server.SiteName))
                    {
                        var siteCacheKey = $"site_status_{server.SiteName}";
                        var cachedSiteStatus = await _cache.GetAsync<string>(siteCacheKey);
                        
                        if (cachedSiteStatus != null)
                        {
                            server.SiteStatus = cachedSiteStatus;
                            _metrics.IncrementCounter("server_status_cache_hit", new() { ["type"] = "site" });
                        }
                        else
                        {
                            var siteStatusResult = await _remoteIisManager.GetWebsiteStatusAsync(server.SiteName, CancellationToken.None);
                            if (siteStatusResult.IsSuccess)
                            {
                                server.SiteStatus = siteStatusResult.Value ?? "Unknown";
                                await _cache.SetAsync(siteCacheKey, server.SiteStatus, CacheExpiration);
                                _metrics.IncrementCounter("server_status_cache_miss", new() { ["type"] = "site" });
                            }
                            else
                            {
                                server.SiteStatus = "Error";
                                _output.WriteLine($"[ERROR] Failed to get site status for '{server.SiteName}': {siteStatusResult.ErrorMessage}");
                                _metrics.IncrementCounter("server_status_error", new() { ["type"] = "site" });
                            }
                        }
                    }
                    else
                    {
                        server.SiteStatus = "Not configured";
                    }
                }
                catch (Exception ex)
                {
                    server.PoolStatus = "Error";
                    server.SiteStatus = "Error";
                    _output.WriteLine($"[ERROR] Failed to get status for server '{server.Name?.Value ?? "Unknown"}': {ex.Message}");
                    _metrics.IncrementCounter("server_status_exception");
                }
                finally
                {
                    server.IsStatusLoading = false;
                }
                
                return Task.CompletedTask;
            }, new() { ["server"] = server.Name?.Value ?? "Unknown" });
        }
        
        public async Task RefreshMultipleServersStatusAsync(params ServerInfo[] servers)
        {
            await _metrics.MeasureAsync("multiple_servers_status_refresh", async () =>
            {
                var tasks = new Task[servers.Length];
                for (int i = 0; i < servers.Length; i++)
                {
                    tasks[i] = RefreshServerStatusAsync(servers[i]);
                }
                
                await Task.WhenAll(tasks);
                return Task.CompletedTask;
            }, new() { ["server_count"] = servers.Length.ToString() });
        }

        public async Task ClearServerStatusCacheAsync(ServerInfo server)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            
            var serverKey = server.UniqueKey;
            var poolCacheKey = $"pool_status_{serverKey}";
            var siteCacheKey = $"site_status_{serverKey}";
            
            await _cache.RemoveAsync(poolCacheKey);
            await _cache.RemoveAsync(siteCacheKey);
            
            _metrics.IncrementCounter("server_status_cache_cleared");
        }
    }
}