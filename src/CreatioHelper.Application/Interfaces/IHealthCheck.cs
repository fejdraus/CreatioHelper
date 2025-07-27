using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Результат проверки здоровья компонента
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
/// Контекст для выполнения проверки здоровья
/// </summary>
public class HealthCheckContext
{
    public string Name { get; init; } = string.Empty;
    public Dictionary<string, object> Properties { get; init; } = new();
}

/// <summary>
/// Интерфейс для проверки здоровья компонентов
/// </summary>
public interface IHealthCheck
{
    Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Сервис для выполнения всех health checks
/// </summary>
public interface IHealthCheckService
{
    Task<Dictionary<string, HealthCheckResult>> CheckAllAsync(CancellationToken cancellationToken = default);
    Task<HealthCheckResult> CheckAsync(string checkName, CancellationToken cancellationToken = default);
    void RegisterHealthCheck(string name, IHealthCheck healthCheck);
}
