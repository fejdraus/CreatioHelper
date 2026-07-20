using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Models;
using CreatioHelper.Shared.Interfaces;
using CreatioHelper.Shared.Utils;

namespace CreatioHelper.Services;

public class SyncthingEventsListener : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IOutputWriter _output;
    private readonly SyncthingRequestFactory _requests;
    private long _lastEventId;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;
    private bool _isRunning;
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
        _requests = new SyncthingRequestFactory(apiUrl, apiKey);
    }
        public void Start()
    {
        if (_isRunning)
        {
            _output.WriteLine("[WARNING] SyncthingEventsListener is already running");
            return;
        }
        _cts = new CancellationTokenSource();
        _isRunning = true;
        _listenerTask = Task.Run(async () => await ListenLoopAsync(_cts.Token), _cts.Token);
    }
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
            }
        }
        _cts?.Dispose();
        _cts = null;
        _listenerTask = null;
    }
        private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var eventTypes = string.Join(",", "StateChanged", "FolderCompletion", "ItemStarted", "ItemFinished", "DeviceConnected", "DeviceDisconnected");
                using var request = _requests.Get(
                    $"/rest/events?since={_lastEventId}&events={eventTypes}&timeout=60");
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(70));
                var response = await _httpClient.SendAsync(request, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    _output.WriteLine($"[ERROR] Events API returned {response.StatusCode}");
                    await Task.Delay(5000, cancellationToken);
                    continue;
                }
                var json = await response.Content.ReadAsStringAsync(cancellationToken);

                var events = JsonSerializer.Deserialize<List<SyncthingEvent>>(json, JsonDefaults.CaseInsensitive);

                if (events is not { Count: > 0 }) continue;
                foreach (var evt in events)
                {
                    _lastEventId = evt.Id;
                    ProcessEvent(evt);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpRequestException ex)
            {
                _output.WriteLine($"[ERROR] HTTP error in events listener: {ex.Message}");
                OnError?.Invoke(ex);
                await Task.Delay(5000, cancellationToken);
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[ERROR] Unexpected error in events listener: {ex.Message}");
                OnError?.Invoke(ex);
                await Task.Delay(5000, cancellationToken);
            }
        }
    }

        private void ProcessEvent(SyncthingEvent evt)
    {
        try
        {
            switch (evt.Type)
            {
                case "StateChanged":
                    var stateData = JsonSerializer.Deserialize<StateChangedEventData>(
                        evt.Data.GetRawText(),
                        JsonDefaults.CaseInsensitive
                    );
                    if (stateData != null)
                    {
                        OnStateChanged?.Invoke(stateData);
                    }
                    break;
                case "FolderCompletion":
                    var completionData = JsonSerializer.Deserialize<FolderCompletionEventData>(
                        evt.Data.GetRawText(),
                        JsonDefaults.CaseInsensitive
                    );
                    if (completionData != null)
                    {
                        OnFolderCompletion?.Invoke(completionData);
                    }
                    break;
                case "ItemStarted":
                    var itemStartedData = JsonSerializer.Deserialize<ItemStartedEventData>(
                        evt.Data.GetRawText(),
                        JsonDefaults.CaseInsensitive
                    );
                    if (itemStartedData != null)
                    {
                        OnItemStarted?.Invoke(itemStartedData);
                    }
                    break;
                case "ItemFinished":
                    var itemFinishedData = JsonSerializer.Deserialize<ItemFinishedEventData>(
                        evt.Data.GetRawText(),
                        JsonDefaults.CaseInsensitive
                    );
                    if (itemFinishedData != null)
                    {
                        OnItemFinished?.Invoke(itemFinishedData);
                    }
                    break;
                case "DeviceConnected":
                    var deviceConnectedData = JsonSerializer.Deserialize<DeviceConnectedEventData>(
                        evt.Data.GetRawText(),
                        JsonDefaults.CaseInsensitive
                    );
                    if (deviceConnectedData != null)
                    {
                        OnDeviceConnected?.Invoke(deviceConnectedData);
                    }
                    break;
                case "DeviceDisconnected":
                    var deviceDisconnectedData = JsonSerializer.Deserialize<DeviceDisconnectedEventData>(
                        evt.Data.GetRawText(),
                        JsonDefaults.CaseInsensitive
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