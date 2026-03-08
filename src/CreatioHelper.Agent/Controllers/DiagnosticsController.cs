using CreatioHelper.Infrastructure.Services.Performance;
using CreatioHelper.Agent.Authorization;
using Microsoft.AspNetCore.Authorization;
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
    [Authorize(Roles = Roles.MonitorRoles)]
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
            return StatusCode(500, new { Error = "Failed to generate diagnostics" });
        }
    }

    /// <summary>
    /// Force a check of all systems with a detailed report
    /// </summary>
    [HttpPost("force-check")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<ActionResult<object>> ForceSystemCheck()
    {
        using var diagnosticContext = _diagnosticsService.StartOperation("force_system_check");

        try
        {
            _logger.LogInformation("Force system check initiated by API request");

            var checks = new Dictionary<string, object>();

            // Check memory
            var process = Process.GetCurrentProcess();
            var memoryMb = process.WorkingSet64 / 1024.0 / 1024.0;
            checks["memory"] = new
            {
                status = memoryMb < 2048 ? "ok" : "warning",
                workingSetMb = Math.Round(memoryMb, 1),
                gcMemory = GC.GetTotalMemory(false) / 1024.0 / 1024.0
            };

            // Check disk space
            try
            {
                var appDir = AppContext.BaseDirectory;
                var driveInfo = new System.IO.DriveInfo(Path.GetPathRoot(appDir)!);
                var freeGb = driveInfo.AvailableFreeSpace / (1024.0 * 1024 * 1024);
                checks["disk"] = new
                {
                    status = freeGb > 1 ? "ok" : "warning",
                    freeSpaceGb = Math.Round(freeGb, 2),
                    drive = driveInfo.Name
                };
            }
            catch
            {
                checks["disk"] = new { status = "unknown", error = "Could not check disk space" };
            }

            // Check process health
            checks["process"] = new
            {
                status = "ok",
                threads = process.Threads.Count,
                handles = process.HandleCount,
                uptimeMinutes = (DateTime.Now - process.StartTime).TotalMinutes
            };

            // Check thread pool
            System.Threading.ThreadPool.GetAvailableThreads(out var workerThreads, out var completionPortThreads);
            checks["threadPool"] = new
            {
                status = "ok",
                availableWorkerThreads = workerThreads,
                availableCompletionPortThreads = completionPortThreads
            };

            var results = new Dictionary<string, object>
            {
                ["timestamp"] = DateTime.UtcNow,
                ["initiated_by"] = "api_request",
                ["status"] = "completed",
                ["checks"] = checks
            };

            diagnosticContext.AddContext("results_count", checks.Count);
            diagnosticContext.MarkSuccess();

            return Ok(results);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Force system check failed");
            diagnosticContext.MarkFailure(ex.Message);
            return StatusCode(500, new { Error = "System check failed" });
        }
    }

    /// <summary>
    /// Get the history of recent issues for troubleshooting
    /// </summary>
    [HttpGet("issues")]
    [Authorize(Roles = Roles.MonitorRoles)]
    public ActionResult<object> GetRecentIssues()
    {
        try
        {
            var summary = _diagnosticsService.GetDiagnosticsSummary();
            var issues = new List<object>();

            foreach (var operation in summary)
            {
                var operationName = operation.Key;
                var operationData = operation.Value as IDictionary<string, object>;
                var failureCount = 0L;
                var avgDuration = 0.0;

                if (operationData != null)
                {
                    if (operationData.TryGetValue("FailureCount", out var fc) && fc is long fcl) failureCount = fcl;
                    if (operationData.TryGetValue("AverageDurationMs", out var ad) && ad is double add) avgDuration = add;
                }

                var issueType = failureCount > 0 ? "failure"
                    : avgDuration > 5000 ? "slow_operation"
                    : "info";

                issues.Add(new
                {
                    Operation = operationName,
                    Timestamp = DateTime.UtcNow,
                    Type = issueType,
                    FailureCount = failureCount,
                    AverageDurationMs = avgDuration
                });
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
            return StatusCode(500, new { Error = "Failed to get issues" });
        }
    }
}
