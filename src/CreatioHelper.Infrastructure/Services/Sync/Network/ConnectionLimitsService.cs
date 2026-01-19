using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Network;

/// <summary>
/// Service for managing connection limits per device.
/// Based on Syncthing's connection limiting functionality.
/// </summary>
public interface IConnectionLimitsService
{
    /// <summary>
    /// Check if a new connection to a device is allowed.
    /// </summary>
    bool CanConnect(string deviceId);

    /// <summary>
    /// Try to acquire a connection slot for a device.
    /// </summary>
    bool TryAcquireConnection(string deviceId, out IDisposable? connectionHandle);

    /// <summary>
    /// Acquire a connection slot, waiting if necessary.
    /// </summary>
    Task<IDisposable> AcquireConnectionAsync(string deviceId, CancellationToken ct = default);

    /// <summary>
    /// Get the number of active connections to a device.
    /// </summary>
    int GetActiveConnections(string deviceId);

    /// <summary>
    /// Get the maximum allowed connections to a device.
    /// </summary>
    int GetMaxConnections(string deviceId);

    /// <summary>
    /// Set the maximum connections for a device.
    /// </summary>
    void SetMaxConnections(string deviceId, int maxConnections);

    /// <summary>
    /// Get the total number of active connections.
    /// </summary>
    int GetTotalActiveConnections();

    /// <summary>
    /// Get the global maximum connections.
    /// </summary>
    int GetGlobalMaxConnections();

    /// <summary>
    /// Set the global maximum connections.
    /// </summary>
    void SetGlobalMaxConnections(int maxConnections);

    /// <summary>
    /// Get connection statistics.
    /// </summary>
    ConnectionLimitStats GetStats();

    /// <summary>
    /// Get connection statistics for a specific device.
    /// </summary>
    DeviceConnectionStats GetDeviceStats(string deviceId);
}

/// <summary>
/// Global connection limit statistics.
/// </summary>
public class ConnectionLimitStats
{
    public int TotalActiveConnections { get; init; }
    public int GlobalMaxConnections { get; init; }
    public int DeviceCount { get; init; }
    public long TotalConnectionsEstablished { get; init; }
    public long TotalConnectionsRejected { get; init; }
    public long TotalConnectionsTimedOut { get; init; }
}

/// <summary>
/// Per-device connection statistics.
/// </summary>
public class DeviceConnectionStats
{
    public string DeviceId { get; init; } = string.Empty;
    public int ActiveConnections { get; init; }
    public int MaxConnections { get; init; }
    public long ConnectionsEstablished { get; set; }
    public long ConnectionsRejected { get; set; }
    public DateTime? LastConnectionTime { get; set; }
}

/// <summary>
/// Configuration for connection limits.
/// </summary>
public class ConnectionLimitsConfiguration
{
    /// <summary>
    /// Global maximum connections (0 = unlimited).
    /// Default matches Syncthing's default of 0 (unlimited).
    /// </summary>
    public int GlobalMaxConnections { get; set; } = 0;

    /// <summary>
    /// Default maximum connections per device (0 = unlimited).
    /// </summary>
    public int DefaultMaxConnectionsPerDevice { get; set; } = 1;

    /// <summary>
    /// Per-device connection limit overrides.
    /// </summary>
    public Dictionary<string, int> DeviceMaxConnections { get; } = new();

    /// <summary>
    /// Timeout for acquiring a connection slot.
    /// </summary>
    public TimeSpan AcquireTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Get effective max connections for a device.
    /// </summary>
    public int GetEffectiveMaxConnections(string deviceId)
    {
        if (DeviceMaxConnections.TryGetValue(deviceId, out var max))
        {
            return max;
        }
        return DefaultMaxConnectionsPerDevice;
    }
}

/// <summary>
/// Implementation of connection limits service.
/// </summary>
public class ConnectionLimitsService : IConnectionLimitsService
{
    private readonly ILogger<ConnectionLimitsService> _logger;
    private readonly ConnectionLimitsConfiguration _config;
    private readonly ConcurrentDictionary<string, DeviceConnectionState> _deviceStates = new();
    private int _totalActiveConnections;
    private long _totalConnectionsEstablished;
    private long _totalConnectionsRejected;
    private long _totalConnectionsTimedOut;

    public ConnectionLimitsService(
        ILogger<ConnectionLimitsService> logger,
        ConnectionLimitsConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new ConnectionLimitsConfiguration();
    }

