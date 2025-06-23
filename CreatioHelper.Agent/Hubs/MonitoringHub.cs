using Microsoft.AspNetCore.SignalR;
using CreatioHelper.Agent.Services;

namespace CreatioHelper.Agent.Hubs;

public class MonitoringHub : Hub
{
    private readonly ILogger<MonitoringHub> _logger;
    private readonly MonitoringService _monitoringService;

    public MonitoringHub(ILogger<MonitoringHub> logger, MonitoringService monitoringService)
    {
        _logger = logger;
        _monitoringService = monitoringService;
    }

    public async Task JoinGroup(string groupName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Connection {ConnectionId} joined group {GroupName}", 
            Context.ConnectionId, groupName);
    }

    public async Task LeaveGroup(string groupName)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Connection {ConnectionId} left group {GroupName}", 
            Context.ConnectionId, groupName);
    }

    public async Task StartMonitoringServer(string siteName, string? poolName = null)
    {
        _monitoringService.AddServerToMonitoring(siteName, poolName);
        await Groups.AddToGroupAsync(Context.ConnectionId, "monitoring");
        _logger.LogInformation("Started monitoring server {SiteName} for connection {ConnectionId}", 
            siteName, Context.ConnectionId);
    }

    public async Task StopMonitoringServer(string siteName)
    {
        _monitoringService.RemoveServerFromMonitoring(siteName);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "monitoring");
        _logger.LogInformation("Stopped monitoring server {SiteName} for connection {ConnectionId}", 
            siteName, Context.ConnectionId);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("Connection {ConnectionId} disconnected", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}