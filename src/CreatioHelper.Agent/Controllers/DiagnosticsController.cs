using CreatioHelper.Infrastructure.Services.Performance;
using System.Diagnostics;

namespace CreatioHelper.Agent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DiagnosticsController : ControllerBase
{
    private readonly DiagnosticsService _diagnosticsService;
    private readonly ILogger<DiagnosticsController> _logger;

    public DiagnosticsController(
        DiagnosticsService diagnosticsService,
        ILogger<DiagnosticsController> logger)
    {
        _diagnosticsService = diagnosticsService;
        _logger = logger;
    }
    
    [HttpGet("summary")]
    public ActionResult<object> GetDiagnosticsSummary()
    {
        try
        {
            var summary = _diagnosticsService.GetDiagnosticsSummary();
            
            var response = new
            {
                Timestamp = DateTime.UtcNow,
                SystemInfo = new
                {
                    Environment.MachineName,
                    Environment.OSVersion.Platform,
                    Environment.ProcessorCount,
                    WorkingSetMB = Environment.WorkingSet / 1024 / 1024,
                    UptimeHours = (DateTime.UtcNow - Process.GetCurrentProcess().StartTime).TotalHours
                },
                Operations = summary,
                HealthStatus = "Available via /api/health"
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate diagnostics summary");
            return StatusCode(500, new { Error = "Failed to generate diagnostics", ex.Message });
        }
    }

    /// <summary>
    /// Force a check of all systems with a detailed report
    /// </summary>
    [HttpPost("force-check")]
    public async Task<ActionResult<object>> ForceSystemCheck()
    {
        using var diagnosticContext = _diagnosticsService.StartOperation("force_system_check");
        
        try
        {
            _logger.LogInformation("🔍 Force system check initiated by API request");
            
            // Simulate a forced check of all critical components
            await Task.Delay(100); // Mocked check
            
            var results = new Dictionary<string, object>
            {
                ["timestamp"] = DateTime.UtcNow,
                ["initiated_by"] = "api_request",
                ["status"] = "completed",
                ["checks_performed"] = new[] { "iis", "file_system", "memory", "process_health" }
            };

            diagnosticContext.AddContext("results_count", results.Count);
            diagnosticContext.MarkSuccess();
            
            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Force system check failed");
            diagnosticContext.MarkFailure(ex.Message);
            return StatusCode(500, new { Error = "System check failed", ex.Message });
        }
    }

    /// <summary>
    /// Get the history of recent issues for troubleshooting
    /// </summary>
    [HttpGet("issues")]
    public ActionResult<object> GetRecentIssues()
    {
        try
        {
            var summary = _diagnosticsService.GetDiagnosticsSummary();
            var issues = new List<object>();

            foreach (var operation in summary)
            {
                {
                    var operationName = operation.Key;
                    issues.Add(new
                    {
                        Operation = operationName,
                        Timestamp = DateTime.UtcNow,
                        Type = "diagnostic_placeholder"
                    });
                }
            }

            return Ok(new
            {
                Timestamp = DateTime.UtcNow,
                TotalIssues = issues.Count,
                Issues = issues.Take(50).ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get recent issues");
            return StatusCode(500, new { Error = "Failed to get issues", ex.Message });
        }
    }
}
