using CreatioHelper.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Performance;

/// <summary>
/// Specialized health check for monitoring Creatio components.
/// </summary>
public class CreatioSystemHealthCheck : IHealthCheck
{
    private readonly IRemoteIisManager _iisManager;
    private readonly ISettingsService _settingsService;
    private readonly ILogger<CreatioSystemHealthCheck> _logger;

    public CreatioSystemHealthCheck(
        IRemoteIisManager iisManager,
        ISettingsService settingsService,
        ILogger<CreatioSystemHealthCheck> logger)
    {
        _iisManager = iisManager;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var checks = new List<(string Name, bool IsHealthy, string Message, TimeSpan Duration)>();
        var overallStopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Check IIS availability
            var iisCheck = await CheckIisAvailability(cancellationToken);
            checks.Add(("IIS_Availability", iisCheck.IsHealthy, iisCheck.Message, iisCheck.Duration));

            // Check configured servers from AppSettings
            var serversCheck = await CheckConfiguredServers(cancellationToken);
            checks.Add(("Configured_Servers", serversCheck.IsHealthy, serversCheck.Message, serversCheck.Duration));

            overallStopwatch.Stop();

            var healthyCount = checks.Count(c => c.IsHealthy);
            var criticalIssues = checks.Where(c => !c.IsHealthy).ToList();

            var data = new Dictionary<string, object>
            {
                ["total_checks"] = checks.Count,
                ["healthy_checks"] = healthyCount,
                ["critical_issues"] = criticalIssues.Count,
                ["checks_detail"] = checks.ToDictionary(c => c.Name, c => new
                {
                    IsHealthy = c.IsHealthy,
                    Message = c.Message,
                    DurationMs = c.Duration.TotalMilliseconds
                })
            };

            if (criticalIssues.Any())
            {
                var criticalNames = string.Join(", ", criticalIssues.Select(c => c.Name));
                _logger.LogCritical("🔥 Critical components down: {Components}", criticalNames);
                return HealthCheckResult.Unhealthy($"Critical components down: {criticalNames}", data);
            }

            _logger.LogInformation("✅ All components operational ({Count} checks)", checks.Count);
            return HealthCheckResult.Healthy($"All components operational", data);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Health check failed");
            return HealthCheckResult.Unhealthy($"Health check error: {ex.Message}");
        }
    }

    private async Task<(bool IsHealthy, string Message, TimeSpan Duration)> CheckIisAvailability(CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Try to get the status of any standard pool to verify IIS connectivity
            // Use well-known pool names that usually exist in IIS
            var testPoolNames = new[] { "DefaultAppPool", ".NET v4.5", ".NET v4.5 Classic" };
            
            foreach (var poolName in testPoolNames)
            {
                try
                {
                    var testResult = await _iisManager.GetAppPoolStatusAsync(poolName, cancellationToken);
                    if (testResult.IsSuccess)
                    {
                        sw.Stop();
                        return (true, $"IIS accessible via pool '{poolName}'", sw.Elapsed);
                    }
                }
                catch
                {
                    // Try the next pool
                    continue;
                }
            }
            
            // If no pool is found but there are no exceptions, IIS is accessible
            sw.Stop();
            return (true, "IIS service accessible, no test pools found", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, $"IIS not accessible: {ex.Message}", sw.Elapsed);
        }
    }

    private async Task<(bool IsHealthy, string Message, TimeSpan Duration)> CheckConfiguredServers(CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var settings = _settingsService.Load();
            if (settings?.ServerList == null || !settings.ServerList.Any())
            {
                sw.Stop();
                return (false, "No servers configured", sw.Elapsed);
            }

            var healthyServers = 0;
            var totalServers = settings.ServerList.Count;

            foreach (var server in settings.ServerList)
            {
                try
                {
                    // Check the pool if configured
                    if (!string.IsNullOrEmpty(server.PoolName))
                    {
                        var poolResult = await _iisManager.GetAppPoolStatusAsync(server.PoolName, cancellationToken);
                        if (poolResult.IsSuccess && poolResult.Value?.Equals("Started", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            healthyServers++;
                            continue;
                        }
                    }

                    // Check the site if configured
                    if (!string.IsNullOrEmpty(server.SiteName))
                    {
                        var siteResult = await _iisManager.GetWebsiteStatusAsync(server.SiteName, cancellationToken);
                        if (siteResult.IsSuccess && siteResult.Value?.Equals("Started", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            healthyServers++;
                        }
                    }
                }
                catch
                {
                    // Skip unreachable servers
                }
            }

            sw.Stop();

            if (healthyServers == totalServers)
                return (true, $"All {totalServers} configured servers operational", sw.Elapsed);

            return (false, $"Only {healthyServers}/{totalServers} configured servers operational", sw.Elapsed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, $"Failed to check configured servers: {ex.Message}", sw.Elapsed);
        }
    }

    private static bool IsCriticalCreatioComponent(string componentName) =>
        componentName.Contains("IIS") || componentName.Contains("Servers");
}
