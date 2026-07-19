using Microsoft.AspNetCore.SignalR;
using CreatioHelper.Agent.Hubs;
using CreatioHelper.Agent.Services.Windows;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Enums;
using CreatioHelper.Contracts.Requests;

namespace CreatioHelper.Agent.Services;

public class MonitoringService : BackgroundService
{
    private readonly IHubContext<MonitoringHub> _hubContext;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MonitoringService> _logger;
    private readonly List<ServerRequest> _monitoredServers = [];

    public MonitoringService(
        IHubContext<MonitoringHub> hubContext,
        IServiceProvider serviceProvider,
        ILogger<MonitoringService> logger)
    {
        _hubContext = hubContext;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public void AddServerToMonitoring(string siteName, string? poolName = null)
    {
        lock (_monitoredServers)
        {
            if (_monitoredServers.All(s => s.SiteName != siteName))
            {
                _monitoredServers.Add(new ServerRequest { SiteName = siteName, PoolName = poolName });
                _logger.LogInformation("Added server {SiteName} to monitoring", siteName);
                
                // Metric for adding a server to monitoring
                using var scope = _serviceProvider.CreateScope();
                var metrics = scope.ServiceProvider.GetService<IMetricsService>();
                metrics?.IncrementCounter("monitoring_server_added");
                metrics?.SetGauge("monitoring_servers_count", _monitoredServers.Count);
            }
        }
    }

    public void RemoveServerFromMonitoring(string siteName)
    {
        lock (_monitoredServers)
        {
            var removed = _monitoredServers.RemoveAll(s => s.SiteName == siteName);
            if (removed > 0)
            {
                _logger.LogInformation("Removed server {SiteName} from monitoring", siteName);
                
                // Metric for removing a server from monitoring
                using var scope = _serviceProvider.CreateScope();
                var metrics = scope.ServiceProvider.GetService<IMetricsService>();
                metrics?.IncrementCounter("monitoring_server_removed");
                metrics?.SetGauge("monitoring_servers_count", _monitoredServers.Count);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MonitoringService started!");
    
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var metrics = scope.ServiceProvider.GetService<IMetricsService>();
                
                if (metrics != null)
                {
                    await metrics.MeasureAsync("monitoring_cycle", async () =>
                    {
                        await MonitorServers();
                        await BroadcastWebServerOverview();
                        return 1;
                    });
                }
                else
                {
                    await MonitorServers();
                    await BroadcastWebServerOverview();
                }
            
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in monitoring service");
                
                // Metric for monitoring errors
                using var scope = _serviceProvider.CreateScope();
                var metrics = scope.ServiceProvider.GetService<IMetricsService>();
                metrics?.IncrementCounter("monitoring_error");
                
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }
    
    public async Task BroadcastWebServerOverview()
    {
        using var scope = _serviceProvider.CreateScope();
        var webServerFactory = scope.ServiceProvider.GetService<IWebServerServiceFactory>();
        var metrics = scope.ServiceProvider.GetService<IMetricsService>();
        var accessStatus = scope.ServiceProvider.GetService<WebServerAccessStatus>();
        var registry = scope.ServiceProvider.GetService<WebSiteRegistryService>();

        if (webServerFactory == null || registry == null || !webServerFactory.IsWebServerSupported())
            return;

        try
        {
            if (metrics != null)
            {
                await metrics.MeasureAsync("webserver_overview_broadcast", async () =>
                {
                    await BuildAndSendOverviewAsync(webServerFactory, registry, accessStatus, metrics);
                    metrics.IncrementCounter("webserver_overview_broadcast_success");
                    return 1;
                });
            }
            else
            {
                await BuildAndSendOverviewAsync(webServerFactory, registry, accessStatus, null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting web server overview");
            metrics?.IncrementCounter("webserver_overview_broadcast_error");
        }
    }

    private async Task BuildAndSendOverviewAsync(
        IWebServerServiceFactory factory,
        WebSiteRegistryService registry,
        WebServerAccessStatus? accessStatus,
        IMetricsService? metrics)
    {
        var registeredSites = await registry.GetAllSitesAsync();
        var iisSites = registeredSites.Where(s => s.EffectiveKind == WebServerKind.Iis).ToList();
        var serviceSites = registeredSites.Where(s => s.EffectiveKind == WebServerKind.Service).ToList();

        var sites = new List<WebServerStatus>();
        var appPools = new List<WebServerStatus>();

        if (iisSites.Count > 0)
        {
            var iisManager = await factory.CreateWebServerServiceForSiteAsync(iisSites[0]);
            sites.AddRange(await iisManager.GetAllSitesAsync());
            appPools = await iisManager.GetAllAppPoolsAsync();
        }

        foreach (var site in serviceSites)
        {
            var manager = await factory.CreateWebServerServiceForSiteAsync(site);
            var status = await manager.GetSiteStatusAsync(site.ServiceName);
            var state = status.Data?.Status ?? site.Status;
            sites.Add(new WebServerStatus
            {
                Name = site.Name,
                Status = state,
                Type = "Service",
                Port = "",
                IsRunning = IsRunningState(state),
                LastChecked = DateTime.UtcNow
            });
        }

        var overview = new
        {
            ServerName = Environment.MachineName,
            Platform = GetPlatformName(),
            Timestamp = DateTime.UtcNow,
            Sites = sites,
            AppPools = appPools,
            RequiresElevation = accessStatus?.RequiresElevation ?? false,
            AccessMessage = accessStatus?.Message,
            Summary = new
            {
                TotalSites = sites.Count,
                RunningSites = sites.Count(s => s.IsRunning),
                TotalAppPools = appPools.Count,
                RunningAppPools = appPools.Count(s => s.IsRunning)
            }
        };

        if (metrics != null)
        {
            metrics.SetGauge("webserver_total_sites", sites.Count);
            metrics.SetGauge("webserver_running_sites", sites.Count(s => s.IsRunning));
            metrics.SetGauge("webserver_total_app_pools", appPools.Count);
            metrics.SetGauge("webserver_running_app_pools", appPools.Count(s => s.IsRunning));
        }

        await _hubContext.Clients.Group("webserver-overview").SendAsync("WebServerOverviewUpdate", overview);
    }

    private static bool IsRunningState(string? state) =>
        string.Equals(state, "Started", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase);
    
    private string GetPlatformName()
    {
        using var scope = _serviceProvider.CreateScope();
        var platformService = scope.ServiceProvider.GetService<IPlatformService>();
    
        return platformService?.GetPlatform() switch
        {
            PlatformType.Windows => "Windows/IIS",
            PlatformType.Linux => "Linux/Systemd", 
            PlatformType.MacOs => "macOS/Launchd",
            _ => "Unknown"
        };
    }

    private async Task MonitorServers()
    {
        ServerRequest[] serversToMonitor;
        lock (_monitoredServers)
        {
            serversToMonitor = _monitoredServers.ToArray();
        }

        if (serversToMonitor.Length == 0)
        {
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var metrics = scope.ServiceProvider.GetService<IMetricsService>();
        
        if (OperatingSystem.IsWindows())
        {
            var statusService = scope.ServiceProvider.GetService<IisStatusService>();
            if (statusService == null)
            {
                return;
            }

            try
            {
                var statuses = await (metrics?.MeasureAsync("monitor_servers_status_check", 
                    () => statusService.GetMultipleServersStatusAsync(serversToMonitor)) 
                    ?? statusService.GetMultipleServersStatusAsync(serversToMonitor));
                
                await _hubContext.Clients.Group("monitoring").SendAsync("ServerStatusUpdate", statuses);
                
                // Metrics for successful monitoring
                metrics?.IncrementCounter("monitor_servers_success");
                metrics?.SetGauge("monitored_servers_count", serversToMonitor.Length);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error monitoring servers");
                metrics?.IncrementCounter("monitor_servers_error");
            }
        }
    }
}