    /// <inheritdoc />
    public bool CanConnect(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        // Check global limit
        if (_config.GlobalMaxConnections > 0 && _totalActiveConnections >= _config.GlobalMaxConnections)
        {
            return false;
        }

        // Check device limit
        var maxPerDevice = GetMaxConnections(deviceId);
        if (maxPerDevice > 0)
        {
            var state = _deviceStates.GetOrAdd(deviceId, _ => new DeviceConnectionState());
            if (state.ActiveConnections >= maxPerDevice)
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public bool TryAcquireConnection(string deviceId, out IDisposable? connectionHandle)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        connectionHandle = null;

        var state = _deviceStates.GetOrAdd(deviceId, _ => new DeviceConnectionState());
        var maxPerDevice = GetMaxConnections(deviceId);

        // Check global limit
        if (_config.GlobalMaxConnections > 0)
        {
            var current = Interlocked.Increment(ref _totalActiveConnections);
            if (current > _config.GlobalMaxConnections)
            {
                Interlocked.Decrement(ref _totalActiveConnections);
                Interlocked.Increment(ref _totalConnectionsRejected);
                state.Stats.ConnectionsRejected++;
                return false;
            }
        }
        else
        {
            Interlocked.Increment(ref _totalActiveConnections);
        }

        // Check device limit
        if (maxPerDevice > 0)
        {
            var deviceCurrent = Interlocked.Increment(ref state.ActiveConnections);
            if (deviceCurrent > maxPerDevice)
            {
                Interlocked.Decrement(ref state.ActiveConnections);
                Interlocked.Decrement(ref _totalActiveConnections);
                Interlocked.Increment(ref _totalConnectionsRejected);
                state.Stats.ConnectionsRejected++;
                return false;
            }
        }
        else
        {
            Interlocked.Increment(ref state.ActiveConnections);
        }

        Interlocked.Increment(ref _totalConnectionsEstablished);
        state.Stats.ConnectionsEstablished++;
        state.Stats.LastConnectionTime = DateTime.UtcNow;

        connectionHandle = new ConnectionHandle(this, deviceId, state);
        _logger.LogDebug("Acquired connection for device {DeviceId}. Active: {Active}/{Max}",
            deviceId, state.ActiveConnections, maxPerDevice);

        return true;
    }

    /// <inheritdoc />
    public async Task<IDisposable> AcquireConnectionAsync(string deviceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        using var timeoutCts = new CancellationTokenSource(_config.AcquireTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        while (!linkedCts.Token.IsCancellationRequested)
        {
            if (TryAcquireConnection(deviceId, out var handle))
            {
                return handle!;
            }

            try
            {
                await Task.Delay(100, linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                Interlocked.Increment(ref _totalConnectionsTimedOut);
                throw new TimeoutException($"Timeout waiting for connection slot to device {deviceId}");
            }
        }

        ct.ThrowIfCancellationRequested();
        throw new OperationCanceledException(ct);
    }

    /// <inheritdoc />
    public int GetActiveConnections(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        if (_deviceStates.TryGetValue(deviceId, out var state))
        {
            return state.ActiveConnections;
        }
        return 0;
    }

    /// <inheritdoc />
    public int GetMaxConnections(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        return _config.GetEffectiveMaxConnections(deviceId);
    }

    /// <inheritdoc />
    public void SetMaxConnections(string deviceId, int maxConnections)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        if (maxConnections < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConnections), "Max connections cannot be negative");
        }

        _config.DeviceMaxConnections[deviceId] = maxConnections;
        _logger.LogInformation("Set max connections for device {DeviceId} to {Max}", deviceId, maxConnections);
    }

    /// <inheritdoc />
    public int GetTotalActiveConnections()
    {
        return _totalActiveConnections;
    }

    /// <inheritdoc />
    public int GetGlobalMaxConnections()
    {
        return _config.GlobalMaxConnections;
    }

    /// <inheritdoc />
    public void SetGlobalMaxConnections(int maxConnections)
    {
        if (maxConnections < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConnections), "Max connections cannot be negative");
        }

        _config.GlobalMaxConnections = maxConnections;
        _logger.LogInformation("Set global max connections to {Max}", maxConnections);
    }

    /// <inheritdoc />
    public ConnectionLimitStats GetStats()
    {
        return new ConnectionLimitStats
        {
            TotalActiveConnections = _totalActiveConnections,
            GlobalMaxConnections = _config.GlobalMaxConnections,
            DeviceCount = _deviceStates.Count,
            TotalConnectionsEstablished = Interlocked.Read(ref _totalConnectionsEstablished),
            TotalConnectionsRejected = Interlocked.Read(ref _totalConnectionsRejected),
            TotalConnectionsTimedOut = Interlocked.Read(ref _totalConnectionsTimedOut)
        };
    }

    /// <inheritdoc />
    public DeviceConnectionStats GetDeviceStats(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        var state = _deviceStates.GetOrAdd(deviceId, _ => new DeviceConnectionState());
        return new DeviceConnectionStats
        {
            DeviceId = deviceId,
            ActiveConnections = state.ActiveConnections,
            MaxConnections = GetMaxConnections(deviceId),
            ConnectionsEstablished = state.Stats.ConnectionsEstablished,
            ConnectionsRejected = state.Stats.ConnectionsRejected,
            LastConnectionTime = state.Stats.LastConnectionTime
        };
    }

    private void ReleaseConnection(string deviceId, DeviceConnectionState state)
    {
        Interlocked.Decrement(ref state.ActiveConnections);
        Interlocked.Decrement(ref _totalActiveConnections);

        _logger.LogDebug("Released connection for device {DeviceId}. Active: {Active}",
            deviceId, state.ActiveConnections);
    }

    private class DeviceConnectionState
    {
        public int ActiveConnections;
        public DeviceConnectionStats Stats { get; } = new();
    }

    private class ConnectionHandle : IDisposable
    {
        private readonly ConnectionLimitsService _service;
        private readonly string _deviceId;
        private readonly DeviceConnectionState _state;
        private bool _disposed;

        public ConnectionHandle(ConnectionLimitsService service, string deviceId, DeviceConnectionState state)
        {
            _service = service;
            _deviceId = deviceId;
            _state = state;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _service.ReleaseConnection(_deviceId, _state);
            }
        }
    }
}
