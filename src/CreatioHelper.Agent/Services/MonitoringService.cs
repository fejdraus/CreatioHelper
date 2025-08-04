using Microsoft.AspNetCore.SignalR;
using CreatioHelper.Agent.Hubs;
using CreatioHelper.Agent.Services.Windows;
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
    
        if (webServerFactory == null || !webServerFactory.IsWebServerSupported())
            return;

        try
        {
            if (metrics != null)
            {
                await metrics.MeasureAsync("webserver_overview_broadcast", async () =>
                {
                    var webServerService = await webServerFactory.CreateWebServerServiceAsync();
                
                    var sitesTask = webServerService.GetAllSitesAsync();
                    var appPoolsTask = webServerService.GetAllAppPoolsAsync();
                    await Task.WhenAll(sitesTask, appPoolsTask);
                
                    var sites = await sitesTask;
                    var appPools = await appPoolsTask;
                
                    var overview = new
                    {
                        ServerName = Environment.MachineName,
                        Platform = GetPlatformName(),
                        Timestamp = DateTime.UtcNow,
                        Sites = sites,
                        AppPools = appPools,
                        Summary = new
                        {
                            TotalSites = sites.Count,
                            RunningSites = sites.Count(s => s.IsRunning),
                            TotalAppPools = appPools.Count,
                            RunningAppPools = appPools.Count(s => s.IsRunning)
                        }
                    };
                    
                    // Set state metrics
                    metrics.SetGauge("webserver_total_sites", sites.Count);
                    metrics.SetGauge("webserver_running_sites", sites.Count(s => s.IsRunning));
                    metrics.SetGauge("webserver_total_app_pools", appPools.Count);
                    metrics.SetGauge("webserver_running_app_pools", appPools.Count(s => s.IsRunning));
                
                    await _hubContext.Clients.Group("webserver-overview").SendAsync("WebServerOverviewUpdate", overview);
                    
                    metrics.IncrementCounter("webserver_overview_broadcast_success");
                    
                    return 1; // Indicate successful completion
                });
            }
            else
            {
                var webServerService = await webServerFactory.CreateWebServerServiceAsync();
            
                var sitesTask = webServerService.GetAllSitesAsync();
                var appPoolsTask = webServerService.GetAllAppPoolsAsync();
                await Task.WhenAll(sitesTask, appPoolsTask);
            
                var sites = await sitesTask;
                var appPools = await appPoolsTask;
            
                var overview = new
                {
                    ServerName = Environment.MachineName,
                    Platform = GetPlatformName(),
                    Timestamp = DateTime.UtcNow,
                    Sites = sites,
                    AppPools = appPools,
                    Summary = new
                    {
                        TotalSites = sites.Count,
                        RunningSites = sites.Count(s => s.IsRunning),
                        TotalAppPools = appPools.Count,
                        RunningAppPools = appPools.Count(s => s.IsRunning)
                    }
                };
            
                await _hubContext.Clients.Group("webserver-overview").SendAsync("WebServerOverviewUpdate", overview);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting web server overview");
            metrics?.IncrementCounter("webserver_overview_broadcast_error");
        }
    }
    
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