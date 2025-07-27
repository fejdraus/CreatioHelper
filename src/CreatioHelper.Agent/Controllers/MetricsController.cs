using CreatioHelper.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace CreatioHelper.Agent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MetricsController : ControllerBase
{
    private readonly IMetricsService _metrics;
    private readonly ILogger<MetricsController> _logger;

    public MetricsController(
        IMetricsService metrics,
        ILogger<MetricsController> logger)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Получить все метрики производительности
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<Dictionary<string, object>>> GetMetrics()
    {
        try
        {
            var metrics = await _metrics.GetMetricsAsync();
            return Ok(metrics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get metrics");
            return StatusCode(500, new { error = "Failed to get metrics", message = ex.Message });
        }
    }

    /// <summary>
    /// Получить сводку производительности
    /// </summary>
    [HttpGet("summary")]
    public async Task<ActionResult<object>> GetPerformanceSummary()
    {
        try
        {
            var allMetrics = await _metrics.GetMetricsAsync();
            
            var counters = (Dictionary<string, long>)allMetrics["counters"];
            var durations = (Dictionary<string, object>)allMetrics["durations"];
            var system = allMetrics["system"];

            // Вычисляем основные показатели
            var totalRequests = counters.Where(c => c.Key.EndsWith("_success") || c.Key.EndsWith("_error"))
                                      .Sum(c => c.Value);
            
            var errorRequests = counters.Where(c => c.Key.EndsWith("_error"))
                                      .Sum(c => c.Value);

            var errorRate = totalRequests > 0 ? (double)errorRequests / totalRequests * 100 : 0;

            // Находим самые медленные операции
            var slowestOperations = durations
                .Where(d => d.Value is not null)
                .Select(d => new
                {
                    operation = d.Key,
                    avg_duration = GetPropertyValue(d.Value, "avg"),
                    p95_duration = GetPropertyValue(d.Value, "p95"),
                    count = GetPropertyValue(d.Value, "count")
                })
                .Where(x => x.avg_duration > 0)
                .OrderByDescending(x => x.avg_duration)
                .Take(5)
                .ToList();

            var summary = new
            {
                timestamp = DateTimeOffset.UtcNow,
                performance = new
                {
                    total_requests = totalRequests,
                    error_requests = errorRequests,
                    error_rate_percent = Math.Round(errorRate, 2),
                    slowest_operations = slowestOperations
                },
                system,
                cache = new
                {
                    hit_count = counters.GetValueOrDefault("cache_hit", 0),
                    miss_count = counters.GetValueOrDefault("cache_miss", 0),
                    hit_rate_percent = CalculateCacheHitRate(counters)
                }
            };

            return Ok(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get performance summary");
            return StatusCode(500, new { error = "Failed to get performance summary", message = ex.Message });
        }
    }

    /// <summary>
    /// Получить метрики по конкретной операции
    /// </summary>
    [HttpGet("operation/{operationName}")]
    public async Task<ActionResult<object>> GetOperationMetrics(string operationName)
    {
        try
        {
            var allMetrics = await _metrics.GetMetricsAsync();
            var counters = (Dictionary<string, long>)allMetrics["counters"];
            var durations = (Dictionary<string, object>)allMetrics["durations"];

            // Фильтруем метрики по операции
            var operationCounters = counters
                .Where(c => c.Key.StartsWith(operationName))
                .ToDictionary(c => c.Key, c => c.Value);

            var operationDurations = durations
                .Where(d => d.Key.StartsWith(operationName))
                .ToDictionary(d => d.Key, d => d.Value);

            if (!operationCounters.Any() && !operationDurations.Any())
            {
                return NotFound(new { message = $"No metrics found for operation: {operationName}" });
            }

            var result = new
            {
                operation = operationName,
                counters = operationCounters,
                durations = operationDurations,
                summary = new
                {
                    success_count = operationCounters.GetValueOrDefault($"{operationName}_success", 0),
                    error_count = operationCounters.GetValueOrDefault($"{operationName}_error", 0),
                    avg_duration = operationDurations.ContainsKey(operationName) 
                        ? GetPropertyValue(operationDurations[operationName], "avg")
                        : 0,
                    p95_duration = operationDurations.ContainsKey(operationName)
                        ? GetPropertyValue(operationDurations[operationName], "p95")
                        : 0
                }
            };

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get operation metrics for {OperationName}", operationName);
            return StatusCode(500, new { error = "Failed to get operation metrics", message = ex.Message });
        }
    }

    /// <summary>
    /// Получить health check статус
    /// </summary>
    [HttpGet("health")]
    public async Task<ActionResult<object>> GetHealthStatus()
    {
        try
        {
            var allMetrics = await _metrics.GetMetricsAsync();
            var counters = (Dictionary<string, long>)allMetrics["counters"];
            var system = allMetrics["system"];

            // Простые health checks
            var memoryMb = GetPropertyValue(system, "memory_mb");
            var isMemoryHealthy = memoryMb < 512; // Менее 512 MB

            var totalErrors = counters.Where(c => c.Key.EndsWith("_error")).Sum(c => c.Value);
            var totalSuccess = counters.Where(c => c.Key.EndsWith("_success")).Sum(c => c.Value);
            var totalOperations = totalErrors + totalSuccess;
            
            var errorRate = totalOperations > 0 ? (double)totalErrors / totalOperations : 0;
            var isErrorRateHealthy = errorRate < 0.05; // Менее 5% ошибок

            var overallHealth = isMemoryHealthy && isErrorRateHealthy ? "Healthy" : "Unhealthy";

            var health = new
            {
                status = overallHealth,
                timestamp = DateTimeOffset.UtcNow,
                checks = new
                {
                    memory = new { healthy = isMemoryHealthy, value_mb = memoryMb },
                    error_rate = new { healthy = isErrorRateHealthy, value_percent = Math.Round(errorRate * 100, 2) }
                },
                details = new
                {
                    total_operations = totalOperations,
                    error_count = totalErrors,
                    success_count = totalSuccess
                }
            };

            return Ok(health);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get health status");
            return StatusCode(500, new { error = "Failed to get health status", message = ex.Message });
        }
    }

    private static double CalculateCacheHitRate(Dictionary<string, long> counters)
    {
        var hits = counters.GetValueOrDefault("cache_hit", 0);
        var misses = counters.GetValueOrDefault("cache_miss", 0);
        var total = hits + misses;
        
        return total > 0 ? Math.Round((double)hits / total * 100, 2) : 0;
    }

    private static double GetPropertyValue(object obj, string propertyName)
    {
        if (obj == null) return 0;
        
        var property = obj.GetType().GetProperty(propertyName);
        if (property == null) return 0;
        
        var value = property.GetValue(obj);
        return Convert.ToDouble(value ?? 0);
    }
}
