using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Performance;

/// <summary>
/// Специализированный Health Check для мониторинга Creatio компонентов
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
            // 1. Проверка доступности IIS
            var iisCheck = await CheckIisAvailability(cancellationToken);
            checks.Add(("IIS_Availability", iisCheck.IsHealthy, iisCheck.Message, iisCheck.Duration));

            // 2. Проверка настроенных серверов из AppSettings
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
            // Попробуем получить статус любого стандартного пула для проверки подключения к IIS
            // Используем общеизвестные имена пулов, которые обычно существуют в IIS
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
                    // Пробуем следующий пул
                    continue;
                }
            }
            
            // Если ни один пул не найден, но исключений нет - IIS доступен
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
                    // Проверяем пул, если настроен
                    if (!string.IsNullOrEmpty(server.PoolName))
                    {
                        var poolResult = await _iisManager.GetAppPoolStatusAsync(server.PoolName, cancellationToken);
                        if (poolResult.IsSuccess && poolResult.Value?.Equals("Started", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            healthyServers++;
                            continue;
                        }
                    }

                    // Проверяем сайт, если настроен
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
                    // Сервер недоступен - пропускаем
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
