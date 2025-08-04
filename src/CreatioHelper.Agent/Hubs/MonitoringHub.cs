using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using CreatioHelper.Agent.Services;
using CreatioHelper.Agent.Authorization;

namespace CreatioHelper.Agent.Hubs;

[Authorize(Roles = Roles.ReadRoles)]
public class MonitoringHub : Hub
{
    private readonly ILogger<MonitoringHub> _logger;
    private readonly MonitoringService _monitoringService;

    public MonitoringHub(ILogger<MonitoringHub> logger, MonitoringService monitoringService)
    {
        _logger = logger;
        _monitoringService = monitoringService;
    }
    
    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("🔥 WebSocket client CONNECTED: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public async Task JoinGroup(string groupName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("➕ Connection {ConnectionId} joined group {GroupName}", 
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
        _logger.LogInformation("❌ WebSocket client DISCONNECTED: {ConnectionId}, Reason: {Exception}", 
            Context.ConnectionId, exception?.Message ?? "Normal disconnect");
        await base.OnDisconnectedAsync(exception);
    }
}