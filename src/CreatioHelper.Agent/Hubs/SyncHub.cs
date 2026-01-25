using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Authorization;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Contracts.Responses;
using CreatioHelper.Agent.Authorization;
using CreatioHelper.Infrastructure.Services.Sync;

namespace CreatioHelper.Agent.Hubs;

/// <summary>
/// SignalR hub for real-time sync events and monitoring
/// Based on Syncthing's event streaming API
/// </summary>
[Authorize(Roles = Roles.ReadRoles)]
public class SyncHub : Hub
{
    private readonly ILogger<SyncHub> _logger;
    private readonly ISyncEngine _syncEngine;
    private readonly SyncEventBroadcaster _eventBroadcaster;

    public SyncHub(
        ILogger<SyncHub> logger,
        ISyncEngine syncEngine,
        SyncEventBroadcaster eventBroadcaster)
    {
        _logger = logger;
        _syncEngine = syncEngine;
        _eventBroadcaster = eventBroadcaster;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("🔥 SyncHub client CONNECTED: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("❌ SyncHub client DISCONNECTED: {ConnectionId}, Reason: {Exception}",
            Context.ConnectionId, exception?.Message ?? "Normal disconnect");
        await base.OnDisconnectedAsync(exception);
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

    /// <summary>
    /// Subscribe to folder-specific events
    /// </summary>
    public async Task SubscribeToFolder(string folderId)
    {
        var groupName = $"folder:{folderId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        await Groups.AddToGroupAsync(Context.ConnectionId, "sync-events");
        _logger.LogInformation("📁 Connection {ConnectionId} subscribed to folder {FolderId}",
            Context.ConnectionId, folderId);
    }

    /// <summary>
    /// Unsubscribe from folder-specific events
    /// </summary>
    public async Task UnsubscribeFromFolder(string folderId)
    {
        var groupName = $"folder:{folderId}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Connection {ConnectionId} unsubscribed from folder {FolderId}",
            Context.ConnectionId, folderId);
    }

    /// <summary>
    /// Subscribe to device-specific events
    /// </summary>
    public async Task SubscribeToDevice(string deviceId)
    {
        var groupName = $"device:{deviceId}";
        await Groups.AddToGroupAsync(Context.ConnectionId, groupName);
        await Groups.AddToGroupAsync(Context.ConnectionId, "sync-events");
        _logger.LogInformation("📱 Connection {ConnectionId} subscribed to device {DeviceId}",
            Context.ConnectionId, deviceId);
    }

    /// <summary>
    /// Unsubscribe from device-specific events
    /// </summary>
    public async Task UnsubscribeFromDevice(string deviceId)
    {
        var groupName = $"device:{deviceId}";
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, groupName);
        _logger.LogInformation("Connection {ConnectionId} unsubscribed from device {DeviceId}",
            Context.ConnectionId, deviceId);
    }

    /// <summary>
    /// Subscribe to all sync events
    /// </summary>
    public async Task SubscribeToAllEvents()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "sync-events");
        _logger.LogInformation("🔔 Connection {ConnectionId} subscribed to all sync events",
            Context.ConnectionId);
    }

    /// <summary>
    /// Unsubscribe from all sync events
    /// </summary>
    public async Task UnsubscribeFromAllEvents()
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "sync-events");
        _logger.LogInformation("Connection {ConnectionId} unsubscribed from all sync events",
            Context.ConnectionId);
    }

    /// <summary>
    /// Get current folder status
    /// </summary>
    public async Task<SyncFolderDto?> GetFolderStatus(string folderId)
    {
        try
        {
            var folder = await _syncEngine.GetFolderAsync(folderId);
            if (folder == null)
            {
                _logger.LogWarning("Folder not found: {FolderId}", folderId);
                return null;
            }

            var status = await _syncEngine.GetSyncStatusAsync(folderId);

            return new SyncFolderDto
            {
                FolderId = folder.Id,
                Label = folder.Label,
                Path = folder.Path,
                Type = folder.Type,
                IsPaused = folder.Paused,
                State = status.State.ToString(),
                GlobalBytes = status.GlobalBytes,
                LocalBytes = status.LocalBytes,
                GlobalFiles = status.GlobalFiles,
                LocalFiles = status.LocalFiles,
                LastScan = status.LastScan,
                LastSync = status.LastSync,
                DeviceIds = folder.Devices.ToList()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting folder status for {FolderId}", folderId);
            throw new HubException($"Failed to get folder status: {ex.Message}");
        }
    }

    /// <summary>
    /// Get current device status
    /// </summary>
    public async Task<SyncDeviceDto?> GetDeviceStatus(string deviceId)
    {
        try
        {
            var devices = await _syncEngine.GetDevicesAsync();
            var device = devices.FirstOrDefault(d => d.DeviceId == deviceId);

            if (device == null)
            {
                _logger.LogWarning("Device not found: {DeviceId}", deviceId);
                return null;
            }

            return new SyncDeviceDto
            {
                DeviceId = device.DeviceId,
                Name = device.DeviceName,
                IsConnected = device.IsConnected,
                LastSeen = device.LastSeen ?? DateTime.MinValue,
                Status = device.IsConnected ? "Connected" : "Disconnected",
                IsPaused = device.Paused,
                Addresses = device.Addresses
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting device status for {DeviceId}", deviceId);
            throw new HubException($"Failed to get device status: {ex.Message}");
        }
    }

    /// <summary>
    /// Get overall system status
    /// </summary>
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

    /// <summary>
    /// Get recent events since a specific event ID
    /// </summary>
    public IEnumerable<SyncEventDto> GetRecentEvents(long sinceEventId = 0, int limit = 100)
    {
        try
        {
            var events = _eventBroadcaster.GetEventsSince(sinceEventId, limit);

            return events.Select(e => new SyncEventDto
            {
                Type = e.Type.ToString(),
                Timestamp = e.Time,
                FolderId = e.Data.TryGetValue("folderId", out var folderId) ? folderId?.ToString() : null,
                DeviceId = e.Data.TryGetValue("deviceId", out var deviceId) ? deviceId?.ToString() : null,
                Message = e.Data.TryGetValue("error", out var error) ? error?.ToString() : null,
                Data = e.Data.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value ?? (object)"null")
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting recent events");
            throw new HubException($"Failed to get recent events: {ex.Message}");
        }
    }

    /// <summary>
    /// Get the last event ID for polling
    /// </summary>
    public long GetLastEventId()
    {
        return _eventBroadcaster.GetLastEventId();
    }
}

