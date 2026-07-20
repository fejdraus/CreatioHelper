using CreatioHelper.Agent.Configuration;
using Microsoft.Extensions.Options;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using CreatioHelper.Shared.Utils;

namespace CreatioHelper.Agent.Services;

public class SyncthingAutoStopService : BackgroundService
{
    private readonly ILogger<SyncthingAutoStopService> _logger;
    private readonly SyncthingAutoStopSettings _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServiceProvider _serviceProvider;
    private int _servicesAreStopped = 0;
    private int _lastEventId = 0;
    private readonly SemaphoreSlim _completionCheckLock = new(1, 1);
    private bool _disposed = false;
    public SyncthingAutoStopService(
        ILogger<SyncthingAutoStopService> logger,
        IOptions<SyncthingAutoStopSettings> settings,
        IHttpClientFactory httpClientFactory,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _settings = settings.Value;
        _httpClientFactory = httpClientFactory;
        _serviceProvider = serviceProvider;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
        {
            _logger.LogInformation("SyncthingAutoStop is disabled in configuration");
            return;
        }
        if (_settings.MonitoredFolders.Count == 0)
        {
            _logger.LogWarning("No monitored folders configured for SyncthingAutoStop");
            return;
        }
        _logger.LogInformation("╔════════════════════════════════════════════════════════════╗");
        _logger.LogInformation("║  SyncthingAutoStop Service Started                        ║");
        _logger.LogInformation("╠════════════════════════════════════════════════════════════╣");
        _logger.LogInformation("║  Monitored folders: {FolderCount,-36} ║", _settings.MonitoredFolders.Count);
        _logger.LogInformation("║  Folders: {Folders,-46} ║", string.Join(", ", _settings.MonitoredFolders));
        _logger.LogInformation("║  Idle timeout: {Timeout,-43}s ║", _settings.IdleTimeoutSeconds);
        _logger.LogInformation("║  Using Events API (event-driven, low overhead)            ║");
        _logger.LogInformation("╚════════════════════════════════════════════════════════════╝");
        try
        {
            await MonitorSyncthingEventsAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("SyncthingAutoStop service stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in SyncthingAutoStop service");
            throw;
        }
        finally
        {
            if (AreServicesStopped())
            {
                _logger.LogInformation("Service shutting down - starting services");
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var serviceStateManager = scope.ServiceProvider.GetRequiredService<ServiceStateManager>();
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await serviceStateManager.StartServicesAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("Timeout while starting services during shutdown");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error starting services during shutdown");
                }
            }
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("SyncthingAutoStop service stopping...");

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("SyncthingAutoStop service stopped");
    }
    public override void Dispose()
    {
        if (!_disposed)
        {
            _completionCheckLock?.Dispose();
            _disposed = true;
            _logger.LogInformation("SyncthingAutoStop service resources disposed");
        }
        base.Dispose();
    }
        private async Task MonitorSyncthingEventsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var events = await GetEventsAsync(_lastEventId, 60, cancellationToken);
                if (events == null || events.Count == 0)
                {
                    continue;
                }
                _lastEventId = events.Max(e => e.Id);

