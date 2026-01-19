using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Network.Connection;

/// <summary>
/// Service for upgrading connections to better alternatives.
/// Monitors device connections and attempts to upgrade them when better
/// connections become available.
/// Based on Syncthing's connection upgrade mechanism from lib/connections/service.go
/// </summary>
public class ConnectionUpgradeService : IDisposable
{
    private readonly ILogger<ConnectionUpgradeService> _logger;
    private readonly IConnectionPrioritizer _prioritizer;
    private readonly ParallelDialer _dialer;
    private readonly ConnectionUpgradeConfiguration _config;

    private readonly ConcurrentDictionary<string, DeviceConnectionState> _deviceStates = new();
    private Timer? _upgradeCheckTimer;
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isRunning;
    private bool _disposed;

    /// <summary>
    /// Is the service running
    /// </summary>
    public bool IsRunning => _isRunning;

    /// <summary>
    /// Event raised when a connection is upgraded
    /// </summary>
    public event EventHandler<ConnectionUpgradedEventArgs>? ConnectionUpgraded;

    /// <summary>
    /// Event raised when an upgrade attempt fails
    /// </summary>
    public event EventHandler<ConnectionUpgradeFailedEventArgs>? UpgradeFailed;

    public ConnectionUpgradeService(
        ILogger<ConnectionUpgradeService> logger,
        IConnectionPrioritizer prioritizer,
        ParallelDialer dialer,
        ConnectionUpgradeConfiguration? config = null)
    {
        _logger = logger;
        _prioritizer = prioritizer;
        _dialer = dialer;
        _config = config ?? new ConnectionUpgradeConfiguration();
    }

