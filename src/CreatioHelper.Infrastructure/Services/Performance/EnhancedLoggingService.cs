using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace CreatioHelper.Infrastructure.Services.Performance;

/// <summary>
/// Расширенный сервис логирования с категоризацией событий для поддержки
/// </summary>
public class EnhancedLoggingService
{
    private readonly ILogger<EnhancedLoggingService> _logger;

    public EnhancedLoggingService(ILogger<EnhancedLoggingService> logger)
    {
        _logger = logger;
    }

    public void LogCriticalSystemError(string component, Exception exception, Dictionary<string, object>? context = null)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["Component"] = component,
            ["ErrorType"] = "Critical",
            ["Context"] = context ?? new Dictionary<string, object>()
        });

        _logger.LogCritical(exception, "🔥 CRITICAL: {Component} system failure", component);
    }

    public void LogOperationWarning(string operation, string warning, TimeSpan? duration = null)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["Operation"] = operation,
            ["Duration"] = duration?.TotalMilliseconds ?? 0
        });

        _logger.LogWarning("⚠️ {Operation} completed with warning: {Warning}", operation, warning);
    }

    public void LogPerformanceIssue(string operation, TimeSpan actualDuration, TimeSpan expectedDuration)
    {
        _logger.LogWarning("🐌 Performance issue in {Operation}: {ActualMs}ms (expected <{ExpectedMs}ms)", 
            operation, actualDuration.TotalMilliseconds, expectedDuration.TotalMilliseconds);
    }

    public void LogSuccessfulOperation(string operation, TimeSpan duration, Dictionary<string, object>? metrics = null)
    {
        _logger.LogInformation("✅ {Operation} completed successfully in {DurationMs}ms {Metrics}", 
            operation, duration.TotalMilliseconds, metrics != null ? $"- {string.Join(", ", metrics.Select(kv => $"{kv.Key}:{kv.Value}"))}" : "");
    }
}
