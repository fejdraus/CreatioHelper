using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Agent.Services.SyncEvents;

/// <summary>
/// Sync event source implementation that uses external Syncthing HTTP API.
/// Connects to Syncthing's /rest/events endpoint for real-time event streaming.
/// </summary>
public class ExternalSyncthingEventSource : ISyncEventSource
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ExternalSyncthingEventSource> _logger;
    private readonly string _httpClientName;
    private readonly Dictionary<string, FolderSyncState> _folderStates = new();
    private readonly Dictionary<string, bool> _folderSyncingStatus = new();
    private readonly object _stateLock = new();

    private CancellationTokenSource? _cts;
    private Task? _eventProcessingTask;
    private int _lastEventId;
    private bool _isConnected;
    private bool _disposed;

    public string SourceName => "ExternalSyncthing";
    public bool IsConnected => _isConnected;

    public event EventHandler<SyncActivityEventArgs>? SyncStarted;
    public event EventHandler<SyncActivityEventArgs>? SyncCompleted;
    public event EventHandler<FileTransferEventArgs>? FileTransferStarted;
    public event EventHandler<FileTransferEventArgs>? FileTransferCompleted;
    public event EventHandler<FolderStateEventArgs>? FolderStateChanged;

    /// <summary>
    /// Creates a new ExternalSyncthingEventSource
    /// </summary>
    /// <param name="httpClientFactory">HTTP client factory</param>
    /// <param name="logger">Logger</param>
    /// <param name="httpClientName">Name of the HTTP client to use (must be configured with Syncthing API URL and API key)</param>
    public ExternalSyncthingEventSource(
        IHttpClientFactory httpClientFactory,
        ILogger<ExternalSyncthingEventSource> logger,
        string httpClientName = "Syncthing")
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _httpClientName = httpClientName;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isConnected)
        {
            _logger.LogWarning("ExternalSyncthingEventSource is already started");
            return;
        }

        _logger.LogInformation("Starting ExternalSyncthingEventSource");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Test connection to Syncthing
        if (!await TestConnectionAsync(_cts.Token))
        {
            _logger.LogError("Failed to connect to external Syncthing API");
            throw new InvalidOperationException("Cannot connect to external Syncthing API");
        }

        // Initialize folder states
        await InitializeFolderStatesAsync(_cts.Token);

        // Start processing events in background
        _eventProcessingTask = ProcessEventsAsync(_cts.Token);

        _isConnected = true;
        _logger.LogInformation("ExternalSyncthingEventSource started successfully");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (!_isConnected)
        {
            return;
        }

        _logger.LogInformation("Stopping ExternalSyncthingEventSource");

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

        _cts?.Dispose();
        _cts = null;

        _isConnected = false;
        _logger.LogInformation("ExternalSyncthingEventSource stopped");
    }

    public async Task<bool> IsFolderSyncingAsync(string folderId, CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await GetFolderStatusAsync(folderId, cancellationToken);
            if (status == null)
            {
                lock (_stateLock)
                {
                    return _folderSyncingStatus.TryGetValue(folderId, out var isSyncing) && isSyncing;
                }
            }

            return status.State != "idle" || status.NeedBytes > 0 || status.NeedFiles > 0;
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
            var status = await GetFolderStatusAsync(folderId, cancellationToken);
            if (status == null)
            {
                return 100.0;
            }

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
            return 100.0;
        }
    }

    private async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient(_httpClientName);
            using var response = await httpClient.GetAsync("/rest/system/status", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error testing connection to Syncthing API");
            return false;
        }
    }

    private async Task InitializeFolderStatesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient(_httpClientName);
            using var response = await httpClient.GetAsync("/rest/config/folders", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get folders from Syncthing API");
                return;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var folders = JsonSerializer.Deserialize<List<SyncthingFolderConfig>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (folders == null)
            {
                return;
            }

            foreach (var folder in folders)
            {
                var status = await GetFolderStatusAsync(folder.Id, cancellationToken);
                if (status != null)
                {
                    var state = MapStateToFolderState(status.State);
                    lock (_stateLock)
                    {
                        _folderStates[folder.Id] = state;
                        _folderSyncingStatus[folder.Id] = status.State != "idle";
                    }
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
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Long-polling request - waits up to 60 seconds for new events
                var events = await GetEventsAsync(_lastEventId, 60, cancellationToken);

                if (events == null || events.Count == 0)
                {
                    continue;
                }

                // Update last event ID
                _lastEventId = events.Max(e => e.Id);

                // Process events
                foreach (var evt in events)
                {
                    try
                    {
                        ProcessEvent(evt);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing Syncthing event {EventType}", evt.Type);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error connecting to Syncthing API");
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in Syncthing event monitoring loop");
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
    }

    private void ProcessEvent(SyncthingApiEvent evt)
    {
        switch (evt.Type)
        {
            case "ItemStarted":
                HandleItemStarted(evt);
                break;

            case "ItemFinished":
                HandleItemFinished(evt);
                break;

            case "StateChanged":
                HandleStateChanged(evt);
                break;

            case "FolderSummary":
                HandleFolderSummary(evt);
                break;

            case "FolderCompletion":
                HandleFolderCompletion(evt);
                break;

            case "FolderPaused":
                HandleFolderPaused(evt);
                break;

            case "FolderResumed":
                HandleFolderResumed(evt);
                break;
        }
    }

    private void HandleItemStarted(SyncthingApiEvent evt)
    {
        var data = evt.Data as JsonElement?;
        if (!data.HasValue)
        {
            return;
        }

        if (!TryGetProperty(data.Value, "folder", out var folder) ||
            !TryGetProperty(data.Value, "item", out var item))
        {
            return;
        }

        TryGetProperty(data.Value, "action", out var action);
        TryGetLongProperty(data.Value, "size", out var size);

        var args = new FileTransferEventArgs
        {
            FolderId = folder,
            FileName = item,
            Action = action ?? "update",
            Size = size,
            Timestamp = evt.Time
        };

        FileTransferStarted?.Invoke(this, args);

        // Mark folder as syncing
        bool wasIdle;
        lock (_stateLock)
        {
            wasIdle = !_folderSyncingStatus.TryGetValue(folder, out var isSyncing) || !isSyncing;
            _folderSyncingStatus[folder] = true;
        }

        // Fire SyncStarted if this is the first item
        if (wasIdle)
        {
            var syncArgs = new SyncActivityEventArgs
            {
                FolderId = folder,
                Timestamp = evt.Time
            };
            SyncStarted?.Invoke(this, syncArgs);
            UpdateFolderState(folder, FolderSyncState.Syncing);
        }
    }

    private void HandleItemFinished(SyncthingApiEvent evt)
    {
        var data = evt.Data as JsonElement?;
        if (!data.HasValue)
        {
            return;
        }

        if (!TryGetProperty(data.Value, "folder", out var folder) ||
            !TryGetProperty(data.Value, "item", out var item))
        {
            return;
        }

        TryGetProperty(data.Value, "action", out var action);
        TryGetLongProperty(data.Value, "size", out var size);
        TryGetProperty(data.Value, "error", out var error);

        var args = new FileTransferEventArgs
        {
            FolderId = folder,
            FileName = item,
            Action = action ?? "update",
            Size = size,
            Timestamp = evt.Time,
            Error = error
        };

        FileTransferCompleted?.Invoke(this, args);
    }

    private void HandleStateChanged(SyncthingApiEvent evt)
    {
        var data = evt.Data as JsonElement?;
        if (!data.HasValue)
        {
            return;
        }

        if (!TryGetProperty(data.Value, "folder", out var folder) ||
            !TryGetProperty(data.Value, "to", out var toState))
        {
            return;
        }

        var newState = MapStateToFolderState(toState);
        UpdateFolderState(folder, newState);

        // Update syncing status
        var isSyncing = toState != "idle" && toState != "error";
        bool wasSyncing;

        lock (_stateLock)
        {
            wasSyncing = _folderSyncingStatus.TryGetValue(folder, out var s) && s;
            _folderSyncingStatus[folder] = isSyncing;
        }

        // Fire events
        if (!wasSyncing && isSyncing)
        {
            var args = new SyncActivityEventArgs
            {
                FolderId = folder,
                Timestamp = evt.Time
            };
            SyncStarted?.Invoke(this, args);
        }
        else if (wasSyncing && !isSyncing && toState == "idle")
        {
            var args = new SyncActivityEventArgs
            {
                FolderId = folder,
                Timestamp = evt.Time
            };
            SyncCompleted?.Invoke(this, args);
        }
    }

    private void HandleFolderSummary(SyncthingApiEvent evt)
    {
        var data = evt.Data as JsonElement?;
        if (!data.HasValue)
        {
            return;
        }

        if (!TryGetProperty(data.Value, "folder", out var folder))
        {
            return;
        }

        // Check if folder has pending items
        TryGetLongProperty(data.Value, "needBytes", out var needBytes);
        TryGetIntProperty(data.Value, "needFiles", out var needFiles);

        bool isSyncing = needBytes > 0 || needFiles > 0;

        lock (_stateLock)
        {
            _folderSyncingStatus[folder] = isSyncing;
        }
    }

    private void HandleFolderCompletion(SyncthingApiEvent evt)
    {
        var data = evt.Data as JsonElement?;
        if (!data.HasValue)
        {
            return;
        }

        if (!TryGetProperty(data.Value, "folder", out var folder))
        {
            return;
        }

        // Completion of 100% means sync is done
        if (TryGetDoubleProperty(data.Value, "completion", out var completion) && completion >= 100.0)
        {
            bool wasSyncing;
            lock (_stateLock)
            {
                wasSyncing = _folderSyncingStatus.TryGetValue(folder, out var s) && s;
                _folderSyncingStatus[folder] = false;
            }

            if (wasSyncing)
            {
                var args = new SyncActivityEventArgs
                {
                    FolderId = folder,
                    Timestamp = evt.Time
                };
                SyncCompleted?.Invoke(this, args);
                UpdateFolderState(folder, FolderSyncState.Idle);
            }
        }
    }

    private void HandleFolderPaused(SyncthingApiEvent evt)
    {
        var data = evt.Data as JsonElement?;
        if (!data.HasValue)
        {
            return;
        }

        if (TryGetProperty(data.Value, "id", out var folder))
        {
            UpdateFolderState(folder, FolderSyncState.Paused);
        }
    }

    private void HandleFolderResumed(SyncthingApiEvent evt)
    {
        var data = evt.Data as JsonElement?;
        if (!data.HasValue)
        {
            return;
        }

        if (TryGetProperty(data.Value, "id", out var folder))
        {
            UpdateFolderState(folder, FolderSyncState.Idle);
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

    private async Task<List<SyncthingApiEvent>?> GetEventsAsync(int since, int timeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient(_httpClientName);
            var url = $"/rest/events?since={since}&timeout={timeoutSeconds}";

            // Use longer timeout for long-polling
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds + 10));
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            using var response = await httpClient.GetAsync(url, combinedCts.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get events: {StatusCode}", response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(combinedCts.Token);

            if (string.IsNullOrWhiteSpace(json) || json == "null" || json == "[]")
            {
                return new List<SyncthingApiEvent>();
            }

            return JsonSerializer.Deserialize<List<SyncthingApiEvent>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? new List<SyncthingApiEvent>();
        }
        catch (OperationCanceledException)
        {
            return new List<SyncthingApiEvent>();
        }
    }

    private async Task<SyncthingFolderStatusDto?> GetFolderStatusAsync(string folderId, CancellationToken cancellationToken)
    {
        try
        {
            var httpClient = _httpClientFactory.CreateClient(_httpClientName);
            var url = $"/rest/db/status?folder={Uri.EscapeDataString(folderId)}";
            using var response = await httpClient.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<SyncthingFolderStatusDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting folder status for {FolderId}", folderId);
            return null;
        }
    }

    private static FolderSyncState MapStateToFolderState(string state)
    {
        return state?.ToLowerInvariant() switch
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

    private static bool TryGetProperty(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? string.Empty;
            return !string.IsNullOrEmpty(value);
        }
        return false;
    }

    private static bool TryGetLongProperty(JsonElement element, string name, out long value)
    {
        value = 0;
        if (element.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number)
            {
                value = prop.GetInt64();
                return true;
            }
        }
        return false;
    }

    private static bool TryGetIntProperty(JsonElement element, string name, out int value)
    {
        value = 0;
        if (element.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number)
            {
                value = prop.GetInt32();
                return true;
            }
        }
        return false;
    }

    private static bool TryGetDoubleProperty(JsonElement element, string name, out double value)
    {
        value = 0;
        if (element.TryGetProperty(name, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number)
            {
                value = prop.GetDouble();
                return true;
            }
        }
        return false;
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

    /// <summary>
    /// Syncthing API event
    /// </summary>
    private class SyncthingApiEvent
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("globalID")]
        public long GlobalId { get; set; }

        [JsonPropertyName("time")]
        public DateTime Time { get; set; }

        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("data")]
        public object? Data { get; set; }
    }

    /// <summary>
    /// Syncthing folder status DTO
    /// </summary>
    private class SyncthingFolderStatusDto
    {
        [JsonPropertyName("state")]
        public string State { get; set; } = string.Empty;

        [JsonPropertyName("globalBytes")]
        public long GlobalBytes { get; set; }

        [JsonPropertyName("needBytes")]
        public long NeedBytes { get; set; }

        [JsonPropertyName("needFiles")]
        public int NeedFiles { get; set; }

        [JsonPropertyName("needDeletes")]
        public int NeedDeletes { get; set; }
    }

    /// <summary>
    /// Syncthing folder config DTO
    /// </summary>
    private class SyncthingFolderConfig
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("label")]
        public string? Label { get; set; }
    }
}
