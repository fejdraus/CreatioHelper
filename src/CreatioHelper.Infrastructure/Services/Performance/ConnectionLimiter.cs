using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Performance;

/// <summary>
/// Implementation of connection limiting service.
/// Based on Syncthing's connection management patterns.
/// </summary>
public class ConnectionLimiter : IConnectionLimiter, IDisposable
{
    private readonly ILogger<ConnectionLimiter> _logger;
    private readonly ConcurrentDictionary<string, DeviceConnectionTracker> _deviceTrackers = new();
    private readonly ConcurrentDictionary<Guid, ConnectionSlot> _activeSlots = new();
    private readonly SemaphoreSlim _globalSemaphore;
    private readonly object _statsLock = new();

    private ConnectionLimiterConfiguration _config;
    private int _peakConnections;
    private long _totalAcquired;
    private long _totalRejected;
    private long _totalDurationTicks;
    private long _completedConnections;
    private int _waitingRequests;
    private bool _disposed;

    public ConnectionLimiter(
        ILogger<ConnectionLimiter> logger,
        ConnectionLimiterConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new ConnectionLimiterConfiguration();

        // Initialize global semaphore (0 = unlimited, use large number)
        var maxTotal = _config.MaxTotalConnections > 0 ? _config.MaxTotalConnections : int.MaxValue;
        _globalSemaphore = new SemaphoreSlim(maxTotal, maxTotal);

        _logger.LogInformation(
            "Connection limiter initialized: max total = {MaxTotal}, max per device = {MaxPerDevice}",
            _config.MaxTotalConnections > 0 ? _config.MaxTotalConnections.ToString() : "unlimited",
            _config.MaxConnectionsPerDevice > 0 ? _config.MaxConnectionsPerDevice.ToString() : "unlimited");
    }

    /// <inheritdoc />
    public int TotalConnectionCount => _activeSlots.Count;

    /// <inheritdoc />
    public IConnectionSlot? TryAcquire(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            throw new ArgumentNullException(nameof(deviceId));

        var tracker = GetOrCreateTracker(deviceId);
        var maxPerDevice = GetMaxConnectionsForDevice(deviceId);

        // Check device limit
        if (maxPerDevice > 0 && tracker.ActiveCount >= maxPerDevice)
        {
            Interlocked.Increment(ref _totalRejected);
            _logger.LogDebug("Connection rejected for device {DeviceId}: device limit reached ({Count}/{Max})",
                deviceId, tracker.ActiveCount, maxPerDevice);
            return null;
        }

        // Try to acquire global slot
        if (!_globalSemaphore.Wait(0))
        {
            Interlocked.Increment(ref _totalRejected);
            _logger.LogDebug("Connection rejected for device {DeviceId}: global limit reached",
                deviceId);
            return null;
        }

        // Create and register slot
        var slot = new ConnectionSlot(deviceId, this);

        if (!tracker.TryAdd(slot))
        {
            _globalSemaphore.Release();
            Interlocked.Increment(ref _totalRejected);
            return null;
        }

        _activeSlots[slot.Id] = slot;
        Interlocked.Increment(ref _totalAcquired);
        UpdatePeakConnections();

        _logger.LogDebug("Connection slot acquired for device {DeviceId} (total: {Total})",
            deviceId, TotalConnectionCount);

        return slot;
    }

    /// <inheritdoc />
    public async Task<IConnectionSlot?> AcquireAsync(string deviceId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(deviceId))
            throw new ArgumentNullException(nameof(deviceId));

        var tracker = GetOrCreateTracker(deviceId);
        var maxPerDevice = GetMaxConnectionsForDevice(deviceId);

        // Try immediate acquire first
        var slot = TryAcquire(deviceId);
        if (slot != null)
            return slot;

        // Wait for availability
        Interlocked.Increment(ref _waitingRequests);
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            while (!linkedCts.Token.IsCancellationRequested)
            {
                // Wait for global semaphore
                var acquired = await _globalSemaphore.WaitAsync(100, linkedCts.Token);
                if (!acquired)
                    continue;

                // Check device limit
                if (maxPerDevice > 0 && tracker.ActiveCount >= maxPerDevice)
                {
                    _globalSemaphore.Release();
                    await Task.Delay(50, linkedCts.Token);
                    continue;
                }

                // Create and register slot
                var newSlot = new ConnectionSlot(deviceId, this);

                if (!tracker.TryAdd(newSlot))
                {
                    _globalSemaphore.Release();
                    await Task.Delay(50, linkedCts.Token);
                    continue;
                }

                _activeSlots[newSlot.Id] = newSlot;
                Interlocked.Increment(ref _totalAcquired);
                UpdatePeakConnections();

                _logger.LogDebug("Connection slot acquired (async) for device {DeviceId} (total: {Total})",
                    deviceId, TotalConnectionCount);

                return newSlot;
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout or cancellation
        }
        finally
        {
            Interlocked.Decrement(ref _waitingRequests);
        }

