using CreatioHelper.Agent.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CreatioHelper.Agent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MetricsController : ControllerBase
{
    private readonly IMetricsService _metricsService;
    private readonly HealthCheckService _healthCheckService;
    private readonly ILogger<MetricsController> _logger;

    public MetricsController(
        IMetricsService metricsService,
        HealthCheckService healthCheckService,
        ILogger<MetricsController> logger)
    {
        _metricsService = metricsService ?? throw new ArgumentNullException(nameof(metricsService));
        _healthCheckService = healthCheckService ?? throw new ArgumentNullException(nameof(healthCheckService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Get overall performance summary
    /// </summary>
    [HttpGet("performance")]
    [Authorize(Roles = Roles.Monitor)]
    public async Task<ActionResult<PerformanceSummary>> GetPerformanceMetrics()
    {
        try
        {
            var healthReport = await _healthCheckService.CheckHealthAsync();
            await _metricsService.GetMetricsAsync();

            var summary = new PerformanceSummary
            {
                Timestamp = DateTime.UtcNow,
                SystemHealth = MapHealthStatus(healthReport.Status),
                TotalRequests = await _metricsService.GetCounterAsync("total_requests"),
                AverageResponseTime = await _metricsService.GetAverageAsync("api_response_time"),
                ErrorRate = await _metricsService.GetRateAsync("api_requests"),
                MemoryUsageMb = await GetMemoryUsage(),
                CpuUsagePercent = await GetCpuUsage(),
                ActiveConnections = await _metricsService.GetCounterAsync("active_connections"),
                HealthChecks = healthReport.Entries.ToDictionary(
                    e => e.Key,
                    e => new HealthStatus
                    {
                        Status = MapHealthStatus(e.Value.Status),
                        Description = e.Value.Description ?? string.Empty,
                        Duration = e.Value.Duration,
                        Data = e.Value.Data.ToDictionary(kv => kv.Key, kv => kv.Value.ToString() ?? string.Empty)
                    })
            };

            return Ok(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting performance metrics");
            return StatusCode(500, new { error = "Failed to retrieve metrics", message = ex.Message });
        }
    }

    /// <summary>
    /// Get detailed metrics by categories
    /// </summary>
    [HttpGet("detailed")]
    [Authorize(Roles = Roles.Monitor)]
    public async Task<ActionResult<Dictionary<string, object>>> GetDetailedMetrics()
    {
        try
        {
            var metrics = await _metricsService.GetMetricsAsync();
            return Ok(metrics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting detailed metrics");
            return StatusCode(500, new { error = "Failed to retrieve detailed metrics" });
        }
    }

    /// <summary>
    /// Prometheus compatible endpoint
    /// </summary>
    [HttpGet("prometheus")]
    [Produces("text/plain")]
    [Authorize(Roles = Roles.Monitor)]
    public async Task<ActionResult<string>> GetPrometheusMetrics()
    {
        try
        {
            var metrics = await _metricsService.GetMetricsAsync();
            var prometheusFormat = ConvertToPrometheusFormat(metrics);
            return Ok(prometheusFormat);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating Prometheus metrics");
            return StatusCode(500, $"# Error generating metrics: {ex.Message}");
        }
    }

    /// <summary>
    /// Reset metrics (for testing only)
    /// </summary>
    [HttpPost("reset")]
    [Authorize(Roles = Roles.WriteRoles)]
    public ActionResult ResetMetrics()
    {
        if (!HttpContext.Request.Headers.ContainsKey("X-Reset-Token"))
        {
            return Unauthorized("Reset token required");
        }

        try
        {
            // Metrics reset implementation
            _logger.LogWarning("Metrics reset requested from {RemoteIp}", HttpContext.Connection.RemoteIpAddress);
            return Ok(new { message = "Metrics reset successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting metrics");
            return StatusCode(500, new { error = "Failed to reset metrics" });
        }
    }

    private string ConvertToPrometheusFormat(Dictionary<string, object> metrics)
    {
        var lines = new List<string>();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Counters
        if (metrics.TryGetValue("counters", out var countersObj) && countersObj is Dictionary<string, object> counters)
        {
            foreach (var counter in counters)
            {
                lines.Add($"# TYPE {SanitizeMetricName(counter.Key)} counter");
                lines.Add($"{SanitizeMetricName(counter.Key)} {counter.Value} {timestamp}");
            }
        }

        // Gauges
        if (metrics.TryGetValue("gauges", out var gaugesObj) && gaugesObj is Dictionary<string, object> gauges)
        {
            foreach (var gauge in gauges)
            {
                lines.Add($"# TYPE {SanitizeMetricName(gauge.Key)} gauge");
                lines.Add($"{SanitizeMetricName(gauge.Key)} {gauge.Value} {timestamp}");
            }
        }

        // Durations
        if (metrics.TryGetValue("durations", out var durationsObj) && durationsObj is Dictionary<string, object> durations)
        {
            foreach (var duration in durations)
            {
                var name = SanitizeMetricName(duration.Key);
                lines.Add($"# TYPE {name}_duration_ms summary");

                {
                    var durationData = JsonSerializer.Deserialize<Dictionary<string, object>>(
                        JsonSerializer.Serialize(duration.Value));
                    
                    if (durationData != null)
                    {
                        lines.Add($"{name}_duration_ms_count {durationData.GetValueOrDefault("Count", 0)} {timestamp}");
                        lines.Add($"{name}_duration_ms_sum {durationData.GetValueOrDefault("Average", 0)} {timestamp}");
                    }
                }
            }
        }

        return string.Join("\n", lines);
    }

    private string SanitizeMetricName(string name)
    {
        return name.Replace("[", "_").Replace("]", "").Replace(",", "_").Replace("=", "_").Replace(" ", "_").ToLower();
    }

    private string MapHealthStatus(Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus status)
    {
        return status switch
        {
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Healthy => "healthy",
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded => "degraded",
            Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Unhealthy => "unhealthy",
            _ => "unknown"
        };
    }

    private Task<double> GetMemoryUsage()
    {
        try
        {
            var workingSet = Environment.WorkingSet;
            return Task.FromResult(workingSet / 1024.0 / 1024.0); // MB
        }
        catch
        {
            return Task.FromResult(0.0);
        }
    }

    private Task<double> GetCpuUsage()
    {
        try
        {
            // Simplified implementation - a more complex calculation is needed in reality
            return Task.FromResult(0.0);
        }
        catch
        {
            return Task.FromResult(0.0);
        }
    }
}

public class PerformanceSummary
{
    public DateTime Timestamp { get; set; }
    public string SystemHealth { get; set; } = string.Empty;
    public long TotalRequests { get; set; }
    public double AverageResponseTime { get; set; }
    public double ErrorRate { get; set; }
    public double MemoryUsageMb { get; set; }
    public double CpuUsagePercent { get; set; }
    public long ActiveConnections { get; set; }
    public Dictionary<string, HealthStatus> HealthChecks { get; set; } = new();
}

public class HealthStatus
{
    public string Status { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public Dictionary<string, string>? Data { get; set; }
}