    /// <summary>
    /// Start the upgrade service
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_isRunning)
            return Task.CompletedTask;

        _logger.LogInformation("Starting connection upgrade service with check interval {Interval}s",
            _config.CheckIntervalSeconds);

        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _isRunning = true;

        // Start periodic upgrade check
        _upgradeCheckTimer = new Timer(
            async _ => await CheckForUpgradesAsync(_cancellationTokenSource.Token),
            null,
            TimeSpan.FromSeconds(_config.InitialDelaySeconds),
            TimeSpan.FromSeconds(_config.CheckIntervalSeconds));

        _logger.LogInformation("Connection upgrade service started");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop the upgrade service
    /// </summary>
    public Task StopAsync()
    {
        if (!_isRunning)
            return Task.CompletedTask;

        _logger.LogInformation("Stopping connection upgrade service");

        _cancellationTokenSource?.Cancel();
        _upgradeCheckTimer?.Dispose();
        _upgradeCheckTimer = null;
        _isRunning = false;

        _logger.LogInformation("Connection upgrade service stopped");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Register a device connection for potential upgrade
    /// </summary>
    public void RegisterConnection(string deviceId, DialResult connection)
    {
        var state = _deviceStates.GetOrAdd(deviceId, _ => new DeviceConnectionState { DeviceId = deviceId });

        lock (state)
        {
            state.CurrentConnections.Add(new TrackedConnection
            {
                Connection = connection,
                Priority = connection.Priority,
                EstablishedAt = connection.ConnectedAt
            });

            state.WorstPriority = state.CurrentConnections.Max(c => c.Priority);
        }

        _logger.LogDebug("Registered connection for device {DeviceId} with priority {Priority}. Total connections: {Count}",
            deviceId, connection.Priority, state.CurrentConnections.Count);
    }

    /// <summary>
    /// Unregister a device connection
    /// </summary>
    public void UnregisterConnection(string deviceId, DialResult connection)
    {
        if (!_deviceStates.TryGetValue(deviceId, out var state))
            return;

        lock (state)
        {
            state.CurrentConnections.RemoveAll(c => ReferenceEquals(c.Connection, connection));

            if (state.CurrentConnections.Count > 0)
            {
                state.WorstPriority = state.CurrentConnections.Max(c => c.Priority);
            }
            else
            {
                _deviceStates.TryRemove(deviceId, out _);
            }
        }

        _logger.LogDebug("Unregistered connection for device {DeviceId}. Remaining connections: {Count}",
            deviceId, state?.CurrentConnections.Count ?? 0);
    }

    /// <summary>
    /// Add potential better addresses for a device
    /// </summary>
    public void AddPotentialAddresses(string deviceId, IEnumerable<string> addresses)
    {
        var state = _deviceStates.GetOrAdd(deviceId, _ => new DeviceConnectionState { DeviceId = deviceId });

        lock (state)
        {
            foreach (var addr in addresses)
            {
                var priority = _prioritizer.CalculatePriority(addr);

                // Only add if potentially better than current worst
                if (priority < state.WorstPriority - _config.UpgradeThreshold)
                {
                    state.PotentialAddresses.Add(new PotentialAddress
                    {
                        Address = addr,
                        Priority = priority,
                        AddedAt = DateTime.UtcNow
                    });
                }
            }

            // Keep only the best N potential addresses
            state.PotentialAddresses = state.PotentialAddresses
                .OrderBy(a => a.Priority)
                .Take(_config.MaxPotentialAddresses)
                .ToList();
        }

        _logger.LogDebug("Added potential addresses for device {DeviceId}. Potential: {Count}",
            deviceId, state.PotentialAddresses.Count);
    }

    /// <summary>
    /// Check if upgrade is needed for a device
    /// </summary>
    public bool ShouldTryUpgrade(string deviceId)
    {
        if (!_deviceStates.TryGetValue(deviceId, out var state))
            return false;

        lock (state)
        {
            if (!state.CurrentConnections.Any() || !state.PotentialAddresses.Any())
                return false;

            var bestPotential = state.PotentialAddresses.Min(a => a.Priority);
            return _prioritizer.ShouldUpgrade(state.WorstPriority, bestPotential, _config.UpgradeThreshold);
        }
    }

    /// <summary>
    /// Get upgrade service status
    /// </summary>
    public ConnectionUpgradeStatus GetStatus()
    {
        return new ConnectionUpgradeStatus
        {
            IsRunning = _isRunning,
            TrackedDeviceCount = _deviceStates.Count,
            TotalConnections = _deviceStates.Values.Sum(s => s.CurrentConnections.Count),
            TotalPotentialAddresses = _deviceStates.Values.Sum(s => s.PotentialAddresses.Count),
            DevicesNeedingUpgrade = _deviceStates.Keys.Count(id => ShouldTryUpgrade(id)),
            CheckIntervalSeconds = _config.CheckIntervalSeconds,
            UpgradeThreshold = _config.UpgradeThreshold,
            Devices = _deviceStates.Values.Select(s => new DeviceUpgradeInfo
            {
                DeviceId = s.DeviceId,
                CurrentConnectionCount = s.CurrentConnections.Count,
                WorstPriority = s.WorstPriority,
                BestPotentialPriority = s.PotentialAddresses.Any() ? s.PotentialAddresses.Min(a => a.Priority) : int.MaxValue,
                PotentialAddressCount = s.PotentialAddresses.Count,
                NeedsUpgrade = ShouldTryUpgrade(s.DeviceId)
            }).ToList()
        };
    }

    private async Task CheckForUpgradesAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return;

        _logger.LogDebug("Checking {Count} devices for potential connection upgrades", _deviceStates.Count);

        foreach (var kvp in _deviceStates)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var deviceId = kvp.Key;
            var state = kvp.Value;

            if (!ShouldTryUpgrade(deviceId))
                continue;

            await TryUpgradeDeviceAsync(deviceId, state, cancellationToken);
        }
    }

    private async Task TryUpgradeDeviceAsync(
        string deviceId,
        DeviceConnectionState state,
        CancellationToken cancellationToken)
    {
        List<string> addressesToTry;

        lock (state)
        {
            // Get addresses that are significantly better
            addressesToTry = state.PotentialAddresses
                .Where(a => _prioritizer.ShouldUpgrade(state.WorstPriority, a.Priority, _config.UpgradeThreshold))
                .OrderBy(a => a.Priority)
                .Take(_config.MaxUpgradeAttempts)
                .Select(a => a.Address)
                .ToList();

            // Clean up old potential addresses
            var cutoff = DateTime.UtcNow.AddMinutes(-_config.PotentialAddressExpirationMinutes);
            state.PotentialAddresses.RemoveAll(a => a.AddedAt < cutoff);
        }

        if (!addressesToTry.Any())
            return;

        _logger.LogDebug("Attempting to upgrade connection for device {DeviceId} with {Count} potential addresses",
            deviceId, addressesToTry.Count);

        try
        {
            var result = await _dialer.DialAsync(deviceId, addressesToTry, cancellationToken: cancellationToken);

            if (result != null)
            {
                // Successfully connected with better priority
                lock (state)
                {
                    // Remove the address from potentials
                    state.PotentialAddresses.RemoveAll(a => a.Address == result.Address);

                    // Find the worst current connection to potentially close
                    var worstConnection = state.CurrentConnections
                        .OrderByDescending(c => c.Priority)
                        .FirstOrDefault();

                    if (worstConnection != null && result.Priority < worstConnection.Priority - _config.UpgradeThreshold)
                    {
                        _logger.LogInformation(
                            "Upgraded connection for device {DeviceId}: priority {OldPriority}→{NewPriority}",
                            deviceId, worstConnection.Priority, result.Priority);

                        ConnectionUpgraded?.Invoke(this, new ConnectionUpgradedEventArgs(
                            deviceId,
                            worstConnection.Connection,
                            result,
                            worstConnection.Priority,
                            result.Priority));

                        // Close the old connection
                        var oldConnection = worstConnection.Connection;
                        state.CurrentConnections.Remove(worstConnection);

                        // Add the new connection
                        state.CurrentConnections.Add(new TrackedConnection
                        {
                            Connection = result,
                            Priority = result.Priority,
                            EstablishedAt = result.ConnectedAt
                        });

                        state.WorstPriority = state.CurrentConnections.Max(c => c.Priority);

                        // Dispose old connection
                        oldConnection.Dispose();
                    }
                    else
                    {
                        // New connection isn't actually better, close it
                        result.Dispose();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to upgrade connection for device {DeviceId}", deviceId);

            UpgradeFailed?.Invoke(this, new ConnectionUpgradeFailedEventArgs(
                deviceId,
                addressesToTry,
                ex.Message));
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            StopAsync().GetAwaiter().GetResult();
            _cancellationTokenSource?.Dispose();

            foreach (var state in _deviceStates.Values)
            {
                foreach (var conn in state.CurrentConnections)
                {
                    conn.Connection.Dispose();
                }
            }
            _deviceStates.Clear();

            _disposed = true;
        }
    }
}

/// <summary>
/// Internal state for a device's connections
/// </summary>
internal class DeviceConnectionState
{
    public string DeviceId { get; set; } = string.Empty;
    public List<TrackedConnection> CurrentConnections { get; set; } = new();
    public List<PotentialAddress> PotentialAddresses { get; set; } = new();
    public int WorstPriority { get; set; } = int.MaxValue;
}

/// <summary>
/// A tracked connection
/// </summary>
internal class TrackedConnection
{
    public DialResult Connection { get; set; } = null!;
    public int Priority { get; set; }
    public DateTime EstablishedAt { get; set; }
}

/// <summary>
/// A potential better address
/// </summary>
internal class PotentialAddress
{
    public string Address { get; set; } = string.Empty;
    public int Priority { get; set; }
    public DateTime AddedAt { get; set; }
}

/// <summary>
/// Configuration for connection upgrade service
/// </summary>
public class ConnectionUpgradeConfiguration
{
    public int CheckIntervalSeconds { get; set; } = 60;
    public int InitialDelaySeconds { get; set; } = 30;
    public int UpgradeThreshold { get; set; } = 5;
    public int MaxPotentialAddresses { get; set; } = 10;
    public int MaxUpgradeAttempts { get; set; } = 3;
    public int PotentialAddressExpirationMinutes { get; set; } = 30;
}

/// <summary>
/// Connection upgrade service status
/// </summary>
public class ConnectionUpgradeStatus
{
    public bool IsRunning { get; set; }
    public int TrackedDeviceCount { get; set; }
    public int TotalConnections { get; set; }
    public int TotalPotentialAddresses { get; set; }
    public int DevicesNeedingUpgrade { get; set; }
    public int CheckIntervalSeconds { get; set; }
    public int UpgradeThreshold { get; set; }
    public List<DeviceUpgradeInfo> Devices { get; set; } = new();
}

/// <summary>
/// Upgrade info for a device
/// </summary>
public class DeviceUpgradeInfo
{
    public string DeviceId { get; set; } = string.Empty;
    public int CurrentConnectionCount { get; set; }
    public int WorstPriority { get; set; }
    public int BestPotentialPriority { get; set; }
    public int PotentialAddressCount { get; set; }
    public bool NeedsUpgrade { get; set; }
}

/// <summary>
/// Event args for successful connection upgrade
/// </summary>
public class ConnectionUpgradedEventArgs : EventArgs
{
    public string DeviceId { get; }
    public DialResult OldConnection { get; }
    public DialResult NewConnection { get; }
    public int OldPriority { get; }
    public int NewPriority { get; }

    public ConnectionUpgradedEventArgs(
        string deviceId,
        DialResult oldConnection,
        DialResult newConnection,
        int oldPriority,
        int newPriority)
    {
        DeviceId = deviceId;
        OldConnection = oldConnection;
        NewConnection = newConnection;
        OldPriority = oldPriority;
        NewPriority = newPriority;
    }
}

/// <summary>
/// Event args for failed connection upgrade
/// </summary>
public class ConnectionUpgradeFailedEventArgs : EventArgs
{
    public string DeviceId { get; }
    public List<string> AttemptedAddresses { get; }
    public string Error { get; }

    public ConnectionUpgradeFailedEventArgs(string deviceId, List<string> attemptedAddresses, string error)
    {
        DeviceId = deviceId;
        AttemptedAddresses = attemptedAddresses;
        Error = error;
    }
}
