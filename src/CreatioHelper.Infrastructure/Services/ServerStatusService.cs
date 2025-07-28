using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services;

public class ServerStatusService : IServerStatusService
{
    private readonly IRemoteIisManager _remoteIisManager;
    private readonly IMetricsService _metrics;
    private readonly ILogger<ServerStatusService> _logger;

    public ServerStatusService(
        IRemoteIisManager remoteIisManager,
        IMetricsService metrics,
        ILogger<ServerStatusService> logger)
    {
        _remoteIisManager = remoteIisManager ?? throw new ArgumentNullException(nameof(remoteIisManager));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ServerInfo> RefreshServerStatusAsync(ServerInfo server, CancellationToken cancellationToken = default)
    {
        if (server == null) throw new ArgumentNullException(nameof(server));

        return await _metrics.MeasureAsync("server_status_refresh", async () =>
        {
            server.IsStatusLoading = true;

            try
            {
                if (!string.IsNullOrEmpty(server.PoolName))
                {
                    var poolStatus = await GetPoolStatusAsync(server.PoolName, cancellationToken);
                    server.PoolStatus = poolStatus ?? "Unknown";
                    _logger.LogDebug("Retrieved pool status for {PoolName}: {Status}", server.PoolName, poolStatus);
                }

                if (!string.IsNullOrEmpty(server.SiteName))
                {
                    var siteStatus = await GetWebsiteStatusAsync(server.SiteName, cancellationToken);
                    server.SiteStatus = siteStatus ?? "Unknown";
                    _logger.LogDebug("Retrieved site status for {SiteName}: {Status}", server.SiteName, siteStatus);
                }

                server.LastUpdated = DateTime.UtcNow;
                server.IsOnline = DetermineServerOnlineStatus(server);
                
                _metrics.IncrementCounter("server_status_success", new() { 
                    ["server_name"] = server.Name ?? "unknown" 
                });

                return server;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh status for server {ServerName}", server.Name);
                server.PoolStatus = "Error";
                server.SiteStatus = "Error";
                server.IsOnline = false;
                
                _metrics.IncrementCounter("server_status_error", new() { 
                    ["server_name"] = server.Name ?? "unknown",
                    ["error_type"] = ex.GetType().Name
                });
                
                throw;
            }
            finally
            {
                server.IsStatusLoading = false;
            }
        }, new() { ["server_name"] = server.Name ?? "unknown" });
    }

    public async Task RefreshMultipleServerStatusAsync(ServerInfo[]? servers, CancellationToken cancellationToken = default)
    {
        if (servers == null || servers.Length == 0) return;

        await _metrics.MeasureAsync("multiple_server_status_refresh", async () =>
        {
            _logger.LogInformation("Refreshing status for {ServerCount} servers", servers.Length);
            
            var semaphore = new SemaphoreSlim(Math.Min(servers.Length, Environment.ProcessorCount));
            
            var tasks = servers.Select(async server =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    await RefreshServerStatusAsync(server, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to refresh status for server {ServerName}", server.Name);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            
            var successCount = servers.Count(s => s.PoolStatus != "Error" && s.SiteStatus != "Error");
            _logger.LogInformation("Status refresh completed: {SuccessCount}/{TotalCount} servers successful", 
                successCount, servers.Length);
                
            _metrics.SetGauge("servers_online", successCount);
            _metrics.SetGauge("servers_total", servers.Length);
            
            return new object();
        });
    }

    private async Task<string?> GetPoolStatusAsync(string poolName, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _remoteIisManager.GetAppPoolStatusAsync(poolName, cancellationToken);
            return result.IsSuccess ? result.Value : "Error";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get pool status for {PoolName}", poolName);
            return "Error";
        }
    }

    private async Task<string?> GetWebsiteStatusAsync(string siteName, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _remoteIisManager.GetWebsiteStatusAsync(siteName, cancellationToken);
            return result.IsSuccess ? result.Value : "Error";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get website status for {SiteName}", siteName);
            return "Error";
        }
    }

    private static bool DetermineServerOnlineStatus(ServerInfo server)
    {
        return server.PoolStatus != "Error" && 
               server.SiteStatus != "Error" && 
               server.PoolStatus != "Unknown" && 
               server.SiteStatus != "Unknown";
    }
}
