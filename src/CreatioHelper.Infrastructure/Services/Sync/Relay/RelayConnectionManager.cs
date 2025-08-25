using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services.Sync.Relay;

/// <summary>
/// Manages relay connections and integrates with SyncEngine
/// Handles connecting to relay servers and establishing connections through relays
/// </summary>
public class RelayConnectionManager : IDisposable
{
    private readonly ILogger<RelayConnectionManager> _logger;
    private readonly X509Certificate2 _clientCertificate;
    private readonly ConcurrentDictionary<string, RelayClient> _relayClients = new();
    private readonly ConcurrentDictionary<string, List<string>> _deviceRelayMap = new();
    private readonly Timer _healthCheckTimer;
    private volatile bool _disposed;

    // Default public relay servers (can be configured)
    private readonly List<string> _defaultRelayServers = new()
    {
        "relay://relay1.syncthing.net:22067",
        "relay://relay2.syncthing.net:22067",
        "relay://relay3.syncthing.net:22067",
        "relay://relay4.syncthing.net:22067"
    };

    public RelayConnectionManager(ILogger<RelayConnectionManager> logger, X509Certificate2 clientCertificate)
    {
        _logger = logger;
        _clientCertificate = clientCertificate;
        
        // Setup periodic health checks
        _healthCheckTimer = new Timer(HealthCheckCallback, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Event fired when a relay connection invitation is received
    /// </summary>
    public event EventHandler<RelayConnectionEventArgs>? RelayConnectionReceived;

    /// <summary>
    /// Connect to the specified relay servers
    /// </summary>
    public async Task ConnectToRelaysAsync(List<string>? relayUris = null)
    {
        var uris = relayUris ?? _defaultRelayServers;
        
        _logger.LogInformation("Connecting to {RelayCount} relay servers", uris.Count);

        var connectTasks = uris.Select(uri => ConnectToRelayAsync(uri)).ToArray();
        await Task.WhenAll(connectTasks);

        var connectedCount = _relayClients.Count(kvp => kvp.Value.IsConnected);
        _logger.LogInformation("Connected to {ConnectedCount}/{TotalCount} relay servers", connectedCount, uris.Count);
    }

    /// <summary>
    /// Connect to a specific relay server
    /// </summary>
    public async Task<bool> ConnectToRelayAsync(string relayUri, string? token = null)
    {
        try
        {
            if (!Uri.TryCreate(relayUri, UriKind.Absolute, out var uri))
            {
                _logger.LogWarning("Invalid relay URI: {RelayUri}", relayUri);
                return false;
            }

            if (_relayClients.ContainsKey(relayUri))
            {
                _logger.LogDebug("Already connected to relay: {RelayUri}", relayUri);
                return true;
            }

            var clientLogger = Microsoft.Extensions.Logging.Abstractions.NullLogger<RelayClient>.Instance;
            var relayClient = new RelayClient(clientLogger, uri, _clientCertificate, TimeSpan.FromSeconds(10));
            
            // Subscribe to session invitations
            relayClient.SessionInvitationReceived += OnSessionInvitationReceived;

            var connected = await relayClient.ConnectAsync(token);
            if (connected)
            {
                _relayClients[relayUri] = relayClient;
                _logger.LogInformation("Connected to relay server: {RelayUri}", relayUri);
                return true;
            }
            else
            {
                relayClient.Dispose();
                _logger.LogWarning("Failed to connect to relay server: {RelayUri}", relayUri);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error connecting to relay server: {RelayUri}", relayUri);
            return false;
        }
    }

    /// <summary>
    /// Attempt to connect to a device through available relays
    /// </summary>
    public async Task<Stream?> ConnectThroughRelayAsync(string deviceId, TimeSpan timeout)
    {
        _logger.LogDebug("Attempting to connect to device {DeviceId} through relay", deviceId);

        var deviceIdBytes = Convert.FromHexString(deviceId);
        var connectedRelays = _relayClients.Where(kvp => kvp.Value.IsConnected).ToList();

        if (connectedRelays.Count == 0)
        {
            _logger.LogWarning("No relay connections available for device {DeviceId}", deviceId);
            return null;
        }

        // Try each relay in parallel
        var connectionTasks = connectedRelays.Select(async kvp =>
        {
            try
            {
                var (relayUri, client) = kvp;
                _logger.LogTrace("Requesting connection to {DeviceId} via relay {RelayUri}", deviceId, relayUri);
                
                var invitation = await client.RequestConnectionAsync(deviceIdBytes, timeout);
                if (invitation != null)
                {
                    _logger.LogDebug("Received invitation from relay {RelayUri} for device {DeviceId}", relayUri, deviceId);
                    return await client.JoinSessionAsync(invitation);
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "Failed to connect through relay for device {DeviceId}", deviceId);
                return null;
            }
        }).ToArray();

        // Wait for the first successful connection
        try
        {
            var results = await Task.WhenAll(connectionTasks);
            var successfulConnection = results.FirstOrDefault(stream => stream != null);

            if (successfulConnection != null)
            {
                _logger.LogInformation("Successfully connected to device {DeviceId} through relay", deviceId);
                return successfulConnection;
            }
            else
            {
                _logger.LogWarning("Failed to connect to device {DeviceId} through any relay", deviceId);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error connecting to device {DeviceId} through relay", deviceId);
            return null;
        }
    }

    /// <summary>
    /// Get the number of connected relays
    /// </summary>
    public int GetConnectedRelayCount()
    {
        return _relayClients.Count(kvp => kvp.Value.IsConnected);
    }

    /// <summary>
    /// Get information about connected relays
    /// </summary>
    public List<RelayInfo> GetRelayInfo()
    {
        return _relayClients.Select(kvp => new RelayInfo
        {
            Uri = kvp.Key,
            IsConnected = kvp.Value.IsConnected
        }).ToList();
    }

    private void OnSessionInvitationReceived(object? sender, SessionInvitation invitation)
    {
        _logger.LogDebug("Received relay session invitation: {Invitation}", invitation);
        
        var relayClient = sender as RelayClient;
        var relayUri = _relayClients.FirstOrDefault(kvp => kvp.Value == relayClient).Key;
        
        var eventArgs = new RelayConnectionEventArgs(invitation, relayClient, relayUri);
        RelayConnectionReceived?.Invoke(this, eventArgs);
    }

    private async void HealthCheckCallback(object? state)
    {
        try
        {
            await PerformHealthChecksAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during relay health check");
        }
    }

    private async Task PerformHealthChecksAsync()
    {
        var healthCheckTasks = _relayClients.ToList().Select(async kvp =>
        {
            var (relayUri, client) = kvp;
            try
            {
                if (client.IsConnected)
                {
                    var pingSuccessful = await client.PingAsync();
                    if (!pingSuccessful)
                    {
                        _logger.LogWarning("Ping failed for relay {RelayUri}", relayUri);
                    }
                }
                else
                {
                    _logger.LogDebug("Relay {RelayUri} is disconnected", relayUri);
                    // Attempt to reconnect
                    await client.ConnectAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Health check failed for relay {RelayUri}", relayUri);
            }
        }).ToArray();

        await Task.WhenAll(healthCheckTasks);
    }

    /// <summary>
    /// Disconnect from all relays
    /// </summary>
    public async Task DisconnectAllAsync()
    {
        _logger.LogInformation("Disconnecting from all relay servers");

        var disconnectTasks = _relayClients.Values.Select(client => client.DisconnectAsync()).ToArray();
        await Task.WhenAll(disconnectTasks);

        foreach (var client in _relayClients.Values)
        {
            client.Dispose();
        }

        _relayClients.Clear();
        _deviceRelayMap.Clear();

        _logger.LogInformation("Disconnected from all relay servers");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        try
        {
            DisconnectAllAsync().Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during disposal");
        }

        _healthCheckTimer.Dispose();
    }
}

/// <summary>
/// Information about a relay server
/// </summary>
public class RelayInfo
{
    public string Uri { get; set; } = string.Empty;
    public bool IsConnected { get; set; }
}

/// <summary>
/// Event arguments for relay connection events
/// </summary>
public class RelayConnectionEventArgs : EventArgs
{
    public SessionInvitation Invitation { get; }
    public RelayClient? RelayClient { get; }
    public string? RelayUri { get; }

    public RelayConnectionEventArgs(SessionInvitation invitation, RelayClient? relayClient, string? relayUri)
    {
        Invitation = invitation;
        RelayClient = relayClient;
        RelayUri = relayUri;
    }
}