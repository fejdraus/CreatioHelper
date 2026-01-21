using CreatioHelper.Agent.Configuration;
using CreatioHelper.Agent.Services.SyncEvents;
using Microsoft.Extensions.Options;

namespace CreatioHelper.Agent.Services;

/// <summary>
/// Background service that monitors synchronization using ISyncEventSource
/// and automatically stops/starts services during incoming sync.
/// Works with both built-in sync engine and external Syncthing.
/// </summary>
public class SyncAutoStopService : BackgroundService
{
    private readonly ILogger<SyncAutoStopService> _logger;
    private readonly SyncthingAutoStopSettings _settings;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISyncEventSource _syncEventSource;

    // Thread-safe flag: 0 = services running, 1 = services stopped
    private int _servicesAreStopped = 0;
    private readonly SemaphoreSlim _completionCheckLock = new(1, 1);
    private bool _disposed = false;

    // Track which folders are currently syncing
    private readonly HashSet<string> _syncingFolders = new();
    private readonly object _syncingFoldersLock = new();
    private DateTime _lastSyncActivityTime = DateTime.UtcNow;

    public SyncAutoStopService(
        ILogger<SyncAutoStopService> logger,
        IOptions<SyncthingAutoStopSettings> settings,
        IServiceProvider serviceProvider,
        ISyncEventSource syncEventSource)
    {
        _logger = logger;
        _settings = settings.Value;
        _serviceProvider = serviceProvider;
        _syncEventSource = syncEventSource;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("SyncAutoStop is disabled in configuration");
            return;
        }

        if (_settings.MonitoredFolders.Count == 0)
        {
            _logger.LogWarning("No monitored folders configured for SyncAutoStop");
            return;
        }

        _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║  SyncAutoStop Service Started                              ║");
        _logger.LogInformation("╠════════════════════════════════════════════════════════════╣");
        _logger.LogInformation("║  Sync source: {Source,-44} ║", _syncEventSource.SourceName);
        _logger.LogInformation("║  Monitored folders: {FolderCount,-36} ║", _settings.MonitoredFolders.Count);
        _logger.LogInformation("║  Folders: {Folders,-46} ║", string.Join(", ", _settings.MonitoredFolders));
        _logger.LogInformation("║  Idle timeout: {Timeout,-43}s ║", _settings.IdleTimeoutSeconds);
        _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");

