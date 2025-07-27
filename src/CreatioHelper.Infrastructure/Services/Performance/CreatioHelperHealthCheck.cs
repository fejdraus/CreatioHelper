using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Shared.Interfaces;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Performance;

/// <summary>
/// Главный Health Check для CreatioHelper, объединяющий все критические компоненты
/// </summary>
public class CreatioHelperHealthCheck : IHealthCheck
{
    private readonly IHealthCheckService _healthCheckService;
    private readonly IRemoteIisManager _iisManager;
    private readonly IRedisManagerFactory _redisManagerFactory;
    private readonly ILogger<CreatioHelperHealthCheck> _logger;

    public CreatioHelperHealthCheck(
        IHealthCheckService healthCheckService,
        IRemoteIisManager iisManager,
        IRedisManagerFactory redisManagerFactory,
        ILogger<CreatioHelperHealthCheck> logger)
    {
        _healthCheckService = healthCheckService;
        _iisManager = iisManager;
        _redisManagerFactory = redisManagerFactory;
        _logger = logger;
        
        RegisterComponentChecks();
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("🏥 Starting comprehensive health check");
        
        var allResults = await _healthCheckService.CheckAllAsync(cancellationToken);
        
        var healthyCount = allResults.Count(r => r.Value.IsHealthy);
        var unhealthyCount = allResults.Count - healthyCount;
        var totalDuration = TimeSpan.FromMilliseconds(allResults.Values.Sum(r => r.Duration.TotalMilliseconds));
        
        var data = new Dictionary<string, object>
        {
            ["total_checks"] = allResults.Count,
            ["healthy_checks"] = healthyCount,
            ["unhealthy_checks"] = unhealthyCount,
            ["total_duration_ms"] = totalDuration.TotalMilliseconds,
            ["timestamp"] = DateTime.UtcNow,
            ["checks"] = allResults.ToDictionary(
                kvp => kvp.Key,
                kvp => new
                {
                    IsHealthy = kvp.Value.IsHealthy,
                    Message = kvp.Value.Message,
                    DurationMs = kvp.Value.Duration.TotalMilliseconds,
                    Data = kvp.Value.Data
                })
        };

        if (unhealthyCount == 0)
        {
            _logger.LogInformation("✅ All {HealthyCount} systems are operational", healthyCount);
            return HealthCheckResult.Healthy(
                $"All {healthyCount} systems operational", data);
        }
        
        // Определяем критичность проблем
        var criticalIssues = allResults.Where(r => !r.Value.IsHealthy && IsCriticalComponent(r.Key)).ToList();
        
        if (criticalIssues.Any())
        {
            var criticalNames = string.Join(", ", criticalIssues.Select(r => r.Key));
            _logger.LogError("❌ Critical systems down: {CriticalSystems}", criticalNames);
            return HealthCheckResult.Unhealthy(
                $"Critical systems down: {criticalNames}", data);
        }
        
        var nonCriticalNames = string.Join(", ", 
            allResults.Where(r => !r.Value.IsHealthy).Select(r => r.Key));
        
        _logger.LogWarning("⚠️ Core systems operational, minor issues: {MinorIssues}", nonCriticalNames);
        
        return HealthCheckResult.Healthy(
            $"Core systems operational, minor issues: {nonCriticalNames}", data);
    }

    private void RegisterComponentChecks()
    {
        // Регистрируем все компонентные health checks
        _healthCheckService.RegisterHealthCheck("file_system", new FileSystemHealthCheck());
        _healthCheckService.RegisterHealthCheck("memory", new MemoryHealthCheck());
        _healthCheckService.RegisterHealthCheck("iis_connectivity", new IisConnectivityHealthCheck(_iisManager));
        _healthCheckService.RegisterHealthCheck("redis", new RedisHealthCheck(_redisManagerFactory));
        
        _logger.LogDebug("📝 Registered {Count} component health checks", 4);
    }

    private static bool IsCriticalComponent(string componentName)
    {
        // Определяем, какие компоненты критичны для работы CreatioHelper
        var criticalComponents = new[] { "file_system", "memory", "iis_connectivity" };
        return criticalComponents.Contains(componentName);
    }
}

/// <summary>
/// Health Check для проверки файловой системы
/// </summary>
internal class FileSystemHealthCheck : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var tempPath = Path.GetTempPath();
            var testFile = Path.Combine(tempPath, $"creatiohelper_healthcheck_{Guid.NewGuid()}.tmp");
            
            // Проверяем возможность записи
            await File.WriteAllTextAsync(testFile, "health check test", cancellationToken);
            
            // Проверяем возможность чтения
            var content = await File.ReadAllTextAsync(testFile, cancellationToken);
            
            // Удаляем тестовый файл
            File.Delete(testFile);
            
            var data = new Dictionary<string, object>
            {
                ["temp_path"] = tempPath,
                ["writable"] = true,
                ["readable"] = true
            };

            return HealthCheckResult.Healthy("File system is accessible", data);
        }
        catch (Exception ex)
        {
            var data = new Dictionary<string, object>
            {
                ["error"] = ex.Message,
                ["error_type"] = ex.GetType().Name
            };
            
            return HealthCheckResult.Unhealthy($"File system check failed: {ex.Message}", data);
        }
    }
}

