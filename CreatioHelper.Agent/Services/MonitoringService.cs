using Microsoft.AspNetCore.SignalR;
using CreatioHelper.Agent.Hubs;
using CreatioHelper.Agent.Services.Windows;
using CreatioHelper.Agent.Models;

namespace CreatioHelper.Agent.Services;

public class MonitoringService : BackgroundService
{
    private readonly IHubContext<MonitoringHub> _hubContext;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<MonitoringService> _logger;
    private readonly List<ServerRequest> _monitoredServers = new();

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
            if (!_monitoredServers.Any(s => s.SiteName == siteName))
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
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await MonitorServers();
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in monitoring service");
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
    }

    private async Task MonitorServers()
    {
        ServerRequest[] serversToMonitor;
        lock (_monitoredServers)
        {
            serversToMonitor = _monitoredServers.ToArray();
        }

        if (serversToMonitor.Length == 0)
            return;

        using var scope = _serviceProvider.CreateScope();
        var statusService = scope.ServiceProvider.GetService<IisStatusService>();
        
        if (statusService == null)
            return;

        try
        {
            var statuses = await statusService.GetMultipleServersStatusAsync(serversToMonitor);
            
            // Отправляем статус всем подключенным клиентам
            await _hubContext.Clients.Group("monitoring").SendAsync("ServerStatusUpdate", statuses);
            
            // Логируем только изменения статуса
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
    
    public async Task BroadcastIisOverview()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var scope = _serviceProvider.CreateScope();
        var iisManager = scope.ServiceProvider.GetService<IisManagerService>();
    
        if (iisManager == null)
            return;

        try
        {
            var sitesTask = iisManager.GetAllSitesAsync();
            var appPoolsTask = iisManager.GetAllAppPoolsAsync();
        
            await Task.WhenAll(sitesTask, appPoolsTask);
        
            var sites = await sitesTask;
            var appPools = await appPoolsTask;
        
            var overview = new
            {
                ServerName = Environment.MachineName,
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
        
            // Отправляем обзор всем подключенным клиентам
            await _hubContext.Clients.Group("iis-overview").SendAsync("IisOverviewUpdate", overview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting IIS overview");
        }
    }
}