using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities.Events;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Agent.Services.SyncEvents;

/// <summary>
/// Sync event source implementation that uses the built-in ISyncEngine.
/// Translates ISyncEngine and IEventLogger events to unified ISyncEventSource events.
/// </summary>
public class BuiltInSyncEventSource : ISyncEventSource
{
    private readonly ISyncEngine _syncEngine;
    private readonly IEventLogger _eventLogger;
    private readonly ILogger<BuiltInSyncEventSource> _logger;
    private readonly Dictionary<string, FolderSyncState> _folderStates = new();
    private readonly Dictionary<string, bool> _folderSyncingStatus = new();
    private readonly object _stateLock = new();

    private IEventSubscription? _eventSubscription;
    private CancellationTokenSource? _cts;
    private Task? _eventProcessingTask;
    private bool _isConnected;
    private bool _disposed;

    public string SourceName => "BuiltInSyncEngine";
    public bool IsConnected => _isConnected;

    public event EventHandler<SyncActivityEventArgs>? SyncStarted;
    public event EventHandler<SyncActivityEventArgs>? SyncCompleted;
    public event EventHandler<FileTransferEventArgs>? FileTransferStarted;
    public event EventHandler<FileTransferEventArgs>? FileTransferCompleted;
    public event EventHandler<FolderStateEventArgs>? FolderStateChanged;

    public BuiltInSyncEventSource(
        ISyncEngine syncEngine,
        IEventLogger eventLogger,
        ILogger<BuiltInSyncEventSource> logger)
    {
        _syncEngine = syncEngine;
        _eventLogger = eventLogger;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isConnected)
        {
            _logger.LogWarning("BuiltInSyncEventSource is already started");
            return;
        }

        _logger.LogInformation("Starting BuiltInSyncEventSource");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Subscribe to ISyncEngine events
        _syncEngine.FolderSynced += OnFolderSynced;
        _syncEngine.ConflictDetected += OnConflictDetected;
        _syncEngine.SyncError += OnSyncError;

        // Subscribe to IEventLogger for detailed events
        var eventMask = SyncEventType.SyncEvents |
                       SyncEventType.TransferEvents |
                       SyncEventType.FolderEvents |
                       SyncEventType.ChangeEvents |
                       SyncEventType.StateChanged;

        _eventSubscription = _eventLogger.Subscribe(eventMask);

        // Start processing events in background
        _eventProcessingTask = ProcessEventsAsync(_cts.Token);

        // Initialize folder states
        await InitializeFolderStatesAsync(cancellationToken);

