using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using CreatioHelper.Application.Interfaces;
using System.Diagnostics;

namespace CreatioHelper.Infrastructure.Services.Performance;

/// <summary>
/// Health Check для мониторинга критических компонентов CreatioHelper
/// </summary>
public class CreatioHelperHealthCheck : IHealthCheck
{
    private readonly IRemoteIisManager _iisManager;
    private readonly IMetricsService _metrics;
    private readonly ILogger<CreatioHelperHealthCheck> _logger;

    public CreatioHelperHealthCheck(
        IRemoteIisManager iisManager, 
        IMetricsService metrics,
        ILogger<CreatioHelperHealthCheck> logger)
    {
        _iisManager = iisManager ?? throw new ArgumentNullException(nameof(iisManager));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var checks = new Dictionary<string, bool>();
        var details = new Dictionary<string, object>();

        try
        {
            // 1. Проверка доступности IIS
            checks["iis_connectivity"] = await CheckIisConnectivityAsync(cancellationToken);
            
            // 2. Проверка файловой системы
            checks["file_system"] = CheckFileSystemAccess();
            
            // 3. Проверка использования памяти (< 80%)
            var memoryUsage = CheckMemoryUsage();
            checks["memory_usage"] = memoryUsage < 80;
            details["memory_usage_percent"] = memoryUsage;
            
            // 4. Проверка доступности PowerShell
            checks["powershell"] = await CheckPowerShellAvailabilityAsync(cancellationToken);
            
            // 5. Проверка метрик сервиса
            checks["metrics_service"] = await CheckMetricsServiceAsync();

            var allHealthy = checks.All(c => c.Value);
            var unhealthyCount = checks.Count(c => !c.Value);

            if (allHealthy)
            {
                _logger.LogInformation("All health checks passed");
                return HealthCheckResult.Healthy("All systems operational", details);
            }
            else
            {
                _logger.LogWarning("Health check failed: {UnhealthyCount}/{TotalCount} checks failed", 
                    unhealthyCount, checks.Count);
                return HealthCheckResult.Unhealthy(
                    $"{unhealthyCount} out of {checks.Count} checks failed", 
                    data: details.Concat(checks.ToDictionary(k => k.Key, v => (object)v.Value)).ToDictionary(k => k.Key, v => v.Value));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check execution failed");
            return HealthCheckResult.Unhealthy("Health check execution failed", ex, details);
        }
    }

    private async Task<bool> CheckIisConnectivityAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Попытка получить статус любого пула
            var result = await _iisManager.GetAppPoolStatusAsync("DefaultAppPool", cancellationToken);
            return true; // Если не выбросило исключение, то IIS доступен
        }
        catch
        {
            return false;
        }
    }

    private bool CheckFileSystemAccess()
    {
        try
        {
            var tempFile = Path.GetTempFileName();
            File.WriteAllText(tempFile, "health check");
            var content = File.ReadAllText(tempFile);
            File.Delete(tempFile);
            return content == "health check";
        }
        catch
        {
            return false;
        }
    }

    private double CheckMemoryUsage()
    {
        try
        {
            var currentProcess = Process.GetCurrentProcess();
            var workingSet = currentProcess.WorkingSet64;
            var totalMemory = GC.GetTotalMemory(false);
            
            // Примерный расчет использования памяти в процентах
            return (double)workingSet / (1024 * 1024 * 1024) * 100; // Процент от 1GB
        }
        catch
        {
            return 100; // Если не можем определить, считаем критичным
        }
    }

    private async Task<bool> CheckPowerShellAvailabilityAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo.FileName = "powershell.exe";
            process.StartInfo.Arguments = "-Command \"Write-Output 'test'\"";
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.CreateNoWindow = true;
            
            process.Start();
            await process.WaitForExitAsync(cancellationToken);
            
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> CheckMetricsServiceAsync()
    {
        try
        {
            var metrics = await _metrics.GetMetricsAsync();
            return metrics != null;
        }
        catch
        {
            return false;
        }
    }
}
