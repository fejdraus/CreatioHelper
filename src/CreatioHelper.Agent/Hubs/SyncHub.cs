using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Contracts.Responses;

namespace CreatioHelper.Agent.Hubs;

/// <summary>
/// SignalR hub for real-time sync events and monitoring
/// Based on Syncthing's event streaming API
/// </summary>
[Authorize]
public class SyncHub : Hub
{
    private readonly ILogger<SyncHub> _logger;
    private readonly ISyncEngine _syncEngine;

    public SyncHub(ILogger<SyncHub> logger, ISyncEngine syncEngine)
    {
        _logger = logger;
        _syncEngine = syncEngine;
    }

    public async Task JoinGroup(string groupName)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        _logger.LogDebug("Client {ConnectionId} joined group {GroupName}", Context.ConnectionId, groupName);
    }

    public async Task LeaveGroup(string groupName)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        _logger.LogDebug("Client {ConnectionId} left group {GroupName}", Context.ConnectionId, groupName);
    }

    public async Task<SyncSystemStatus> GetStatus()
    {
        try
        {
            var statistics = await _syncEngine.GetStatisticsAsync();
            var devices = await _syncEngine.GetDevicesAsync();

            return new SyncSystemStatus
            {
                Uptime = statistics.Uptime,
                ConnectedDevices = statistics.ConnectedDevices,
                TotalDevices = statistics.TotalDevices,
                SyncedFolders = statistics.SyncedFolders,
                TotalFolders = statistics.TotalFolders,
                TotalBytesIn = statistics.TotalBytesIn,
                TotalBytesOut = statistics.TotalBytesOut,
                IsOnline = devices.Any(d => d.IsConnected)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting sync status for SignalR client");
            throw new HubException("Failed to get sync status");
        }
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogDebug("Client {ConnectionId} connected to SyncHub", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogDebug("Client {ConnectionId} disconnected from SyncHub", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}