        _isConnected = true;
        _logger.LogInformation("BuiltInSyncEventSource started successfully");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isConnected)
        {
            return;
        }

        _logger.LogInformation("Stopping BuiltInSyncEventSource");

        // Unsubscribe from ISyncEngine events
        _syncEngine.FolderSynced -= OnFolderSynced;
        _syncEngine.ConflictDetected -= OnConflictDetected;
        _syncEngine.SyncError -= OnSyncError;

        // Cancel event processing
        _cts?.Cancel();

        // Wait for event processing to complete
        if (_eventProcessingTask != null)
        {
            try
            {
                await _eventProcessingTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Event processing task did not complete in time");
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        }

        // Dispose event subscription
        _eventSubscription?.Dispose();
        _eventSubscription = null;

        _cts?.Dispose();
        _cts = null;

        _isConnected = false;
        _logger.LogInformation("BuiltInSyncEventSource stopped");
    }

    public async Task<bool> IsFolderSyncingAsync(string folderId, CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await _syncEngine.GetSyncStatusAsync(folderId);
            return status.State == SyncState.Syncing || status.State == SyncState.Scanning;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking folder sync status for {FolderId}", folderId);

            lock (_stateLock)
            {
                return _folderSyncingStatus.TryGetValue(folderId, out var isSyncing) && isSyncing;
            }
        }
    }

    public async Task<double> GetFolderCompletionAsync(string folderId, CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await _syncEngine.GetSyncStatusAsync(folderId);

            if (status.GlobalBytes == 0)
            {
                return 100.0;
            }

            var completedBytes = status.GlobalBytes - status.NeedBytes;
            return (double)completedBytes / status.GlobalBytes * 100.0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting folder completion for {FolderId}", folderId);
            return 100.0; // Assume complete if we can't check
        }
    }

    private async Task InitializeFolderStatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var folders = await _syncEngine.GetFoldersAsync();

            foreach (var folder in folders)
            {
                var status = await _syncEngine.GetSyncStatusAsync(folder.Id);
                var state = MapSyncStateToFolderState(status.State);

                lock (_stateLock)
                {
                    _folderStates[folder.Id] = state;
                    _folderSyncingStatus[folder.Id] = status.State == SyncState.Syncing;
                }
            }

            _logger.LogDebug("Initialized states for {Count} folders", folders.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing folder states");
        }
    }

    private async Task ProcessEventsAsync(CancellationToken cancellationToken)
    {
        if (_eventSubscription == null)
        {
            return;
        }

        try
        {
            await foreach (var syncEvent in _eventSubscription.Events.WithCancellation(cancellationToken))
            {
                try
                {
                    ProcessSyncEvent(syncEvent);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing sync event {EventType}", syncEvent.Type);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when stopping
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in event processing loop");
        }
    }

    private void ProcessSyncEvent(SyncEvent syncEvent)
    {
        switch (syncEvent.Type)
        {
            case SyncEventType.SyncStarted:
                HandleSyncStarted(syncEvent);
                break;

            case SyncEventType.SyncCompleted:
                HandleSyncCompleted(syncEvent);
                break;

            case SyncEventType.ItemStarted:
                HandleItemStarted(syncEvent);
                break;

            case SyncEventType.ItemFinished:
                HandleItemFinished(syncEvent);
                break;

            case SyncEventType.StateChanged:
                HandleStateChanged(syncEvent);
                break;

            case SyncEventType.FolderPaused:
                HandleFolderPaused(syncEvent);
                break;

            case SyncEventType.FolderResumed:
                HandleFolderResumed(syncEvent);
                break;
        }
    }

    private void HandleSyncStarted(SyncEvent syncEvent)
    {
        if (string.IsNullOrEmpty(syncEvent.FolderId))
        {
            return;
        }

        lock (_stateLock)
        {
            _folderSyncingStatus[syncEvent.FolderId] = true;
        }

        var args = new SyncActivityEventArgs
        {
            FolderId = syncEvent.FolderId,
            Timestamp = syncEvent.Time,
            DeviceId = syncEvent.DeviceId
        };

        SyncStarted?.Invoke(this, args);
        UpdateFolderState(syncEvent.FolderId, FolderSyncState.Syncing);
    }

    private void HandleSyncCompleted(SyncEvent syncEvent)
    {
        if (string.IsNullOrEmpty(syncEvent.FolderId))
        {
            return;
        }

        lock (_stateLock)
        {
            _folderSyncingStatus[syncEvent.FolderId] = false;
        }

        var args = new SyncActivityEventArgs
        {
            FolderId = syncEvent.FolderId,
            Timestamp = syncEvent.Time,
            DeviceId = syncEvent.DeviceId
        };

        SyncCompleted?.Invoke(this, args);
        UpdateFolderState(syncEvent.FolderId, FolderSyncState.Idle);
    }

    private void HandleItemStarted(SyncEvent syncEvent)
    {
        if (string.IsNullOrEmpty(syncEvent.FolderId) || string.IsNullOrEmpty(syncEvent.FilePath))
        {
            return;
        }

        var action = GetActionFromEventData(syncEvent.Data);
        var size = GetSizeFromEventData(syncEvent.Data);

        var args = new FileTransferEventArgs
        {
            FolderId = syncEvent.FolderId,
            FileName = syncEvent.FilePath,
            Action = action,
            Size = size,
            Timestamp = syncEvent.Time
        };

        FileTransferStarted?.Invoke(this, args);
    }

    private void HandleItemFinished(SyncEvent syncEvent)
    {
        if (string.IsNullOrEmpty(syncEvent.FolderId) || string.IsNullOrEmpty(syncEvent.FilePath))
        {
            return;
        }

        var action = GetActionFromEventData(syncEvent.Data);
        var size = GetSizeFromEventData(syncEvent.Data);
        var error = GetErrorFromEventData(syncEvent.Data);

        var args = new FileTransferEventArgs
        {
            FolderId = syncEvent.FolderId,
            FileName = syncEvent.FilePath,
            Action = action,
            Size = size,
            Timestamp = syncEvent.Time,
            Error = error
        };

        FileTransferCompleted?.Invoke(this, args);
    }

    private void HandleStateChanged(SyncEvent syncEvent)
    {
        if (string.IsNullOrEmpty(syncEvent.FolderId))
        {
            return;
        }

        var newState = GetStateFromEventData(syncEvent.Data);
        UpdateFolderState(syncEvent.FolderId, newState);
    }

    private void HandleFolderPaused(SyncEvent syncEvent)
    {
        if (!string.IsNullOrEmpty(syncEvent.FolderId))
        {
            UpdateFolderState(syncEvent.FolderId, FolderSyncState.Paused);
        }
    }

    private void HandleFolderResumed(SyncEvent syncEvent)
    {
        if (!string.IsNullOrEmpty(syncEvent.FolderId))
        {
            UpdateFolderState(syncEvent.FolderId, FolderSyncState.Idle);
        }
    }

    private void UpdateFolderState(string folderId, FolderSyncState newState)
    {
        FolderSyncState previousState;

        lock (_stateLock)
        {
            _folderStates.TryGetValue(folderId, out previousState);

            if (previousState == newState)
            {
                return;
            }

            _folderStates[folderId] = newState;
        }

        var args = new FolderStateEventArgs
        {
            FolderId = folderId,
            State = newState,
            PreviousState = previousState,
            Timestamp = DateTime.UtcNow
        };

        FolderStateChanged?.Invoke(this, args);
    }

    private void OnFolderSynced(object? sender, FolderSyncedEventArgs e)
    {
        lock (_stateLock)
        {
            _folderSyncingStatus[e.FolderId] = false;
        }

        var args = new SyncActivityEventArgs
        {
            FolderId = e.FolderId,
            Timestamp = DateTime.UtcNow
        };

        SyncCompleted?.Invoke(this, args);
        UpdateFolderState(e.FolderId, FolderSyncState.Idle);
    }

    private void OnConflictDetected(object? sender, ConflictDetectedEventArgs e)
    {
        _logger.LogWarning("Conflict detected in folder {FolderId} for file {FilePath}",
            e.FolderId, e.FilePath);
    }

    private void OnSyncError(object? sender, SyncErrorEventArgs e)
    {
        _logger.LogError("Sync error in folder {FolderId}: {Error}", e.FolderId, e.Error);

        FolderSyncState previousState;
        lock (_stateLock)
        {
            _folderStates.TryGetValue(e.FolderId, out previousState);
            _folderStates[e.FolderId] = FolderSyncState.Error;
        }

        var args = new FolderStateEventArgs
        {
            FolderId = e.FolderId,
            State = FolderSyncState.Error,
            PreviousState = previousState,
            Error = e.Error,
            Timestamp = DateTime.UtcNow
        };

        FolderStateChanged?.Invoke(this, args);
    }

    private static FolderSyncState MapSyncStateToFolderState(SyncState syncState)
    {
        return syncState switch
        {
            SyncState.Idle => FolderSyncState.Idle,
            SyncState.Scanning => FolderSyncState.Scanning,
            SyncState.Syncing => FolderSyncState.Syncing,
            SyncState.Error => FolderSyncState.Error,
            SyncState.Paused => FolderSyncState.Paused,
            _ => FolderSyncState.Unknown
        };
    }

    private static string GetActionFromEventData(object? data)
    {
        if (data is IDictionary<string, object?> dict)
        {
            if (dict.TryGetValue("action", out var action) && action is string actionStr)
            {
                return actionStr;
            }
            if (dict.TryGetValue("Action", out action) && action is string actionStr2)
            {
                return actionStr2;
            }
        }
        return "update";
    }

    private static long GetSizeFromEventData(object? data)
    {
        if (data is IDictionary<string, object?> dict)
        {
            if (dict.TryGetValue("size", out var size) && size is long sizeL)
            {
                return sizeL;
            }
            if (dict.TryGetValue("Size", out size) && size is long sizeL2)
            {
                return sizeL2;
            }
            if (dict.TryGetValue("size", out size) && size is int sizeI)
            {
                return sizeI;
            }
        }
        return 0;
    }

    private static string? GetErrorFromEventData(object? data)
    {
        if (data is IDictionary<string, object?> dict)
        {
            if (dict.TryGetValue("error", out var error) && error is string errorStr)
            {
                return errorStr;
            }
            if (dict.TryGetValue("Error", out error) && error is string errorStr2)
            {
                return errorStr2;
            }
        }
        return null;
    }

    private static FolderSyncState GetStateFromEventData(object? data)
    {
        if (data is IDictionary<string, object?> dict)
        {
            string? stateStr = null;

            if (dict.TryGetValue("state", out var state) && state is string s)
            {
                stateStr = s;
            }
            else if (dict.TryGetValue("State", out state) && state is string s2)
            {
                stateStr = s2;
            }
            else if (dict.TryGetValue("to", out state) && state is string s3)
            {
                stateStr = s3;
            }

            if (!string.IsNullOrEmpty(stateStr))
            {
                return stateStr.ToLowerInvariant() switch
                {
                    "idle" => FolderSyncState.Idle,
                    "scanning" => FolderSyncState.Scanning,
                    "scan-waiting" => FolderSyncState.ScanWaiting,
                    "syncing" => FolderSyncState.Syncing,
                    "sync-waiting" => FolderSyncState.SyncWaiting,
                    "sync-preparing" => FolderSyncState.SyncPreparing,
                    "cleaning" => FolderSyncState.Cleaning,
                    "clean-waiting" => FolderSyncState.CleanWaiting,
                    "error" => FolderSyncState.Error,
                    "paused" => FolderSyncState.Paused,
                    _ => FolderSyncState.Unknown
                };
            }
        }
        return FolderSyncState.Unknown;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await StopAsync();

        GC.SuppressFinalize(this);
    }
}
