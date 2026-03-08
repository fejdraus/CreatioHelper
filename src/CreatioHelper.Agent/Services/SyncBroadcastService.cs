using Microsoft.AspNetCore.SignalR;
using CreatioHelper.Agent.Hubs;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services.Sync;
using CreatioHelper.Infrastructure.Services.Sync.Events;
using CreatioHelper.Contracts.Responses;

namespace CreatioHelper.Agent.Services;

/// <summary>
/// Background service that broadcasts sync events and system status via SignalR
/// </summary>
public class SyncBroadcastService : BackgroundService
{
    private readonly IHubContext<SyncHub> _hubContext;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<SyncBroadcastService> _logger;
    private SyncEventSubscription? _eventSubscription;

    public SyncBroadcastService(
        IHubContext<SyncHub> hubContext,
        IServiceProvider serviceProvider,
        ILogger<SyncBroadcastService> logger)
    {
        _hubContext = hubContext;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SyncBroadcastService started");

        // Start event subscription task
        var eventTask = ProcessEventsAsync(stoppingToken);

        // Start periodic status broadcast task
        var statusTask = BroadcastStatusPeriodicallyAsync(stoppingToken);

        await Task.WhenAll(eventTask, statusTask);
    }

    private async Task ProcessEventsAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var eventBroadcaster = scope.ServiceProvider.GetService<SyncEventBroadcaster>();

        if (eventBroadcaster == null)
        {
            _logger.LogWarning("SyncEventBroadcaster not available, event processing disabled");
            return;
        }

        _eventSubscription = eventBroadcaster.Subscribe("SyncBroadcastService");

        try
        {
            await foreach (var syncEvent in _eventSubscription.Events.ReadAllAsync(stoppingToken))
            {
                await BroadcastEventAsync(syncEvent);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing sync events");
        }
    }