        try
        {
            // Subscribe to events
            _syncEventSource.SyncStarted += OnSyncStarted;
            _syncEventSource.SyncCompleted += OnSyncCompleted;
            _syncEventSource.FileTransferStarted += OnFileTransferStarted;
            _syncEventSource.FileTransferCompleted += OnFileTransferCompleted;
            _syncEventSource.FolderStateChanged += OnFolderStateChanged;

            // Start the event source
            await _syncEventSource.StartAsync(stoppingToken);

            // Keep running until cancellation
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SyncAutoStop service stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in SyncAutoStop service");
            throw;
        }
        finally
        {
            // Unsubscribe from events
            _syncEventSource.SyncStarted -= OnSyncStarted;
            _syncEventSource.SyncCompleted -= OnSyncCompleted;
            _syncEventSource.FileTransferStarted -= OnFileTransferStarted;
            _syncEventSource.FileTransferCompleted -= OnFileTransferCompleted;
            _syncEventSource.FolderStateChanged -= OnFolderStateChanged;

            // Stop the event source
            await _syncEventSource.StopAsync();

            // Ensure services are started on shutdown
            if (AreServicesStopped())
            {
                _logger.LogInformation("Service shutting down - starting services");
                await StartServicesWithTimeoutAsync(TimeSpan.FromSeconds(30));
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SyncAutoStop service stopping...");
        await base.StopAsync(cancellationToken);
        _logger.LogInformation("SyncAutoStop service stopped");
    }

    public override void Dispose()
    {
        if (!_disposed)
        {
            _completionCheckLock?.Dispose();
            _disposed = true;
            _logger.LogInformation("SyncAutoStop service resources disposed");
        }
        base.Dispose();
    }

    private void OnSyncStarted(object? sender, SyncActivityEventArgs e)
    {
        if (!IsMonitoredFolder(e.FolderId))
        {
            return;
        }

        _logger.LogInformation("┌────────────────────────────────────────────────────────────┐");
        _logger.LogInformation("│ 🔄 SYNC STARTED                                            │");
        _logger.LogInformation("├────────────────────────────────────────────────────────────┤");
        _logger.LogInformation("│ Folder: {Folder,-50} │", e.FolderId);
        _logger.LogInformation("└────────────────────────────────────────────────────────────┘");

        lock (_syncingFoldersLock)
        {
            _syncingFolders.Add(e.FolderId);
            _lastSyncActivityTime = DateTime.UtcNow;
        }

        // Stop services asynchronously
        _ = StopServicesIfNeededAsync();
    }

    private void OnSyncCompleted(object? sender, SyncActivityEventArgs e)
    {
        if (!IsMonitoredFolder(e.FolderId))
        {
            return;
        }

        _logger.LogInformation("⏳ Sync completed for folder {Folder}", e.FolderId);

        lock (_syncingFoldersLock)
        {
            _syncingFolders.Remove(e.FolderId);
            _lastSyncActivityTime = DateTime.UtcNow;
        }

        // Check if all folders are done and start services
        _ = CheckAndStartServicesAsync();
    }

    private void OnFileTransferStarted(object? sender, FileTransferEventArgs e)
    {
        if (!IsMonitoredFolder(e.FolderId))
        {
            return;
        }

        _logger.LogWarning("┌────────────────────────────────────────────────────────────┐");
        _logger.LogWarning("│ 🔄 INCOMING FILE TRANSFER                                  │");
        _logger.LogWarning("├────────────────────────────────────────────────────────────┤");
        _logger.LogWarning("│ Folder: {Folder,-50} │", e.FolderId);
        _logger.LogWarning("│ File: {File,-52} │", e.FileName);
        _logger.LogWarning("│ Action: {Action,-50} │", e.Action);
        _logger.LogWarning("└────────────────────────────────────────────────────────────┘");

        lock (_syncingFoldersLock)
        {
            _syncingFolders.Add(e.FolderId);
            _lastSyncActivityTime = DateTime.UtcNow;
        }

        // Stop services asynchronously
        _ = StopServicesIfNeededAsync();
    }

    private void OnFileTransferCompleted(object? sender, FileTransferEventArgs e)
    {
        if (!IsMonitoredFolder(e.FolderId))
        {
            return;
        }

        lock (_syncingFoldersLock)
        {
            _lastSyncActivityTime = DateTime.UtcNow;
        }

        _logger.LogDebug("File transfer completed: {File} in {Folder}", e.FileName, e.FolderId);

        // Check if we can start services
        _ = CheckAndStartServicesAsync();
    }

    private void OnFolderStateChanged(object? sender, FolderStateEventArgs e)
    {
        if (!IsMonitoredFolder(e.FolderId))
        {
            return;
        }

        _logger.LogDebug("Folder {Folder} state changed: {PreviousState} -> {NewState}",
            e.FolderId, e.PreviousState, e.State);

        lock (_syncingFoldersLock)
        {
            _lastSyncActivityTime = DateTime.UtcNow;

            if (e.State == FolderSyncState.Syncing || e.State == FolderSyncState.Scanning)
            {
                _syncingFolders.Add(e.FolderId);
            }
            else if (e.State == FolderSyncState.Idle)
            {
                _syncingFolders.Remove(e.FolderId);
            }
        }

        if (e.State == FolderSyncState.Idle)
        {
            _ = CheckAndStartServicesAsync();
        }
    }

    private async Task StopServicesIfNeededAsync()
    {
        if (AreServicesStopped())
        {
            return;
        }

        using var scope = _serviceProvider.CreateScope();
        var serviceStateManager = scope.ServiceProvider.GetRequiredService<ServiceStateManager>();

        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromSeconds(_settings.ServiceOperationTimeoutSeconds));

        bool stopped = await serviceStateManager.StopServicesAsync(timeoutCts.Token);

        if (stopped && TrySetServicesStoppedState())
        {
            _logger.LogInformation("✅ Services stopped successfully");
        }
        else if (!stopped)
        {
            _logger.LogWarning("⚠️  Failed to stop services or no services configured");
        }
        else
        {
            _logger.LogInformation("ℹ️  Services already stopped by concurrent sync event");
        }
    }

    private async Task CheckAndStartServicesAsync()
    {
        // Prevent concurrent completion checks
        if (!await _completionCheckLock.WaitAsync(0))
        {
            _logger.LogDebug("Completion check already in progress, skipping duplicate");
            return;
        }

        try
        {
            if (!AreServicesStopped())
            {
                _logger.LogDebug("Services not stopped, skipping completion check");
                return;
            }

            _logger.LogInformation("┌────────────────────────────────────────────────────────────┐");
            _logger.LogInformation("│ ⏳ WAITING FOR SYNCHRONIZATION TO COMPLETE                 │");
            _logger.LogInformation("├────────────────────────────────────────────────────────────┤");
            _logger.LogInformation("│ Idle timeout: {Timeout,-46}s │", _settings.IdleTimeoutSeconds);
            _logger.LogInformation("│ Required stable checks: {Checks,-36} │", _settings.RequiredStableChecks);
            _logger.LogInformation("└────────────────────────────────────────────────────────────┘");

            bool syncCompleted = await WaitForSyncCompletionAsync();

            if (syncCompleted)
            {
                _logger.LogInformation("┌────────────────────────────────────────────────────────────┐");
                _logger.LogInformation("│ ✅ SYNCHRONIZATION COMPLETED AND STABLE                    │");
                _logger.LogInformation("└────────────────────────────────────────────────────────────┘");

                await StartServicesWithTimeoutAsync(TimeSpan.FromSeconds(_settings.ServiceOperationTimeoutSeconds));
            }
            else
            {
                _logger.LogWarning("⚠️  Sync completion monitoring cancelled or failed");
            }
        }
        finally
        {
            _completionCheckLock.Release();
        }
    }

