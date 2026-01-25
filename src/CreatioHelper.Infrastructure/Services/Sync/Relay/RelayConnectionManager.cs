using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Network.Connection;

namespace CreatioHelper.Infrastructure.Services.Sync.Relay;

/// <summary>
/// Manages relay connections and integrates with SyncEngine
/// Handles connecting to relay servers and establishing connections through relays
/// </summary>
public class RelayConnectionManager : IDisposable
{
    private readonly ILogger<RelayConnectionManager> _logger;
    private readonly X509Certificate2 _clientCertificate;
    private readonly IConnectionPrioritizer? _connectionPrioritizer;
    private readonly ConcurrentDictionary<string, RelayClient> _relayClients = new();
    private readonly ConcurrentDictionary<string, List<string>> _deviceRelayMap = new();
    private readonly ConcurrentDictionary<string, RelayReconnectionState> _reconnectionStates = new();
    private readonly ConcurrentDictionary<string, int> _relayPriorities = new();
    private readonly Timer _healthCheckTimer;
    private volatile bool _disposed;

    // Exponential backoff configuration
    private const int MinReconnectDelaySeconds = 5;
    private const int MaxReconnectDelaySeconds = 300; // 5 minutes max
    private const double BackoffMultiplier = 2.0;
    private const int MaxConsecutiveFailures = 10;

    // Default public relay servers (can be configured)
    private readonly List<string> _defaultRelayServers = new()
    {
        "relay://relay1.syncthing.net:22067",
        "relay://relay2.syncthing.net:22067",
        "relay://relay3.syncthing.net:22067",
        "relay://relay4.syncthing.net:22067"
    };

    public RelayConnectionManager(
        ILogger<RelayConnectionManager> logger,
        X509Certificate2 clientCertificate,
        IConnectionPrioritizer? connectionPrioritizer = null)
    {
        _logger = logger;
        _clientCertificate = clientCertificate;
        _connectionPrioritizer = connectionPrioritizer;

        // Setup periodic health checks
        _healthCheckTimer = new Timer(HealthCheckCallback, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// Gets the connection prioritizer if available
    /// </summary>
    public IConnectionPrioritizer? ConnectionPrioritizer => _connectionPrioritizer;

    /// <summary>
    /// Event fired when a relay connection invitation is received
    /// </summary>
    public event EventHandler<RelayConnectionEventArgs>? RelayConnectionReceived;

    /// <summary>
    /// Connect to the specified relay servers, optionally prioritized
    /// </summary>
    public async Task ConnectToRelaysAsync(List<string>? relayUris = null)
    {
        var uris = relayUris ?? _defaultRelayServers;

        _logger.LogInformation("Connecting to {RelayCount} relay servers", uris.Count);

        // Use prioritization if available
        if (_connectionPrioritizer != null)
        {
            var prioritizedRelays = _connectionPrioritizer.PrioritizeAddresses(uris);

            _logger.LogDebug("Prioritized relay servers: {Relays}",
                string.Join(", ", prioritizedRelays.Select(p => $"{p.Address}(p:{p.Priority})")));

            // Connect to relays in priority order within buckets
            var buckets = _connectionPrioritizer.GetPriorityBuckets(uris);
            foreach (var bucket in buckets)
            {
                var bucketTasks = bucket.Select(async p =>
                {
                    var success = await ConnectToRelayAsync(p.Address);
                    if (success)
                    {
                        _relayPriorities[p.Address] = p.Priority;
                    }
                    return success;
                }).ToArray();

                await Task.WhenAll(bucketTasks);
            }
        }
        else
        {
            // Connect without prioritization
            var connectTasks = uris.Select(uri => ConnectToRelayAsync(uri)).ToArray();
            await Task.WhenAll(connectTasks);
        }

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

                // Calculate and store priority for this relay
                if (_connectionPrioritizer != null)
                {
                    var priority = _connectionPrioritizer.CalculatePriority(relayUri);
                    _relayPriorities[relayUri] = priority;
                    _logger.LogInformation("Connected to relay server: {RelayUri} (priority: {Priority})", relayUri, priority);
                }
                else
                {
                    _logger.LogInformation("Connected to relay server: {RelayUri}", relayUri);
                }

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
            IsConnected = kvp.Value.IsConnected,
            Priority = _relayPriorities.TryGetValue(kvp.Key, out var priority) ? priority : null,
            ConnectionType = _connectionPrioritizer?.GetConnectionType(kvp.Key)
        }).ToList();
    }