        Interlocked.Increment(ref _totalRejected);
        _logger.LogDebug("Connection acquire timed out for device {DeviceId}", deviceId);
        return null;
    }

    /// <inheritdoc />
    public int GetDeviceConnectionCount(string deviceId)
    {
        if (_deviceTrackers.TryGetValue(deviceId, out var tracker))
        {
            return tracker.ActiveCount;
        }
        return 0;
    }

    /// <inheritdoc />
    public bool CanConnect(string deviceId)
    {
        var maxPerDevice = GetMaxConnectionsForDevice(deviceId);
        var currentCount = GetDeviceConnectionCount(deviceId);

        if (maxPerDevice > 0 && currentCount >= maxPerDevice)
            return false;

        if (_config.MaxTotalConnections > 0 && TotalConnectionCount >= _config.MaxTotalConnections)
            return false;

        return true;
    }

    /// <inheritdoc />
    public void UpdateConfiguration(ConnectionLimiterConfiguration configuration)
    {
        var oldConfig = _config;
        _config = configuration;

        _logger.LogInformation(
            "Connection limiter configuration updated: max total = {MaxTotal}, max per device = {MaxPerDevice}",
            configuration.MaxTotalConnections > 0 ? configuration.MaxTotalConnections.ToString() : "unlimited",
            configuration.MaxConnectionsPerDevice > 0 ? configuration.MaxConnectionsPerDevice.ToString() : "unlimited");
    }

    /// <inheritdoc />
    public ConnectionLimiterStatistics GetStatistics()
    {
        var connectionsByDevice = _deviceTrackers.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ActiveCount);

        TimeSpan avgDuration = TimeSpan.Zero;
        if (_completedConnections > 0)
        {
            avgDuration = TimeSpan.FromTicks(Interlocked.Read(ref _totalDurationTicks) / _completedConnections);
        }

        return new ConnectionLimiterStatistics
        {
            ActiveConnections = TotalConnectionCount,
            PeakConnections = _peakConnections,
            TotalConnectionsAcquired = Interlocked.Read(ref _totalAcquired),
            TotalConnectionsRejected = Interlocked.Read(ref _totalRejected),
            ConnectionsByDevice = connectionsByDevice,
            AverageConnectionDuration = avgDuration,
            WaitingRequests = _waitingRequests
        };
    }

    internal void ReleaseSlot(ConnectionSlot slot)
    {
        if (_activeSlots.TryRemove(slot.Id, out _))
        {
            if (_deviceTrackers.TryGetValue(slot.DeviceId, out var tracker))
            {
                tracker.Remove(slot);
            }

            _globalSemaphore.Release();

            // Track duration statistics
            if (_config.TrackStatistics)
            {
                var duration = DateTime.UtcNow - slot.AcquiredAt;
                Interlocked.Add(ref _totalDurationTicks, duration.Ticks);
                Interlocked.Increment(ref _completedConnections);
            }

            _logger.LogDebug("Connection slot released for device {DeviceId} (total: {Total})",
                slot.DeviceId, TotalConnectionCount);
        }
    }

    private DeviceConnectionTracker GetOrCreateTracker(string deviceId)
    {
        return _deviceTrackers.GetOrAdd(deviceId, _ => new DeviceConnectionTracker(deviceId));
    }

    private int GetMaxConnectionsForDevice(string deviceId)
    {
        if (_config.DeviceOverrides.TryGetValue(deviceId, out var maxConnections))
        {
            return maxConnections;
        }
        return _config.MaxConnectionsPerDevice;
    }

    private void UpdatePeakConnections()
    {
        var current = TotalConnectionCount;
        lock (_statsLock)
        {
            if (current > _peakConnections)
            {
                _peakConnections = current;
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _globalSemaphore.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Tracks connections for a specific device.
/// </summary>
internal class DeviceConnectionTracker
{
    private readonly string _deviceId;
    private readonly ConcurrentDictionary<Guid, ConnectionSlot> _slots = new();

    public DeviceConnectionTracker(string deviceId)
    {
        _deviceId = deviceId;
    }

    public string DeviceId => _deviceId;
    public int ActiveCount => _slots.Count;

    public bool TryAdd(ConnectionSlot slot)
    {
        return _slots.TryAdd(slot.Id, slot);
    }

    public void Remove(ConnectionSlot slot)
    {
        _slots.TryRemove(slot.Id, out _);
    }
}

/// <summary>
/// Represents an acquired connection slot.
/// </summary>
internal class ConnectionSlot : IConnectionSlot
{
    private readonly ConnectionLimiter _limiter;
    private bool _disposed;

    public ConnectionSlot(string deviceId, ConnectionLimiter limiter)
    {
        Id = Guid.NewGuid();
        DeviceId = deviceId;
        AcquiredAt = DateTime.UtcNow;
        _limiter = limiter;
    }

    public Guid Id { get; }
    public string DeviceId { get; }
    public DateTime AcquiredAt { get; }
    public bool IsValid => !_disposed;

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _limiter.ReleaseSlot(this);
        }
    }
}
