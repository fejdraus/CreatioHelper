using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services.Performance;
using Microsoft.AspNetCore.Mvc;

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
    /// Получить общий статус здоровья системы
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
                Message = result.Message,
                Duration = $"{result.Duration.TotalMilliseconds:F1}ms",
                Timestamp = DateTime.UtcNow,
                Data = result.Data
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
                Error = ex.Message,
                Timestamp = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Получить детальную информацию о всех компонентах
    /// </summary>
    [HttpGet("detailed")]
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
                        Message = kvp.Value.Message,
                        Duration = $"{kvp.Value.Duration.TotalMilliseconds:F1}ms",
                        Data = kvp.Value.Data
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
                Error = ex.Message,
                Timestamp = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Проверить здоровье конкретного компонента
    /// </summary>
    [HttpGet("component/{componentName}")]
    public async Task<ActionResult<object>> GetComponentHealth(string componentName)
    {
        try
        {
            var result = await _healthCheckService.CheckAsync(componentName);
            
            var response = new
            {
                Component = componentName,
                Status = result.IsHealthy ? "Healthy" : "Unhealthy",
                Message = result.Message,
                Duration = $"{result.Duration.TotalMilliseconds:F1}ms",
                Timestamp = DateTime.UtcNow,
                Data = result.Data
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
                Message = $"Health check failed: {ex.Message}",
                Timestamp = DateTime.UtcNow
            });
        }
    }

    /// <summary>
    /// Простая проверка доступности для мониторинга
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