    private async Task BroadcastEventAsync(SyncEvent syncEvent)
    {
        try
        {
            switch (syncEvent.Type)
            {
                case SyncEventType.FolderStateChanged:
                case SyncEventType.FolderCompletion:
                case SyncEventType.FolderScanProgress:
                case SyncEventType.FolderScanComplete:
                case SyncEventType.FolderPaused:
                case SyncEventType.FolderResumed:
                case SyncEventType.FolderError:
                    await BroadcastFolderStatusAsync(syncEvent);
                    break;

                case SyncEventType.DeviceConnected:
                case SyncEventType.DeviceDisconnected:
                    await BroadcastConnectionChangedAsync(syncEvent);
                    break;

                case SyncEventType.ItemStarted:
                case SyncEventType.ItemFinished:
                case SyncEventType.DownloadProgress:
                case SyncEventType.LocalChangeDetected:
                case SyncEventType.RemoteChangeReceived:
                case SyncEventType.ConflictDetected:
                case SyncEventType.SyncError:
                    await BroadcastSyncEventAsync(syncEvent);
                    break;

                case SyncEventType.StateChanged:
                    // System status is handled by periodic broadcast
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting event {EventType}", syncEvent.Type);
        }
    }

    private async Task BroadcastFolderStatusAsync(SyncEvent syncEvent)
    {
        if (!syncEvent.Data.TryGetValue("folderId", out var folderIdObj) || folderIdObj is not string folderId)
            return;

        using var scope = _serviceProvider.CreateScope();
        var syncEngine = scope.ServiceProvider.GetRequiredService<ISyncEngine>();

        try
        {
            var status = await syncEngine.GetSyncStatusAsync(folderId);
            var folder = await syncEngine.GetFolderAsync(folderId);

            if (folder == null) return;

            // Use LocalFiles/LocalBytes as global (same as API endpoint /rest/db/status)
            var globalFiles = status.LocalFiles;
            var globalBytes = status.LocalBytes;
            var inSyncBytes = globalBytes - status.NeedBytes;

            var folderStatus = new FolderStatusDto
            {
                Folder = folderId,
                State = status.State.ToString().ToLowerInvariant(),
                GlobalFiles = globalFiles,
                GlobalDirectories = status.LocalDirectories,
                GlobalBytes = globalBytes,
                LocalFiles = status.LocalFiles,
                LocalDirectories = status.LocalDirectories,
                LocalBytes = status.LocalBytes,
                NeedFiles = status.NeedFiles,
                NeedBytes = status.NeedBytes,
                InSyncBytes = inSyncBytes,
                SyncPercentage = globalBytes > 0
                    ? (double)inSyncBytes / globalBytes * 100
                    : 100
            };

            await _hubContext.Clients.All.SendAsync("FolderStatus", folderStatus);
            _logger.LogDebug("Broadcasted FolderStatus for {FolderId}: {State}", folderId, status.State);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting folder status for {FolderId}", folderId);
        }
    }

    private async Task BroadcastConnectionChangedAsync(SyncEvent syncEvent)
    {
        if (!syncEvent.Data.TryGetValue("deviceId", out var deviceIdObj) || deviceIdObj is not string deviceId)
            return;

        var connected = syncEvent.Type == SyncEventType.DeviceConnected;
        syncEvent.Data.TryGetValue("address", out var addressObj);
        syncEvent.Data.TryGetValue("connectionType", out var connectionTypeObj);

        var connectionInfo = new ConnectionInfoDto
        {
            DeviceId = deviceId,
            Connected = connected,
            Address = addressObj?.ToString() ?? "",
            Type = connectionTypeObj?.ToString() ?? ""
        };

        await _hubContext.Clients.All.SendAsync("ConnectionChanged", connectionInfo);
        _logger.LogDebug("Broadcasted ConnectionChanged for {DeviceId}: {Connected}", deviceId, connected);
    }

    private async Task BroadcastSyncEventAsync(SyncEvent syncEvent)
    {
        var eventDto = new SyncEventDto
        {
            Type = syncEvent.Type.ToString(),
            Timestamp = syncEvent.Time,
            FolderId = syncEvent.Data.TryGetValue("folderId", out var fid) ? fid?.ToString() : null,
            DeviceId = syncEvent.Data.TryGetValue("deviceId", out var did) ? did?.ToString() : null,
            Message = syncEvent.Data.TryGetValue("error", out var err) ? err?.ToString() : null,
            Data = syncEvent.Data.ToDictionary(k => k.Key, v => v.Value ?? (object)"null")
        };

        await _hubContext.Clients.All.SendAsync("SyncEvent", eventDto);
    }

    private async Task BroadcastStatusPeriodicallyAsync(CancellationToken stoppingToken)
    {
        // Initial delay to allow services to start
        await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await BroadcastSystemStatusAsync();
                await BroadcastAllFolderStatusesAsync();
                await BroadcastAllConnectionStatusesAsync();
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting status");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }

    private async Task BroadcastAllFolderStatusesAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var syncEngine = scope.ServiceProvider.GetRequiredService<ISyncEngine>();

        try
        {
            var folders = await syncEngine.GetFoldersAsync();

            foreach (var folder in folders)
            {
                try
                {
                    var status = await syncEngine.GetSyncStatusAsync(folder.Id);

                    // Use LocalFiles/LocalBytes as global (same as API endpoint /rest/db/status)
                    var globalFiles = status.LocalFiles;
                    var globalBytes = status.LocalBytes;
                    var inSyncBytes = globalBytes - status.NeedBytes;

                    var folderStatus = new FolderStatusDto
                    {
                        Folder = folder.Id,
                        State = status.State.ToString().ToLowerInvariant(),
                        GlobalFiles = globalFiles,
                        GlobalDirectories = status.LocalDirectories,
                        GlobalBytes = globalBytes,
                        LocalFiles = status.LocalFiles,
                        LocalDirectories = status.LocalDirectories,
                        LocalBytes = status.LocalBytes,
                        NeedFiles = status.NeedFiles,
                        NeedBytes = status.NeedBytes,
                        InSyncBytes = inSyncBytes,
                        SyncPercentage = globalBytes > 0
                            ? (double)inSyncBytes / globalBytes * 100
                            : 100
                    };

                    await _hubContext.Clients.All.SendAsync("FolderStatus", folderStatus);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error getting status for folder {FolderId}", folder.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting folder statuses");
        }
    }

    private async Task BroadcastAllConnectionStatusesAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var syncEngine = scope.ServiceProvider.GetRequiredService<ISyncEngine>();

        try
        {
            var devices = await syncEngine.GetDevicesAsync();
            var myId = syncEngine.DeviceId;

            foreach (var device in devices.Where(d => d.DeviceId != myId))
            {
                var connectionInfo = new ConnectionInfoDto
                {
                    DeviceId = device.DeviceId,
                    Connected = device.IsConnected,
                    Address = device.Addresses?.FirstOrDefault() ?? "",
                    Type = device.ConnectionType ?? ""
                };

                await _hubContext.Clients.All.SendAsync("ConnectionChanged", connectionInfo);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error broadcasting connection statuses");
        }
    }

    private async Task BroadcastSystemStatusAsync()
    {
        using var scope = _serviceProvider.CreateScope();
        var syncEngine = scope.ServiceProvider.GetRequiredService<ISyncEngine>();

        try
        {
            var statistics = await syncEngine.GetStatisticsAsync();
            var process = System.Diagnostics.Process.GetCurrentProcess();
            var syncDatabase = scope.ServiceProvider.GetRequiredService<ISyncDatabase>();

            long dbSize = 0;
            try
            {
                dbSize = await syncDatabase.GetDatabaseSizeAsync();
            }
            catch { /* ignore */ }

            var gcMemoryInfo = GC.GetGCMemoryInfo();

            var systemStatus = new SystemStatusDto
            {
                MyId = syncEngine.DeviceId,
                StartTime = process.StartTime.ToUniversalTime(),
                Uptime = (long)statistics.Uptime.TotalSeconds,
                Sys = process.WorkingSet64,
                Alloc = GC.GetTotalMemory(false),
                CpuPercent = process.TotalProcessorTime.TotalMilliseconds /
                    (Environment.ProcessorCount * DateTime.UtcNow.Subtract(process.StartTime.ToUniversalTime()).TotalMilliseconds) * 100,
                Goroutines = Environment.ProcessorCount,
                TotalIn = statistics.TotalBytesIn,
                TotalOut = statistics.TotalBytesOut,
                InBytesPerSec = 0,
                OutBytesPerSec = 0,
                DbSize = dbSize,
                AppMemory = process.WorkingSet64,
                OsMemoryUsed = gcMemoryInfo.MemoryLoadBytes,
                TotalPhysicalMemory = gcMemoryInfo.TotalAvailableMemoryBytes,
                GcGen0Collections = GC.CollectionCount(0),
                GcGen1Collections = GC.CollectionCount(1),
                GcGen2Collections = GC.CollectionCount(2),
                GcTotalPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds,
                HeapSizeBytes = gcMemoryInfo.HeapSizeBytes,
                HeapFragmentedBytes = gcMemoryInfo.FragmentedBytes,
                ProcessHandleCount = process.HandleCount,
                ProcessThreadCount = process.Threads.Count,
                TotalBytesIn = statistics.TotalBytesIn,
                TotalBytesOut = statistics.TotalBytesOut
            };

            await _hubContext.Clients.All.SendAsync("SystemStatus", systemStatus);
            _logger.LogTrace("Broadcasted SystemStatus: Uptime={Uptime}, Memory={Memory}",
                statistics.Uptime, process.WorkingSet64);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting system statistics");
        }
    }

    public override void Dispose()
    {
        _eventSubscription?.Dispose();
        base.Dispose();
    }
}