    /// <summary>
    /// Get prioritized list of connected relay addresses
    /// </summary>
    public IEnumerable<PrioritizedAddress> GetPrioritizedRelayAddresses()
    {
        if (_connectionPrioritizer == null)
        {
            // Return addresses with default relay priority
            return _relayClients
                .Where(kvp => kvp.Value.IsConnected)
                .Select(kvp => new PrioritizedAddress
                {
                    Address = kvp.Key,
                    Priority = 90, // Default relay priority
                    Type = ConnectionType.Relay,
                    IsLan = false
                });
        }

        var connectedRelays = _relayClients
            .Where(kvp => kvp.Value.IsConnected)
            .Select(kvp => kvp.Key);

        return _connectionPrioritizer.PrioritizeAddresses(connectedRelays);
    }

    /// <summary>
    /// Get the connection priority for a specific relay
    /// </summary>
    public int GetRelayPriority(string relayUri)
    {
        if (_relayPriorities.TryGetValue(relayUri, out var priority))
        {
            return priority;
        }

        return _connectionPrioritizer?.CalculatePriority(relayUri)
            ?? 90; // Default relay priority
    }

    /// <summary>
    /// Check if the current relay connection should be upgraded to a different connection type
    /// </summary>
    public bool ShouldUpgradeFromRelay(string newConnectionAddress)
    {
        if (_connectionPrioritizer == null)
        {
            return false;
        }

        var relayPriority = _connectionPrioritizer.Configuration.RelayPriority;
        var newPriority = _connectionPrioritizer.CalculatePriority(newConnectionAddress);
        var threshold = _connectionPrioritizer.Configuration.UpgradeThreshold;

        return _connectionPrioritizer.ShouldUpgrade(relayPriority, newPriority, threshold);
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
                    if (pingSuccessful)
                    {
                        // Reset reconnection state on successful ping
                        ResetReconnectionState(relayUri);
                    }
                    else
                    {
                        _logger.LogWarning("Ping failed for relay {RelayUri}", relayUri);
                    }
                }
                else
                {
                    _logger.LogDebug("Relay {RelayUri} is disconnected", relayUri);
                    // Attempt to reconnect with exponential backoff
                    await AttemptReconnectWithBackoffAsync(relayUri, client);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Health check failed for relay {RelayUri}", relayUri);
                RecordReconnectionFailure(relayUri);
            }
        }).ToArray();

        await Task.WhenAll(healthCheckTasks);
    }

    /// <summary>
    /// Attempt to reconnect to a relay with exponential backoff
    /// </summary>
    private async Task AttemptReconnectWithBackoffAsync(string relayUri, RelayClient client)
    {
        var state = _reconnectionStates.GetOrAdd(relayUri, _ => new RelayReconnectionState());

        // Check if we should attempt reconnection based on backoff
        if (!ShouldAttemptReconnection(state))
        {
            var nextAttemptIn = state.NextReconnectTime - DateTime.UtcNow;
            _logger.LogDebug("Skipping reconnection attempt for relay {RelayUri}, next attempt in {Delay:F0}s",
                relayUri, nextAttemptIn.TotalSeconds);
            return;
        }

        // Check if we've exceeded max consecutive failures
        if (state.ConsecutiveFailures >= MaxConsecutiveFailures)
        {
            _logger.LogWarning("Relay {RelayUri} has exceeded maximum consecutive failures ({MaxFailures}), " +
                "removing from active connections", relayUri, MaxConsecutiveFailures);

            // Remove from clients and clean up
            if (_relayClients.TryRemove(relayUri, out var removedClient))
            {
                removedClient.Dispose();
            }
            _reconnectionStates.TryRemove(relayUri, out _);
            _relayPriorities.TryRemove(relayUri, out _);
            return;
        }

        _logger.LogInformation("Attempting to reconnect to relay {RelayUri} (attempt {Attempt})",
            relayUri, state.ConsecutiveFailures + 1);

        try
        {
            var connected = await client.ConnectAsync();
            if (connected)
            {
                _logger.LogInformation("Successfully reconnected to relay {RelayUri} after {Attempts} attempts",
                    relayUri, state.ConsecutiveFailures + 1);
                ResetReconnectionState(relayUri);
            }
            else
            {
                RecordReconnectionFailure(relayUri);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reconnection attempt to relay {RelayUri} failed", relayUri);
            RecordReconnectionFailure(relayUri);
        }
    }

    /// <summary>
    /// Check if we should attempt reconnection based on backoff timing
    /// </summary>
    private bool ShouldAttemptReconnection(RelayReconnectionState state)
    {
        return DateTime.UtcNow >= state.NextReconnectTime;
    }

    /// <summary>
    /// Record a reconnection failure and update backoff state
    /// </summary>
    private void RecordReconnectionFailure(string relayUri)
    {
        var state = _reconnectionStates.GetOrAdd(relayUri, _ => new RelayReconnectionState());

        state.ConsecutiveFailures++;
        state.LastFailureTime = DateTime.UtcNow;

        // Calculate next reconnect delay with exponential backoff
        var backoffSeconds = Math.Min(
            MinReconnectDelaySeconds * Math.Pow(BackoffMultiplier, state.ConsecutiveFailures - 1),
            MaxReconnectDelaySeconds);

        // Add jitter (up to 10% of the delay) to prevent thundering herd
        var jitter = Random.Shared.NextDouble() * 0.1 * backoffSeconds;
        state.NextReconnectTime = DateTime.UtcNow.AddSeconds(backoffSeconds + jitter);

        _logger.LogDebug("Relay {RelayUri} reconnection failure #{Failures}, next attempt in {Delay:F0}s",
            relayUri, state.ConsecutiveFailures, backoffSeconds + jitter);
    }

    /// <summary>
    /// Reset reconnection state after successful connection
    /// </summary>
    private void ResetReconnectionState(string relayUri)
    {
        if (_reconnectionStates.TryGetValue(relayUri, out var state))
        {
            if (state.ConsecutiveFailures > 0)
            {
                _logger.LogDebug("Resetting reconnection state for relay {RelayUri} after {Failures} failures",
                    relayUri, state.ConsecutiveFailures);
            }
            state.ConsecutiveFailures = 0;
            state.LastFailureTime = null;
            state.NextReconnectTime = DateTime.UtcNow;
        }
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
        _relayPriorities.Clear();

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
    /// <summary>
    /// The relay server URI
    /// </summary>
    public string Uri { get; set; } = string.Empty;

    /// <summary>
    /// Whether the relay is currently connected
    /// </summary>
    public bool IsConnected { get; set; }

    /// <summary>
    /// Connection priority (lower is better), null if not prioritized
    /// </summary>
    public int? Priority { get; set; }

    /// <summary>
    /// The connection type as determined by the prioritizer
    /// </summary>
    public ConnectionType? ConnectionType { get; set; }
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

/// <summary>
/// Tracks reconnection state for exponential backoff
/// </summary>
internal class RelayReconnectionState
{
    /// <summary>
    /// Number of consecutive connection failures
    /// </summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>
    /// Time of the last connection failure
    /// </summary>
    public DateTime? LastFailureTime { get; set; }

    /// <summary>
    /// Next time a reconnection should be attempted
    /// </summary>
    public DateTime NextReconnectTime { get; set; } = DateTime.UtcNow;
}