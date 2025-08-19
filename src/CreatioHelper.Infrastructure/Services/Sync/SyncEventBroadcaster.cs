using Microsoft.Extensions.Logging;
using CreatioHelper.Contracts.Responses;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Service for broadcasting sync events to SignalR clients
/// </summary>
public class SyncEventBroadcaster
{
    private readonly ILogger<SyncEventBroadcaster> _logger;

    public SyncEventBroadcaster(ILogger<SyncEventBroadcaster> logger)
    {
        _logger = logger;
    }

    public async Task BroadcastDeviceConnectedAsync(string deviceId, string deviceName)
    {
        _logger.LogInformation("Device {DeviceId} ({DeviceName}) connected", deviceId, deviceName);
        await Task.CompletedTask;
    }

    public async Task BroadcastDeviceDisconnectedAsync(string deviceId)
    {
        _logger.LogInformation("Device {DeviceId} disconnected", deviceId);
        await Task.CompletedTask;
    }

    public async Task BroadcastFolderSyncedAsync(string folderId, int filesTransferred, long bytesTransferred)
    {
        _logger.LogInformation("Folder {FolderId} synced: {FilesTransferred} files, {BytesTransferred} bytes", 
            folderId, filesTransferred, bytesTransferred);
        await Task.CompletedTask;
    }

    public async Task BroadcastConflictDetectedAsync(string folderId, string filePath)
    {
        _logger.LogWarning("Conflict detected in folder {FolderId}, file {FilePath}", folderId, filePath);
        await Task.CompletedTask;
    }

    public async Task BroadcastSyncErrorAsync(string folderId, string error, string? deviceId = null)
    {
        _logger.LogError("Sync error in folder {FolderId}: {Error} (Device: {DeviceId})", folderId, error, deviceId);
        await Task.CompletedTask;
    }

    public async Task BroadcastStatusUpdateAsync(SyncSystemStatus status)
    {
        _logger.LogDebug("Status update: {ConnectedDevices}/{TotalDevices} devices, {SyncedFolders}/{TotalFolders} folders", 
            status.ConnectedDevices, status.TotalDevices, status.SyncedFolders, status.TotalFolders);
        await Task.CompletedTask;
    }
}