    private async Task<bool> WaitForSyncCompletionAsync()
    {
        var pollingInterval = TimeSpan.FromSeconds(_settings.CompletionCheckIntervalSeconds);
        var stableChecks = 0;
        var startTime = DateTime.UtcNow;
        var maxWaitTime = TimeSpan.FromMinutes(_settings.MaxSyncWaitTimeMinutes);

        while (true)
        {
            // Check if max wait time exceeded
            var elapsedTime = DateTime.UtcNow - startTime;
            if (elapsedTime > maxWaitTime)
            {
                _logger.LogError("Sync completion timeout after {Minutes} minutes - forcing service start",
                    _settings.MaxSyncWaitTimeMinutes);
                return true; // Return true to start services anyway
            }

            bool allFoldersCompleted = true;

            // Check all monitored folders
            foreach (var folderId in _settings.MonitoredFolders)
            {
                var isSyncing = await _syncEventSource.IsFolderSyncingAsync(folderId);
                if (isSyncing)
                {
                    _logger.LogDebug("Folder {FolderId}: still syncing", folderId);
                    allFoldersCompleted = false;
                    break;
                }

                var completion = await _syncEventSource.GetFolderCompletionAsync(folderId);
                if (completion < 100.0)
                {
                    _logger.LogDebug("Folder {FolderId}: {Completion:F1}% complete", folderId, completion);
                    allFoldersCompleted = false;
                    break;
                }
            }

            // Check if any folders are in the syncing set
            lock (_syncingFoldersLock)
            {
                if (_syncingFolders.Count > 0)
                {
                    allFoldersCompleted = false;
                }
            }

            if (allFoldersCompleted)
            {
                // Check if we've been idle long enough
                DateTime lastActivity;
                lock (_syncingFoldersLock)
                {
                    lastActivity = _lastSyncActivityTime;
                }

                var idleTime = DateTime.UtcNow - lastActivity;
                if (idleTime.TotalSeconds >= _settings.IdleTimeoutSeconds)
                {
                    stableChecks++;
                    _logger.LogInformation("Sync stable check {Check}/{Required} (idle for {IdleSeconds}s)",
                        stableChecks, _settings.RequiredStableChecks, (int)idleTime.TotalSeconds);

                    if (stableChecks >= _settings.RequiredStableChecks)
                    {
                        _logger.LogInformation("All folders sync completed and stable!");
                        return true;
                    }
                }
                else
                {
                    _logger.LogDebug("Waiting for idle timeout: {ElapsedSeconds}/{RequiredSeconds}s",
                        (int)idleTime.TotalSeconds, _settings.IdleTimeoutSeconds);
                }
            }
            else
            {
                stableChecks = 0;
            }

            await Task.Delay(pollingInterval);
        }
    }

    private async Task StartServicesWithTimeoutAsync(TimeSpan timeout)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var serviceStateManager = scope.ServiceProvider.GetRequiredService<ServiceStateManager>();

            using var timeoutCts = new CancellationTokenSource(timeout);
            bool started = await serviceStateManager.StartServicesAsync(timeoutCts.Token);

            if (started)
            {
                SetServicesStoppedState(false);
                _logger.LogInformation("🚀 Services started successfully");
            }
            else
            {
                _logger.LogWarning("⚠️  Failed to start services");
                SetServicesStoppedState(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Timeout while starting services");
            SetServicesStoppedState(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting services");
            SetServicesStoppedState(false);
        }
    }

    private bool IsMonitoredFolder(string folderId)
    {
        return !string.IsNullOrEmpty(folderId) && _settings.MonitoredFolders.Contains(folderId);
    }

    /// <summary>
    /// Thread-safe check if services are currently stopped
    /// </summary>
    private bool AreServicesStopped() => Interlocked.CompareExchange(ref _servicesAreStopped, 0, 0) == 1;

    /// <summary>
    /// Thread-safe set services stopped state
    /// </summary>
    private void SetServicesStoppedState(bool stopped) => Interlocked.Exchange(ref _servicesAreStopped, stopped ? 1 : 0);

    /// <summary>
    /// Atomically try to transition from running (0) to stopped (1)
    /// </summary>
    private bool TrySetServicesStoppedState() => Interlocked.CompareExchange(ref _servicesAreStopped, 1, 0) == 0;
}
