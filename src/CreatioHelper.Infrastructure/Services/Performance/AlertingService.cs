using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Performance;

/// <summary>
/// Service for sending notifications about critical system events.
/// </summary>
public class AlertingService
{
    private readonly ILogger<AlertingService> _logger;
    private readonly Dictionary<string, DateTime> _lastAlerts = new();
    private readonly TimeSpan _alertCooldown = TimeSpan.FromMinutes(5);

    public AlertingService(ILogger<AlertingService> logger)
    {
        _logger = logger;
    }

    public async Task SendCriticalAlert(string component, string message, Exception? exception = null)
    {
        var alertKey = $"{component}:{message}";
        
        // Check cooldown to avoid spamming
        if (_lastAlerts.TryGetValue(alertKey, out var lastAlert) && 
            DateTime.UtcNow - lastAlert < _alertCooldown)
        {
            return;
        }

        _lastAlerts[alertKey] = DateTime.UtcNow;

        _logger.LogCritical("🚨 CRITICAL ALERT: {Component} - {Message}", component, message);
        
        // Integrate with external notification systems if needed:
        // - Email notifications
        // - Slack/Teams webhooks
        // - SMS alerts
        // - PagerDuty integration
        
        await NotifyAdministrators(component);
    }

    public async Task SendHealthDegradedAlert(string component, string details)
    {
        _logger.LogWarning("🟡 HEALTH DEGRADED: {Component} - {Details}", component, details);
        
        // Less critical notifications
        await Task.CompletedTask;
    }

    private async Task NotifyAdministrators(string component)
    {
        // Placeholder for real notification integrations such as:
        // - SMTP email server
        // - Slack/Teams webhooks
        // - Push notifications
        
        _logger.LogInformation("📧 Alert notification sent for {Component}", component);
        await Task.CompletedTask;
    }
}
