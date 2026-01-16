using CreatioHelper.Infrastructure.Services.Performance;
using Microsoft.AspNetCore.Authorization;

namespace CreatioHelper.Agent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HealthController : ControllerBase
{
    private readonly IHealthCheckService _healthCheckService;
    private readonly CreatioHelperHealthCheck _mainHealthCheck;
    private readonly ILogger<HealthController> _logger;

    public HealthController(
        IHealthCheckService healthCheckService,
        CreatioHelperHealthCheck mainHealthCheck,
        ILogger<HealthController> logger)
    {
        _healthCheckService = healthCheckService;
        _mainHealthCheck = mainHealthCheck;
        _logger = logger;
    }

    /// <summary>
    /// Get overall system health status
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<object>> GetHealth()
    {
        try
        {
            var context = new HealthCheckContext { Name = "system_health" };
            var result = await _mainHealthCheck.CheckHealthAsync(context);

            var response = new
            {
                Status = result.IsHealthy ? "Healthy" : "Unhealthy",
                result.Message,
                Duration = $"{result.Duration.TotalMilliseconds:F1}ms",
                Timestamp = DateTime.UtcNow,
                result.Data
            };

            if (result.IsHealthy)
            {
                return Ok(response);
            }
            else
            {
                return StatusCode(503, response); // Service Unavailable
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed with exception");
            
            return StatusCode(500, new
            {
                Status = "Error",
                Message = "Health check system error",
                Timestamp = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Get detailed information about all components
    /// </summary>
    [HttpGet("detailed")]
    [Authorize]
    public async Task<ActionResult<object>> GetDetailedHealth()
    {
        try
        {
            var allResults = await _healthCheckService.CheckAllAsync();
            
            var healthyCount = allResults.Count(r => r.Value.IsHealthy);
            var unhealthyCount = allResults.Count - healthyCount;
            
            var response = new
            {
                OverallStatus = unhealthyCount == 0 ? "Healthy" : "Degraded",
                TotalComponents = allResults.Count,
                HealthyComponents = healthyCount,
                UnhealthyComponents = unhealthyCount,
                Timestamp = DateTime.UtcNow,
                Components = allResults.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new
                    {
                        Status = kvp.Value.IsHealthy ? "Healthy" : "Unhealthy",
                        kvp.Value.Message,
                        Duration = $"{kvp.Value.Duration.TotalMilliseconds:F1}ms",
                        kvp.Value.Data
                    })
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Detailed health check failed");
            
            return StatusCode(500, new
            {
                Status = "Error",
                Message = "Health check system error",
                Timestamp = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Check the health of a specific component
    /// </summary>
    [HttpGet("component/{componentName}")]
    [Authorize]
    public async Task<ActionResult<object>> GetComponentHealth(string componentName)
    {
        try
        {
            var result = await _healthCheckService.CheckAsync(componentName);
            
            var response = new
            {
                Component = componentName,
                Status = result.IsHealthy ? "Healthy" : "Unhealthy",
                result.Message,
                Duration = $"{result.Duration.TotalMilliseconds:F1}ms",
                Timestamp = DateTime.UtcNow,
                result.Data
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Component health check failed for {ComponentName}", componentName);
            
            return StatusCode(500, new
            {
                Component = componentName,
                Status = "Error",
                Message = "Health check failed",
                Timestamp = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Simple availability check for monitoring
    /// </summary>
    [HttpGet("ping")]
    public ActionResult<object> Ping()
    {
        return Ok(new
        {
            Status = "OK",
            Message = "CreatioHelper Agent is running",
            Timestamp = DateTime.UtcNow,
            Version = typeof(Program).Assembly.GetName().Version?.ToString()
        });
    }
}
