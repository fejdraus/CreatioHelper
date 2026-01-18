using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using CreatioHelper.Contracts.Responses;
using CreatioHelper.Infrastructure.Services.Sync.Events;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Service for broadcasting sync events to SignalR clients and the event queue.
/// Compatible with Syncthing's event system.
/// </summary>
public class SyncEventBroadcaster : IDisposable
{
    private readonly ILogger<SyncEventBroadcaster> _logger;
    private readonly SyncEventQueue _eventQueue;

    /// <summary>
    /// Gets the underlying event queue for subscriptions.
    /// </summary>
    public SyncEventQueue EventQueue => _eventQueue;

    public SyncEventBroadcaster(ILogger<SyncEventBroadcaster> logger, ILoggerFactory loggerFactory)
    {
        _logger = logger;
        _eventQueue = new SyncEventQueue(loggerFactory.CreateLogger<SyncEventQueue>());
    }

    /// <summary>
    /// Safely publishes an event with exception handling.
    /// </summary>
    private async Task SafePublishAsync(SyncEvent syncEvent)
    {
        try
        {
            await _eventQueue.PublishAsync(syncEvent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish event {EventType}", syncEvent.Type);
        }
    }

    public async Task BroadcastDeviceConnectedAsync(string deviceId, string deviceName, string? address = null, string? connectionType = null)
    {
        _logger.LogInformation("Device {DeviceId} ({DeviceName}) connected via {ConnectionType}", deviceId, deviceName, connectionType ?? "unknown");

        await SafePublishAsync(new SyncEvent
        {
            Type = SyncEventType.DeviceConnected,
            Data = new ConcurrentDictionary<string, object?>
            {
                ["deviceId"] = deviceId,
                ["deviceName"] = deviceName,
                ["address"] = address,
                ["connectionType"] = connectionType
            }
        });
    }

    public async Task BroadcastDeviceDisconnectedAsync(string deviceId, string? error = null)
    {
        _logger.LogInformation("Device {DeviceId} disconnected{Error}", deviceId, error != null ? $": {error}" : "");

        await SafePublishAsync(new SyncEvent
        {
            Type = SyncEventType.DeviceDisconnected,
            Data = new ConcurrentDictionary<string, object?>
            {
                ["deviceId"] = deviceId,
                ["error"] = error
            }
        });
    }

    public async Task BroadcastFolderSyncedAsync(string folderId, int filesTransferred, long bytesTransferred)
    {
        _logger.LogInformation("Folder {FolderId} synced: {FilesTransferred} files, {BytesTransferred} bytes",
            folderId, filesTransferred, bytesTransferred);

        await SafePublishAsync(new SyncEvent
        {
            Type = SyncEventType.FolderCompletion,
            Data = new ConcurrentDictionary<string, object?>
            {
                ["folderId"] = folderId,
                ["filesTransferred"] = filesTransferred,
                ["bytesTransferred"] = bytesTransferred,
                ["completion"] = 100.0
            }
        });
    }

    public async Task BroadcastFolderStateChangedAsync(string folderId, string state, string? prevState = null)
    {
        _logger.LogInformation("Folder {FolderId} state changed: {PrevState} -> {State}", folderId, prevState ?? "unknown", state);

        await SafePublishAsync(new SyncEvent
        {
            Type = SyncEventType.FolderStateChanged,
            Data = new ConcurrentDictionary<string, object?>
            {
                ["folderId"] = folderId,
                ["state"] = state,
                ["prevState"] = prevState
            }
        });
    }

    public async Task BroadcastFolderScanProgressAsync(string folderId, int current, int total, double rate)
    {
        await SafePublishAsync(new SyncEvent
        {
            Type = SyncEventType.FolderScanProgress,
            Data = new ConcurrentDictionary<string, object?>
            {
                ["folderId"] = folderId,
                ["current"] = current,
                ["total"] = total,
                ["rate"] = rate
            }
        });
    }

    public async Task BroadcastItemStartedAsync(string folderId, string item, string action, long size, string? fromDevice = null)
    {
        _logger.LogDebug("Item started: {FolderId}/{Item} ({Action})", folderId, item, action);

        await SafePublishAsync(new SyncEvent
        {
            Type = SyncEventType.ItemStarted,
            Data = new ConcurrentDictionary<string, object?>
            {
                ["folderId"] = folderId,
                ["item"] = item,
                ["action"] = action,
                ["size"] = size,
                ["fromDevice"] = fromDevice
            }
        });
    }

    public async Task BroadcastItemFinishedAsync(string folderId, string item, string action, string? error = null)
    {
        if (error != null)
        {
            _logger.LogWarning("Item finished with error: {FolderId}/{Item} ({Action}): {Error}", folderId, item, action, error);
        }
        else
        {
            _logger.LogDebug("Item finished: {FolderId}/{Item} ({Action})", folderId, item, action);
        }

        await SafePublishAsync(new SyncEvent
        {
            Type = SyncEventType.ItemFinished,
            Data = new ConcurrentDictionary<string, object?>
            {
                ["folderId"] = folderId,
                ["item"] = item,
                ["action"] = action,
                ["error"] = error
            }
        });
    }

    public async Task BroadcastDownloadProgressAsync(string folderId, string item, long bytesTotal, long bytesDone, double rate)
    {
        await SafePublishAsync(new SyncEvent
        {
            Type = SyncEventType.DownloadProgress,
            Data = new ConcurrentDictionary<string, object?>
            {
                ["folderId"] = folderId,
                ["item"] = item,
                ["bytesTotal"] = bytesTotal,
                ["bytesDone"] = bytesDone,
                ["rate"] = rate
            }
        });
    }

    public async Task BroadcastConflictDetectedAsync(string folderId, string filePath)
    {
        _logger.LogWarning("Conflict detected in folder {FolderId}, file {FilePath}", folderId, filePath);

        await SafePublishAsync(new SyncEvent
        {
            Type = SyncEventType.ConflictDetected,
            Data = new ConcurrentDictionary<string, object?>
            {
                ["folderId"] = folderId,
                ["item"] = filePath
            }
        });
    }

    public async Task BroadcastSyncErrorAsync(string folderId, string error, string? deviceId = null)
    {
        _logger.LogError("Sync error in folder {FolderId}: {Error} (Device: {DeviceId})", folderId, error, deviceId);

        await SafePublishAsync(new SyncEvent
        {
            Type = SyncEventType.SyncError,
            Data = new ConcurrentDictionary<string, object?>
            {
                ["folderId"] = folderId,
                ["error"] = error,
                ["deviceId"] = deviceId
            }
        });
    }

    public async Task BroadcastFolderErrorAsync(string folderId, string error)
    {
        _logger.LogError("Folder error {FolderId}: {Error}", folderId, error);

        await SafePublishAsync(new SyncEvent
        {
            Type = SyncEventType.FolderError,
            Data = new ConcurrentDictionary<string, object?>
            {
                ["folderId"] = folderId,
                ["error"] = error
            }
        });
    }

    public async Task BroadcastStatusUpdateAsync(SyncSystemStatus status)
    {
        _logger.LogDebug("Status update: {ConnectedDevices}/{TotalDevices} devices, {SyncedFolders}/{TotalFolders} folders",
            status.ConnectedDevices, status.TotalDevices, status.SyncedFolders, status.TotalFolders);

        await SafePublishAsync(new SyncEvent
        {
            Type = SyncEventType.StateChanged,
            Data = new ConcurrentDictionary<string, object?>
            {
                ["connectedDevices"] = status.ConnectedDevices,
                ["totalDevices"] = status.TotalDevices,
                ["syncedFolders"] = status.SyncedFolders,
                ["totalFolders"] = status.TotalFolders,
                ["totalBytesIn"] = status.TotalBytesIn,
                ["totalBytesOut"] = status.TotalBytesOut,
                ["uptime"] = status.Uptime.TotalSeconds,
                ["isOnline"] = status.IsOnline
            }
        });
    }

    public async Task BroadcastLocalChangeDetectedAsync(string folderId, string item, string action)
    {
        _logger.LogDebug("Local change detected: {FolderId}/{Item} ({Action})", folderId, item, action);

        await SafePublishAsync(new SyncEvent
        {
            Type = SyncEventType.LocalChangeDetected,
            Data = new ConcurrentDictionary<string, object?>
            {
                ["folderId"] = folderId,
                ["item"] = item,
                ["action"] = action
            }
        });
    }

    public async Task BroadcastRemoteChangeReceivedAsync(string folderId, string item, string deviceId)
    {
        _logger.LogDebug("Remote change received: {FolderId}/{Item} from {DeviceId}", folderId, item, deviceId);

        await SafePublishAsync(new SyncEvent
        {
            Type = SyncEventType.RemoteChangeReceived,
            Data = new ConcurrentDictionary<string, object?>
            {
                ["folderId"] = folderId,
                ["item"] = item,
                ["deviceId"] = deviceId
            }
        });
    }

    public async Task BroadcastNatTypeDetectedAsync(string natType, string? externalAddress = null)
    {
        _logger.LogInformation("NAT type detected: {NatType}, External: {ExternalAddress}", natType, externalAddress ?? "unknown");

        await SafePublishAsync(new SyncEvent
        {
            Type = SyncEventType.NatTypeDetected,
            Data = new ConcurrentDictionary<string, object?>
            {
                ["natType"] = natType,
                ["externalAddress"] = externalAddress
            }
        });
    }

    public async Task BroadcastExternalAddressDiscoveredAsync(string address, int port, string source)
    {
        _logger.LogInformation("External address discovered: {Address}:{Port} via {Source}", address, port, source);

        await SafePublishAsync(new SyncEvent
        {
            Type = SyncEventType.ExternalAddressDiscovered,
            Data = new ConcurrentDictionary<string, object?>
            {
                ["address"] = address,
                ["port"] = port,
                ["source"] = source
            }
        });
    }

    /// <summary>
    /// Subscribes to events with an optional filter.
    /// </summary>
    public SyncEventSubscription Subscribe(
        string subscriberId,
        SyncEventType[]? filter = null,
        long? sinceEventId = null)
    {
        return _eventQueue.Subscribe(subscriberId, filter, sinceEventId);
    }

    /// <summary>
    /// Gets the last event ID for polling.
    /// </summary>
    public long GetLastEventId() => _eventQueue.GetLastEventId();

    /// <summary>
    /// Gets events since a specific event ID.
    /// </summary>
    public IEnumerable<SyncEvent> GetEventsSince(long sinceEventId, int limit = 100, SyncEventType[]? filter = null)
    {
        return _eventQueue.GetEventsSince(sinceEventId, limit, filter);
    }

    public void Dispose()
    {
        _eventQueue.Dispose();
    }
}