                foreach (var evt in events)
                {
                    if (evt.Type != "ItemStarted" && evt.Type != "ItemFinished" &&
                        evt.Type != "StateChanged" && evt.Type != "DownloadProgress")
                    {
                        continue;
                    }
                    await ProcessEventAsync(evt, cancellationToken);
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "HTTP error connecting to Syncthing API at {ApiUrl}", _settings.SyncthingApiUrl);
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SyncthingAutoStop event monitoring loop");
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
    }
        private async Task ProcessEventAsync(SyncthingEvent evt, CancellationToken cancellationToken)
    {
        if (!IsMonitoredFolder(evt))
        {
            return;
        }

        switch (evt.Type)
        {
            case "ItemStarted":
                await HandleItemStartedAsync(evt, cancellationToken);
                break;
            case "ItemFinished":
                await HandleItemFinishedAsync(evt, cancellationToken);
                break;
            case "StateChanged":
                await HandleStateChangedAsync(evt, cancellationToken);
                break;
            case "DownloadProgress":
                break;
        }
    }
    private async Task HandleItemStartedAsync(SyncthingEvent evt, CancellationToken cancellationToken)
    {
        var folderData = evt.Data as JsonElement?;
        if (!folderData.HasValue)
        {
            _logger.LogDebug("Event data is null for ItemStarted event, skipping");
            return;
        }
        if (!folderData.Value.TryGetProperty("folder", out var folderProp) ||
            !folderData.Value.TryGetProperty("item", out var itemProp) ||
            !folderData.Value.TryGetProperty("action", out var actionProp))
        {
            _logger.LogWarning("ItemStarted event missing required properties (folder/item/action)");
            return;
        }
        var folder = folderProp.GetString();
        var item = itemProp.GetString();
        var action = actionProp.GetString();
        if (string.IsNullOrEmpty(action))
        {
            _logger.LogDebug("ItemStarted event has null action, skipping");
            return;
        }

        _logger.LogWarning("┌────────────────────────────────────────────────────────────┐");
        _logger.LogWarning("│ 🔄 INCOMING SYNCHRONIZATION DETECTED                       │");
        _logger.LogWarning("├────────────────────────────────────────────────────────────┤");
        _logger.LogWarning("│ Folder: {Folder,-50} │", folder ?? "unknown");
        _logger.LogWarning("│ File: {File,-52} │", item ?? "unknown");
        _logger.LogWarning("│ Action: {Action,-50} │", action ?? "unknown");
        _logger.LogWarning("└────────────────────────────────────────────────────────────┘");

        using var scope = _serviceProvider.CreateScope();
        var serviceStateManager = scope.ServiceProvider.GetRequiredService<ServiceStateManager>();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_settings.ServiceOperationTimeoutSeconds));
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
    private async Task HandleItemFinishedAsync(SyncthingEvent evt, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Item finished - checking if sync complete");
        await CheckAndStartServicesAsync(cancellationToken);
    }
    private async Task HandleStateChangedAsync(SyncthingEvent evt, CancellationToken cancellationToken)
    {
        var stateData = evt.Data as JsonElement?;
        if (!stateData.HasValue)
        {
            _logger.LogDebug("StateChanged event data is null, skipping");
            return;
        }
        if (!stateData.Value.TryGetProperty("to", out var toProp))
        {
            _logger.LogWarning("StateChanged event missing 'to' property");
            return;
        }
        stateData.Value.TryGetProperty("folder", out var folderProp);
        stateData.Value.TryGetProperty("from", out var fromProp);

        var to = toProp.GetString();
        var folder = folderProp.ValueKind != JsonValueKind.Undefined ? folderProp.GetString() : null;
        var from = fromProp.ValueKind != JsonValueKind.Undefined ? fromProp.GetString() : null;
        if (string.IsNullOrEmpty(to))
        {
            _logger.LogDebug("StateChanged event has null 'to' state, skipping");
            return;
        }

        _logger.LogDebug("State changed for folder {Folder}: {From} -> {To}", folder, from, to);
        if (to == "idle")
        {
            _logger.LogInformation("⏳ Folder {Folder} became idle - checking if sync complete", folder);
            await CheckAndStartServicesAsync(cancellationToken);
        }
    }
    private async Task CheckAndStartServicesAsync(CancellationToken cancellationToken)
    {

        if (!await _completionCheckLock.WaitAsync(0))
        {
            _logger.LogDebug("Completion check already in progress, skipping duplicate");
            return;
        }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AreServicesStopped())
            {
                _logger.LogDebug("Services not stopped, skipping completion check");
                return;
            }
            using var scope = _serviceProvider.CreateScope();
            var completionMonitor = scope.ServiceProvider.GetRequiredService<SyncthingCompletionMonitor>();
            var serviceStateManager = scope.ServiceProvider.GetRequiredService<ServiceStateManager>();

        _logger.LogInformation("┌────────────────────────────────────────────────────────────┐");
        _logger.LogInformation("│ ⏳ WAITING FOR SYNCHRONIZATION TO COMPLETE                 │");
        _logger.LogInformation("├────────────────────────────────────────────────────────────┤");
        _logger.LogInformation("│ Idle timeout: {Timeout,-46}s │", _settings.IdleTimeoutSeconds);
        _logger.LogInformation("│ Required stable checks: {Checks,-36} │", _settings.RequiredStableChecks);
        _logger.LogInformation("└────────────────────────────────────────────────────────────┘");

        bool syncCompleted = await completionMonitor.WaitForSyncCompletionAsync(
            _settings.MonitoredFolders,
            _settings.IdleTimeoutSeconds,
            _settings.CompletionCheckIntervalSeconds,
            _settings.RequiredStableChecks,
            _settings.MaxSyncWaitTimeMinutes,
            cancellationToken);
        if (syncCompleted)
        {
            _logger.LogInformation("┌────────────────────────────────────────────────────────────┐");
            _logger.LogInformation("│ ✅ SYNCHRONIZATION COMPLETED AND STABLE                    │");
            _logger.LogInformation("└────────────────────────────────────────────────────────────┘");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_settings.ServiceOperationTimeoutSeconds));
            bool started = await serviceStateManager.StartServicesAsync(timeoutCts.Token);

            if (started)
            {
                SetServicesStoppedState(false);
                _logger.LogInformation("🚀 Services started successfully");
                _logger.LogInformation("");
            }
            else
            {
                _logger.LogWarning("⚠️  Failed to start services");
                SetServicesStoppedState(false);
                _logger.LogWarning("Resetting services state to prevent infinite retry attempts");
            }
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
        private bool IsMonitoredFolder(SyncthingEvent evt)
    {
        if (evt.Data == null)
            return false;
        try
        {
            var data = evt.Data as JsonElement?;
            if (data?.TryGetProperty("folder", out var folderProp) == true)
            {
                var folder = folderProp.GetString();
                return folder != null && _settings.MonitoredFolders.Contains(folder);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing event data for folder detection");
        }

        return false;
    }
        private async Task<List<SyncthingEvent>?> GetEventsAsync(int since, int timeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            using var httpClient = _httpClientFactory.CreateClient("Syncthing");
            var url = $"/rest/events?since={since}&timeout={timeoutSeconds}";
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds + 10));
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token
            );

            using var response = await httpClient.GetAsync(url, combinedCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get events: {StatusCode}", response.StatusCode);
                return null;
            }
            var json = await response.Content.ReadAsStringAsync(combinedCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json) || json == "null" || json == "[]")
            {
                return new List<SyncthingEvent>();
            }
            var events = JsonSerializer.Deserialize<List<SyncthingEvent>>(json, JsonDefaults.CaseInsensitive);
            return events ?? new List<SyncthingEvent>();
        }
        catch (OperationCanceledException)
        {
            return new List<SyncthingEvent>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting events from Syncthing API");
            return null;
        }
    }
        private bool AreServicesStopped() => Interlocked.CompareExchange(ref _servicesAreStopped, 0, 0) == 1;

        private void SetServicesStoppedState(bool stopped) => Interlocked.Exchange(ref _servicesAreStopped, stopped ? 1 : 0);

        private bool TrySetServicesStoppedState() => Interlocked.CompareExchange(ref _servicesAreStopped, 1, 0) == 0;
}
internal class SyncthingEvent
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