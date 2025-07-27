using CreatioHelper.Application.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace CreatioHelper.Agent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MetricsController : ControllerBase
{
    private readonly IMetricsService _metrics;
    private readonly ICacheService _cache;
    private readonly ILogger<MetricsController> _logger;

    public MetricsController(
        IMetricsService metrics,
        ICacheService cache,
        ILogger<MetricsController> logger)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Получить все метрики производительности
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetMetrics()
    {
        try
        {
            var metrics = await _metrics.GetMetricsAsync();
            return Ok(new
            {
                Timestamp = DateTime.UtcNow,
                ServerName = Environment.MachineName,
                Metrics = metrics
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving metrics");
            return StatusCode(500, new { Error = "Failed to retrieve metrics" });
        }
    }

    /// <summary>
    /// Получить краткую сводку производительности
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetPerformanceSummary()
    {
        try
        {
            var allMetrics = await _metrics.GetMetricsAsync();
            
            var summary = new
            {
                Timestamp = DateTime.UtcNow,
                ServerName = Environment.MachineName,
                Performance = new
                {
                    ServerStatusChecks = GetOperationSummary(allMetrics, "server_status_refresh"),
                    CacheEfficiency = GetCacheEfficiency(allMetrics),
                    ErrorRates = GetErrorRates(allMetrics),
                    SystemHealth = "Healthy" // Базовая проверка
                }
            };

            return Ok(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving performance summary");
            return StatusCode(500, new { Error = "Failed to retrieve performance summary" });
        }
    }

    /// <summary>
    /// Очистить кэш метрик (для отладки)
    /// </summary>
    [HttpPost("clear-cache")]
    public async Task<IActionResult> ClearCache()
    {
        try
        {
            await _cache.ClearAsync();
            _metrics.IncrementCounter("metrics_cache_cleared_manually");
            _logger.LogInformation("Metrics cache cleared manually");
            
            return Ok(new { Message = "Cache cleared successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing cache");
            return StatusCode(500, new { Error = "Failed to clear cache" });
        }
    }

    private static object? GetOperationSummary(Dictionary<string, object> metrics, string operationName)
    {
        if (!metrics.TryGetValue("durations", out var durationsObj) || 
            durationsObj is not Dictionary<string, object> durations)
            return null;

        var operationMetrics = durations
            .Where(kv => kv.Key.StartsWith(operationName))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (!operationMetrics.Any())
            return null;

        return new
        {
            Operations = operationMetrics.Count,
            Details = operationMetrics
        };
    }

    private static object GetCacheEfficiency(Dictionary<string, object> metrics)
    {
        if (!metrics.TryGetValue("counters", out var countersObj) || 
            countersObj is not Dictionary<string, object> counters)
            return new { HitRate = 0, Hits = 0, Misses = 0 };

        var hits = GetCounterValue(counters, "server_status_cache_hit");
        var misses = GetCounterValue(counters, "server_status_cache_miss");
        var total = hits + misses;

        return new
        {
            HitRate = total > 0 ? Math.Round((double)hits / total * 100, 2) : 0,
            Hits = hits,
            Misses = misses,
            Total = total
        };
    }

    private static object GetErrorRates(Dictionary<string, object> metrics)
    {
        if (!metrics.TryGetValue("counters", out var countersObj) || 
            countersObj is not Dictionary<string, object> counters)
            return new { ErrorCount = 0, ExceptionCount = 0 };

        var errors = GetCounterValue(counters, "server_status_error");
        var exceptions = GetCounterValue(counters, "server_status_exception");

        return new
        {
            ErrorCount = errors,
            ExceptionCount = exceptions,
            TotalIssues = errors + exceptions
        };
    }

    private static long GetCounterValue(Dictionary<string, object> counters, string prefix)
    {
        return counters
            .Where(kv => kv.Key.StartsWith(prefix))
            .Sum(kv => kv.Value is long value ? value : 0);
    }
}
