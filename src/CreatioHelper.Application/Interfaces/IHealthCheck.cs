namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Result of a component health check.
/// </summary>
public class HealthCheckResult
{
    public bool IsHealthy { get; init; }
    public string Message { get; init; } = string.Empty;
    public Dictionary<string, object>? Data { get; init; }
    public TimeSpan Duration { get; init; }

    public static HealthCheckResult Healthy(string message = "Healthy", Dictionary<string, object>? data = null)
        => new() { IsHealthy = true, Message = message, Data = data };

    public static HealthCheckResult Unhealthy(string message, Dictionary<string, object>? data = null)
        => new() { IsHealthy = false, Message = message, Data = data };
}

/// <summary>
/// Context information for a health check.
/// </summary>
public class HealthCheckContext
{
    public string Name { get; init; } = string.Empty;
    public Dictionary<string, object> Properties { get; init; } = new();
}

/// <summary>
/// Interface for performing component health checks.
/// </summary>
public interface IHealthCheck
{
    Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Service that executes all registered health checks.
/// </summary>
public interface IHealthCheckService
{
    Task<Dictionary<string, HealthCheckResult>> CheckAllAsync(CancellationToken cancellationToken = default);
    Task<HealthCheckResult> CheckAsync(string checkName, CancellationToken cancellationToken = default);
    void RegisterHealthCheck(string name, IHealthCheck healthCheck);
}
