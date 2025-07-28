using CreatioHelper.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Performance;

/// <summary>
/// Сервис уведомлений для критических событий системы
/// </summary>
public class AlertingService
{
    private readonly ILogger<AlertingService> _logger;
    private readonly Dictionary<string, DateTime> _lastAlerts = new();
    private readonly TimeSpan _alertCooldown = TimeSpan.FromMinutes(5); // Предотвращаем спам

    public AlertingService(ILogger<AlertingService> logger)
    {
        _logger = logger;
    }

    public async Task SendCriticalAlert(string component, string message, Exception? exception = null)
    {
        var alertKey = $"{component}:{message}";
        
        // Проверяем cooldown для предотвращения спама
        if (_lastAlerts.TryGetValue(alertKey, out var lastAlert) && 
            DateTime.UtcNow - lastAlert < _alertCooldown)
        {
            return;
        }

        _lastAlerts[alertKey] = DateTime.UtcNow;

        _logger.LogCritical("🚨 CRITICAL ALERT: {Component} - {Message}", component, message);
        
        // Здесь можно добавить интеграцию с внешними системами уведомлений:
        // - Email notifications
        // - Slack/Teams webhooks
        // - SMS alerts
        // - PagerDuty integration
        
        await NotifyAdministrators(component, message, exception);
    }

    public async Task SendHealthDegradedAlert(string component, string details)
    {
        _logger.LogWarning("🟡 HEALTH DEGRADED: {Component} - {Details}", component, details);
        
        // Менее критичные уведомления
        await Task.CompletedTask;
    }

    private async Task NotifyAdministrators(string component, string message, Exception? exception)
    {
        // Placeholder для интеграции с системами уведомлений
        // В реальном проекте здесь была бы интеграция с:
        // - SMTP сервер для email
        // - Webhook для Slack/Teams
        // - Push notifications
        
        _logger.LogInformation("📧 Alert notification sent for {Component}", component);
        await Task.CompletedTask;
    }
}
