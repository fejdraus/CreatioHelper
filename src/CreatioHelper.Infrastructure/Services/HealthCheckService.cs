using System.Collections.Concurrent;
using System.Diagnostics;
using CreatioHelper.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services;

/// <summary>
/// Health Checks service implementation for CreatioHelper
/// </summary>
public class HealthCheckService : IHealthCheckService
{
    private readonly ILogger<HealthCheckService> _logger;
    private readonly ConcurrentDictionary<string, IHealthCheck> _healthChecks = new();

    public HealthCheckService(ILogger<HealthCheckService> logger)
    {
        _logger = logger;
    }

    public async Task<Dictionary<string, HealthCheckResult>> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, HealthCheckResult>();
        
        _logger.LogInformation("🏥 Starting health checks for {Count} components", _healthChecks.Count);

        var tasks = _healthChecks.Select(async kvp =>
        {
            var (name, _) = (kvp.Key, kvp.Value);
            var result = await CheckAsync(name, cancellationToken);
            return new { Name = name, Result = result };
        });

        var completedTasks = await Task.WhenAll(tasks);
        
        foreach (var task in completedTasks)
        {
            results[task.Name] = task.Result;
        }

        var healthyCount = results.Count(r => r.Value.IsHealthy);
        var unhealthyCount = results.Count - healthyCount;
        
        _logger.LogInformation("🏥 Health checks completed: {Healthy} healthy, {Unhealthy} unhealthy", 
            healthyCount, unhealthyCount);

        return results;
    }

    public async Task<HealthCheckResult> CheckAsync(string checkName, CancellationToken cancellationToken = default)
    {
        if (!_healthChecks.TryGetValue(checkName, out var healthCheck))
        {
            return HealthCheckResult.Unhealthy($"Health check '{checkName}' not found");
        }

        var stopwatch = Stopwatch.StartNew();
        
        try
        {
            _logger.LogDebug("🔍 Running health check: {CheckName}", checkName);
            
            var context = new HealthCheckContext { Name = checkName };
            var result = await healthCheck.CheckHealthAsync(context, cancellationToken);
            
            stopwatch.Stop();
            
            var finalResult = new HealthCheckResult
            {
                IsHealthy = result.IsHealthy,
                Message = result.Message,
                Data = result.Data,
                Duration = stopwatch.Elapsed
            };

            var emoji = result.IsHealthy ? "✅" : "❌";
            _logger.LogDebug("{Emoji} Health check {CheckName}: {Message} ({Duration}ms)", 
                emoji, checkName, result.Message, stopwatch.ElapsedMilliseconds);

            return finalResult;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            
            _logger.LogError(ex, "❌ Health check {CheckName} failed with exception", checkName);
            
            return new HealthCheckResult
            {
                IsHealthy = false,
                Message = $"Health check failed: {ex.Message}",
                Data = new Dictionary<string, object> { ["exception"] = ex.GetType().Name },
                Duration = stopwatch.Elapsed
            };
        }
    }

    public void RegisterHealthCheck(string name, IHealthCheck healthCheck)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Health check name cannot be null or empty", nameof(name));
        
        if (healthCheck == null)
            throw new ArgumentNullException(nameof(healthCheck));

        _healthChecks.AddOrUpdate(name, healthCheck, (_, _) => healthCheck);
        
        _logger.LogDebug("📝 Registered health check: {CheckName}", name);
    }
}
