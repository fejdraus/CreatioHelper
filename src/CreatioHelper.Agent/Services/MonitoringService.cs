using Microsoft.AspNetCore.SignalR;
using CreatioHelper.Agent.Hubs;
using CreatioHelper.Agent.Services.Windows;
using CreatioHelper.Domain.Enums;
using CreatioHelper.Domain.Entities;
using System;

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
            }
        }
    }

    public void RemoveServerFromMonitoring(string siteName)
    {
        lock (_monitoredServers)
        {
            _monitoredServers.RemoveAll(s => s.SiteName == siteName);
            _logger.LogInformation("Removed server {SiteName} from monitoring", siteName);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MonitoringService started!");
    
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MonitorServers();
                await BroadcastWebServerOverview();
            
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in monitoring service");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }
    
    public async Task BroadcastWebServerOverview()
    {
        using var scope = _serviceProvider.CreateScope();
        var webServerFactory = scope.ServiceProvider.GetService<IWebServerServiceFactory>();
    
        if (webServerFactory == null || !webServerFactory.IsWebServerSupported())
            return;

        try
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting web server overview");
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
            PlatformType.MacOS => "macOS/Launchd",
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
        if (OperatingSystem.IsWindows())
        {
            var statusService = scope.ServiceProvider.GetService<IisStatusService>();
            if (statusService == null)
            {
                return;
            }

            try
            {
                var statuses = await statusService.GetMultipleServersStatusAsync(serversToMonitor);
                await _hubContext.Clients.Group("monitoring").SendAsync("ServerStatusUpdate", statuses);
                foreach (var status in statuses)
                {
                    if (!status.IsHealthy)
                    {
                        _logger.LogWarning("Server {SiteName} is unhealthy: Site={SiteStatus}, Pool={PoolStatus}", 
                            status.SiteName, status.SiteStatus, status.PoolStatus);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error monitoring servers");
            }
        }
    }
}