using System;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services;

public class ServerStatusService : IServerStatusService
{
    private readonly IIisManager _iisManager;
    private readonly IMetricsService _metrics;
    private readonly ILogger<ServerStatusService> _logger;

    public ServerStatusService(
        IIisManager iisManager,
        IMetricsService metrics,
        ILogger<ServerStatusService> logger)
    {
        _iisManager = iisManager ?? throw new ArgumentNullException(nameof(iisManager));
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
                    var poolStatus = await GetPoolStatusAsync(server.Name ?? Environment.MachineName, server.PoolName, cancellationToken).ConfigureAwait(false);
                    server.PoolStatus = poolStatus ?? "Unknown";
                    _logger.LogDebug("Retrieved pool status for {PoolName}: {Status}", server.PoolName, poolStatus);
                }

                if (!string.IsNullOrEmpty(server.SiteName))
                {
                    var siteStatus = await GetWebsiteStatusAsync(server.Name ?? Environment.MachineName, server.SiteName, cancellationToken).ConfigureAwait(false);
                    server.SiteStatus = siteStatus ?? "Unknown";
                    _logger.LogDebug("Retrieved site status for {SiteName}: {Status}", server.SiteName, siteStatus);
                }

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
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await RefreshServerStatusAsync(server, cancellationToken).ConfigureAwait(false);
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

            await Task.WhenAll(tasks).ConfigureAwait(false);
            
            var successCount = servers.Count(s => s.PoolStatus != "Error" && s.SiteStatus != "Error");
            _logger.LogInformation("Status refresh completed: {SuccessCount}/{TotalCount} servers successful", 
                successCount, servers.Length);
                
            _metrics.SetGauge("servers_online", successCount);
            _metrics.SetGauge("servers_total", servers.Length);
            
            return new object();
        });
    }

    private async Task<string?> GetPoolStatusAsync(string serverName, string poolName, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _iisManager.GetAppPoolStatusAsync(serverName, poolName, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? result.Value : "Error";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get pool status for {PoolName}", poolName);
            return "Error";
        }
    }

    private async Task<string?> GetWebsiteStatusAsync(string serverName, string siteName, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _iisManager.GetWebsiteStatusAsync(serverName, siteName, cancellationToken).ConfigureAwait(false);
            return result.IsSuccess ? result.Value : "Error";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get website status for {SiteName}", siteName);
            return "Error";
        }
    }
}
