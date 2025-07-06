using CreatioHelper.Agent.Abstractions;
using CreatioHelper.Core.Models;

namespace CreatioHelper.Agent.Services.Windows;

public class IisStatusService
{
    private readonly IisManagerService _iisManager;
    private readonly ILogger<IisStatusService> _logger;

    public IisStatusService(IisManagerService iisManager, ILogger<IisStatusService> logger)
    {
        _iisManager = iisManager;
        _logger = logger;
    }

    public async Task<ServerStatusInfo> GetServerStatusAsync(string siteName, string? poolName = null)
    {
        var status = new ServerStatusInfo
        {
            ServerName = Environment.MachineName,
            SiteName = siteName,
            PoolName = poolName,
            IsStatusLoading = true,
            LastUpdated = DateTime.UtcNow
        };

        try
        {
            // Получаем статус сайта
            if (!string.IsNullOrWhiteSpace(siteName))
            {
                var siteResult = await _iisManager.GetSiteStatusAsync(siteName);
                if (siteResult.Success && siteResult.Data is { } siteData)
                {
                    var siteInfo = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
                        System.Text.Json.JsonSerializer.Serialize(siteData));
                    status.SiteStatus = siteInfo?["Status"]?.ToString() ?? "Unknown";
                }
                else
                {
                    status.SiteStatus = "Error";
                }
            }

            // Получаем статус пула
            if (!string.IsNullOrWhiteSpace(poolName))
            {
                var poolResult = await _iisManager.GetAppPoolStatusAsync(poolName);
                if (poolResult.Success && poolResult.Data is { } poolData)
                {
                    var poolInfo = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
                        System.Text.Json.JsonSerializer.Serialize(poolData));
                    status.PoolStatus = poolInfo?["Status"]?.ToString() ?? "Unknown";
                }
                else
                {
                    status.PoolStatus = "Error";
                }
            }

            status.IsHealthy = IsStatusHealthy(status);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get server status for site {SiteName}", siteName);
            status.SiteStatus = "Error";
            status.PoolStatus = "Error";
            status.IsHealthy = false;
            status.ErrorMessage = ex.Message;
        }
        finally
        {
            status.IsStatusLoading = false;
            status.LastUpdated = DateTime.UtcNow;
        }

        return status;
    }

    public async Task<List<ServerStatusInfo>> GetMultipleServersStatusAsync(params ServerRequest[] requests)
    {
        var tasks = requests.Select(request => 
            GetServerStatusAsync(request.SiteName, request.PoolName)).ToArray();
        
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    private static bool IsStatusHealthy(ServerStatusInfo status)
    {
        var siteHealthy = string.IsNullOrWhiteSpace(status.SiteName) || 
                         string.Equals(status.SiteStatus, "Started", StringComparison.OrdinalIgnoreCase);
        
        var poolHealthy = string.IsNullOrWhiteSpace(status.PoolName) || 
                         string.Equals(status.PoolStatus, "Started", StringComparison.OrdinalIgnoreCase);

        return siteHealthy && poolHealthy;
    }
}