/// <summary>
/// Health Check для проверки использования памяти
/// </summary>
internal class MemoryHealthCheck : IHealthCheck
{
    private const long CriticalMemoryThreshold = 2L * 1024 * 1024 * 1024; // 2GB
    private const long WarningMemoryThreshold = 1L * 1024 * 1024 * 1024;  // 1GB

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var workingSet = process.WorkingSet64;
            var privateMemory = process.PrivateMemorySize64;
            
            var data = new Dictionary<string, object>
            {
                ["working_set_mb"] = workingSet / (1024 * 1024),
                ["private_memory_mb"] = privateMemory / (1024 * 1024),
                ["warning_threshold_mb"] = WarningMemoryThreshold / (1024 * 1024),
                ["critical_threshold_mb"] = CriticalMemoryThreshold / (1024 * 1024)
            };

            if (workingSet > CriticalMemoryThreshold)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Critical memory usage: {workingSet / (1024 * 1024)}MB", data));
            }
            
            if (workingSet > WarningMemoryThreshold)
            {
                return Task.FromResult(HealthCheckResult.Healthy(
                    $"Warning: High memory usage: {workingSet / (1024 * 1024)}MB", data));
            }

            return Task.FromResult(HealthCheckResult.Healthy(
                $"Memory usage normal: {workingSet / (1024 * 1024)}MB", data));
        }
        catch (Exception ex)
        {
            var data = new Dictionary<string, object>
            {
                ["error"] = ex.Message,
                ["error_type"] = ex.GetType().Name
            };
            
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Memory check failed: {ex.Message}", data));
        }
    }
}

/// <summary>
/// Health Check для проверки IIS подключения
/// </summary>
internal class IisConnectivityHealthCheck : IHealthCheck
{
    private readonly IRemoteIisManager _iisManager;

    public IisConnectivityHealthCheck(IRemoteIisManager iisManager)
    {
        _iisManager = iisManager;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // Попробуем получить статус DefaultAppPool для проверки подключения к IIS
            var appPoolStatus = await _iisManager.GetAppPoolStatusAsync("DefaultAppPool", cancellationToken);
            
            var data = new Dictionary<string, object>
            {
                ["default_app_pool_accessible"] = appPoolStatus.IsSuccess,
                ["app_pool_status"] = appPoolStatus.IsSuccess ? appPoolStatus.Value ?? "Unknown" : "Not accessible",
                ["error_message"] = appPoolStatus.IsSuccess ? "None" : (appPoolStatus.ErrorMessage ?? "Unknown error")
            };

            if (appPoolStatus.IsSuccess)
            {
                return HealthCheckResult.Healthy($"IIS is accessible, DefaultAppPool status: {appPoolStatus.Value ?? "Unknown"}", data);
            }
            else
            {
                return HealthCheckResult.Unhealthy($"IIS connectivity issue: {appPoolStatus.ErrorMessage ?? "Unknown error"}", data);
            }
        }
        catch (Exception ex)
        {
            var data = new Dictionary<string, object>
            {
                ["error"] = ex.Message,
                ["error_type"] = ex.GetType().Name,
                ["iis_accessible"] = false
            };
            
            return HealthCheckResult.Unhealthy($"IIS connectivity check failed: {ex.Message}", data);
        }
    }
}

/// <summary>
/// Health Check для проверки Redis подключения
/// </summary>
internal class RedisHealthCheck : IHealthCheck
{
    private readonly IRedisManagerFactory _redisManagerFactory;

    public RedisHealthCheck(IRedisManagerFactory redisManagerFactory)
    {
        _redisManagerFactory = redisManagerFactory;
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // Проверяем доступность Redis (используем тестовый путь)
            var testPath = @"C:\inetpub\wwwroot\test";
            var redisManager = _redisManagerFactory.Create(testPath);
            var status = redisManager.CheckStatus();
            
            var data = new Dictionary<string, object>
            {
                ["redis_available"] = status,
                ["test_path"] = testPath
            };

            if (status)
            {
                return Task.FromResult(HealthCheckResult.Healthy("Redis is available", data));
            }
            else
            {
                return Task.FromResult(HealthCheckResult.Healthy("Redis is not configured (optional)", data));
            }
        }
        catch (Exception ex)
        {
            var data = new Dictionary<string, object>
            {
                ["error"] = ex.Message,
                ["error_type"] = ex.GetType().Name,
                ["redis_available"] = false
            };
            
            return Task.FromResult(HealthCheckResult.Unhealthy($"Redis check failed: {ex.Message}", data));
        }
    }
}
