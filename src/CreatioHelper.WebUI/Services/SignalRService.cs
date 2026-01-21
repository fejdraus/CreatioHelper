using CreatioHelper.WebUI.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CreatioHelper.WebUI.Services;

/// <summary>
/// SignalR service for real-time updates from the Agent
/// </summary>
public interface ISignalRService : IAsyncDisposable
{
    bool IsConnected { get; }
    bool IsConnecting { get; }

    event Action<SystemStatus>? OnSystemStatusChanged;
    event Action<FolderStatus>? OnFolderStatusChanged;
    event Action<SyncEvent>? OnSyncEvent;
    event Action<ConnectionInfo>? OnConnectionChanged;
    event Action<string>? OnLogMessage;
    event Action<LogEntry>? OnLogEntry;
    event Action<bool>? OnConnectionStateChanged;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync();
}

public class SignalRService : ISignalRService
{
    private HubConnection? _hubConnection;
    private readonly string _hubUrl;
    private readonly IAuthService _authService;
    private bool _disposed;

    public bool IsConnected => _hubConnection?.State == HubConnectionState.Connected;
    public bool IsConnecting => _hubConnection?.State == HubConnectionState.Connecting ||
                                _hubConnection?.State == HubConnectionState.Reconnecting;

    public event Action<SystemStatus>? OnSystemStatusChanged;
    public event Action<FolderStatus>? OnFolderStatusChanged;
    public event Action<SyncEvent>? OnSyncEvent;
    public event Action<ConnectionInfo>? OnConnectionChanged;
    public event Action<string>? OnLogMessage;
    public event Action<LogEntry>? OnLogEntry;
    public event Action<bool>? OnConnectionStateChanged;

    public SignalRService(IConfiguration configuration, IAuthService authService)
    {
        var baseUrl = configuration["ApiBaseUrl"] ?? "";
        _hubUrl = $"{baseUrl}/syncHub";
        _authService = authService;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_hubConnection != null)
        {
            await DisconnectAsync();
        }

        _hubConnection = new HubConnectionBuilder()
            .WithUrl(_hubUrl, options =>
            {
                // Pass the JWT token for authentication
                options.AccessTokenProvider = () => Task.FromResult(_authService.Token);
            })
            .WithAutomaticReconnect(new RetryPolicy())
            .Build();

        // Register event handlers
        _hubConnection.On<SystemStatus>("SystemStatus", status =>
        {
            OnSystemStatusChanged?.Invoke(status);
        });

        _hubConnection.On<FolderStatus>("FolderStatus", status =>
        {
            OnFolderStatusChanged?.Invoke(status);
        });

        _hubConnection.On<SyncEvent>("SyncEvent", evt =>
        {
            OnSyncEvent?.Invoke(evt);
        });

        _hubConnection.On<ConnectionInfo>("ConnectionChanged", info =>
        {
            OnConnectionChanged?.Invoke(info);
        });

        _hubConnection.On<string>("LogMessage", message =>
        {
            OnLogMessage?.Invoke(message);
        });

        _hubConnection.On<LogEntry>("LogEntry", entry =>
        {
            OnLogEntry?.Invoke(entry);
        });

        _hubConnection.Reconnecting += (error) =>
        {
            OnConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        _hubConnection.Reconnected += (connectionId) =>
        {
            OnConnectionStateChanged?.Invoke(true);
            return Task.CompletedTask;
        };

        _hubConnection.Closed += (error) =>
        {
            OnConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        try
        {
            await _hubConnection.StartAsync(cancellationToken);
            OnConnectionStateChanged?.Invoke(true);
        }
        catch (Exception)
        {
            OnConnectionStateChanged?.Invoke(false);
            throw;
        }
    }

    public async Task DisconnectAsync()
    {
        if (_hubConnection != null)
        {
            await _hubConnection.DisposeAsync();
            _hubConnection = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            await DisconnectAsync();
            _disposed = true;
        }
    }

    private class RetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] RetryDelays =
        [
            TimeSpan.FromSeconds(0),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60)
        ];

        public TimeSpan? NextRetryDelay(RetryContext retryContext)
        {
            var index = Math.Min(retryContext.PreviousRetryCount, RetryDelays.Length - 1);
            return RetryDelays[index];
        }
    }
}

/// <summary>
/// Configuration for ISignalRService - used in DI registration
/// </summary>
public interface IConfiguration
{
    string? this[string key] { get; }
}

/// <summary>
/// Simple configuration implementation that reads from base URI
/// </summary>
public class SignalRConfiguration : IConfiguration
{
    private readonly Uri _baseAddress;

    public SignalRConfiguration(HttpClient httpClient)
    {
        _baseAddress = httpClient.BaseAddress ?? new Uri("http://localhost:8384");
    }

    public string? this[string key] => key switch
    {
        "ApiBaseUrl" => _baseAddress.ToString().TrimEnd('/'),
        _ => null
    };
}
