using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Models;
using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.Services;

/// <summary>
/// Service for listening to Syncthing Events API in real-time
/// Uses long polling to receive events as they occur
/// Based on Syncthing Events API: GET /rest/events
/// </summary>
public class SyncthingEventsListener : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IOutputWriter _output;
    private long _lastEventId;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private bool _isRunning;

    // Events for external subscription
    public event Action<StateChangedEventData>? OnStateChanged;
    public event Action<FolderCompletionEventData>? OnFolderCompletion;
    public event Action<ItemStartedEventData>? OnItemStarted;
    public event Action<ItemFinishedEventData>? OnItemFinished;
    public event Action<DeviceConnectedEventData>? OnDeviceConnected;
    public event Action<DeviceDisconnectedEventData>? OnDeviceDisconnected;
    public event Action<Exception>? OnError;

    public SyncthingEventsListener(
        IHttpClientFactory httpClientFactory,
        IOutputWriter output,
        string apiUrl,
        string? apiKey)
    {
        _httpClient = httpClientFactory.CreateClient("Syncthing");
        _output = output;

        _httpClient.BaseAddress = new Uri(apiUrl);
        _httpClient.Timeout = TimeSpan.FromSeconds(70); // Syncthing timeout is 60s + buffer

        if (!string.IsNullOrEmpty(apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        }
    }

    /// <summary>
    /// Start listening to Syncthing events in background
    /// </summary>
    public void Start()
    {
        if (_isRunning)
        {
            _output.WriteLine("[WARNING] SyncthingEventsListener is already running");
            return;
        }
        _cts = new CancellationTokenSource();
        _isRunning = true;

        // Start background listener task
        _listenerTask = Task.Run(async () => await ListenLoopAsync(_cts.Token), _cts.Token);
    }

    /// <summary>
    /// Stop listening to events
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning)
            return;

        _output.WriteLine("[INFO] Stopping Syncthing Events Listener");
        _isRunning = false;

        if (_cts != null)
        {
            await _cts.CancelAsync();
        }

        if (_listenerTask != null)
        {
            try
            {
                await _listenerTask;
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
            }
        }

        _cts?.Dispose();
        _cts = null;
        _listenerTask = null;
    }

    /// <summary>
    /// Main long polling loop
    /// </summary>
    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // Build request URL with event filters
                // We're interested in: StateChanged, FolderCompletion, ItemStarted, ItemFinished, DeviceConnected, DeviceDisconnected
                var eventTypes = string.Join(",", "StateChanged", "FolderCompletion", "ItemStarted", "ItemFinished", "DeviceConnected", "DeviceDisconnected");

                var url = $"/rest/events?since={_lastEventId}&events={eventTypes}&timeout=60";

                var response = await _httpClient.GetAsync(url, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _output.WriteLine($"[ERROR] Events API returned {response.StatusCode}");
                    await Task.Delay(5000, cancellationToken); // Wait 5s before retry
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(cancellationToken);

                // Parse event array
                var events = JsonSerializer.Deserialize<List<SyncthingEvent>>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (events is not { Count: > 0 }) continue;
                foreach (var evt in events)
                {
                    _lastEventId = evt.Id;
                    ProcessEvent(evt);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
                break;
            }
            catch (HttpRequestException ex)
            {
                _output.WriteLine($"[ERROR] HTTP error in events listener: {ex.Message}");
                OnError?.Invoke(ex);

                // Wait before retry
                await Task.Delay(5000, cancellationToken);
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[ERROR] Unexpected error in events listener: {ex.Message}");
                OnError?.Invoke(ex);

                // Wait before retry
                await Task.Delay(5000, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Process a single event and invoke appropriate event handlers
    /// </summary>
    private void ProcessEvent(SyncthingEvent evt)
    {
        try
        {
            switch (evt.Type)
            {
                case "StateChanged":
                    var stateData = JsonSerializer.Deserialize<StateChangedEventData>(
                        evt.Data.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );
                    if (stateData != null)
                    {
                        OnStateChanged?.Invoke(stateData);
                    }
                    break;

                case "FolderCompletion":
                    var completionData = JsonSerializer.Deserialize<FolderCompletionEventData>(
                        evt.Data.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );
                    if (completionData != null)
                    {
                        OnFolderCompletion?.Invoke(completionData);
                    }
                    break;

                case "ItemStarted":
                    var itemStartedData = JsonSerializer.Deserialize<ItemStartedEventData>(
                        evt.Data.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );
                    if (itemStartedData != null)
                    {
                        OnItemStarted?.Invoke(itemStartedData);
                    }
                    break;

                case "ItemFinished":
                    var itemFinishedData = JsonSerializer.Deserialize<ItemFinishedEventData>(
                        evt.Data.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );
                    if (itemFinishedData != null)
                    {
                        OnItemFinished?.Invoke(itemFinishedData);
                    }
                    break;

                case "DeviceConnected":
                    var deviceConnectedData = JsonSerializer.Deserialize<DeviceConnectedEventData>(
                        evt.Data.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );
                    if (deviceConnectedData != null)
                    {
                        OnDeviceConnected?.Invoke(deviceConnectedData);
                    }
                    break;

                case "DeviceDisconnected":
                    var deviceDisconnectedData = JsonSerializer.Deserialize<DeviceDisconnectedEventData>(
                        evt.Data.GetRawText(),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
                    );
                    if (deviceDisconnectedData != null)
                    {
                        OnDeviceDisconnected?.Invoke(deviceDisconnectedData);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to process event {evt.Type}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _httpClient.Dispose();
    }
}
