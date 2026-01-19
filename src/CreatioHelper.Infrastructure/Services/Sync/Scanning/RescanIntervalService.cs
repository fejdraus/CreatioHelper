using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Scanning;

/// <summary>
/// Service for managing folder rescan intervals.
/// Supports sub-second intervals for high-frequency syncing.
/// Based on Syncthing's RescanIntervalS and FSWatcherDelayS configurations.
/// </summary>
public interface IRescanIntervalService
{
    /// <summary>
    /// Get the effective rescan interval for a folder.
    /// </summary>
    TimeSpan GetRescanInterval(string folderId);

    /// <summary>
    /// Set the rescan interval for a folder.
    /// </summary>
    void SetRescanInterval(string folderId, TimeSpan interval);

    /// <summary>
    /// Get the filesystem watcher delay for a folder.
    /// </summary>
    TimeSpan GetFsWatcherDelay(string folderId);

    /// <summary>
    /// Set the filesystem watcher delay for a folder.
    /// </summary>
    void SetFsWatcherDelay(string folderId, TimeSpan delay);

    /// <summary>
    /// Schedule next rescan for a folder.
    /// </summary>
    Task<DateTime> ScheduleNextRescanAsync(string folderId, CancellationToken ct = default);

    /// <summary>
    /// Cancel scheduled rescan for a folder.
    /// </summary>
    void CancelScheduledRescan(string folderId);

    /// <summary>
    /// Get time until next scheduled rescan.
    /// </summary>
    TimeSpan? GetTimeUntilNextRescan(string folderId);

    /// <summary>
    /// Trigger immediate rescan for a folder.
    /// </summary>
    Task TriggerImmediateRescanAsync(string folderId, CancellationToken ct = default);

    /// <summary>
    /// Subscribe to rescan events.
    /// </summary>
    IDisposable OnRescanDue(Action<string> callback);

    /// <summary>
    /// Get rescan statistics.
    /// </summary>
    RescanStats GetStats(string folderId);
}

/// <summary>
/// Statistics about rescans for a folder.
/// </summary>
public class RescanStats
{
    public string FolderId { get; init; } = string.Empty;
    public long TotalRescans { get; set; }
    public DateTime? LastRescanTime { get; set; }
    public DateTime? NextScheduledRescan { get; set; }
    public TimeSpan AverageRescanDuration { get; set; }
    public TimeSpan ConfiguredInterval { get; set; }
    public TimeSpan ConfiguredFsWatcherDelay { get; set; }
    public bool FsWatcherEnabled { get; set; }
}

/// <summary>
/// Rescan schedule information.
/// </summary>
public class RescanSchedule
{
    public string FolderId { get; init; } = string.Empty;
    public DateTime ScheduledTime { get; init; }
    public CancellationTokenSource? CancellationSource { get; set; }
    public bool IsCancelled => CancellationSource?.IsCancellationRequested ?? true;
}

/// <summary>
/// Implementation of rescan interval service.
/// </summary>
public class RescanIntervalService : IRescanIntervalService, IDisposable
{
    private readonly ILogger<RescanIntervalService> _logger;
    private readonly ConcurrentDictionary<string, TimeSpan> _rescanIntervals = new();
    private readonly ConcurrentDictionary<string, TimeSpan> _fsWatcherDelays = new();
    private readonly ConcurrentDictionary<string, RescanSchedule> _scheduledRescans = new();
    private readonly ConcurrentDictionary<string, RescanStats> _stats = new();
    private readonly ConcurrentBag<Action<string>> _rescanCallbacks = new();
    private readonly RescanIntervalConfiguration _config;
    private bool _disposed;

    // Syncthing defaults
    private static readonly TimeSpan DefaultRescanInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultFsWatcherDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MinRescanInterval = TimeSpan.FromMilliseconds(100); // Sub-second support
    private static readonly TimeSpan MaxRescanInterval = TimeSpan.FromDays(365);
    private static readonly TimeSpan MinFsWatcherDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan MaxFsWatcherDelay = TimeSpan.FromMinutes(10);

    public RescanIntervalService(
        ILogger<RescanIntervalService> logger,
        RescanIntervalConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new RescanIntervalConfiguration();
    }

    /// <inheritdoc />
    public TimeSpan GetRescanInterval(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (_rescanIntervals.TryGetValue(folderId, out var interval))
        {
            return interval;
        }

        return _config.DefaultRescanInterval;
    }

    /// <inheritdoc />
    public void SetRescanInterval(string folderId, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        // Validate interval
        if (interval < MinRescanInterval)
        {
            _logger.LogWarning(
                "Rescan interval {Interval} is below minimum {Min}, using minimum",
                interval, MinRescanInterval);
            interval = MinRescanInterval;
        }
        else if (interval > MaxRescanInterval)
        {
            _logger.LogWarning(
                "Rescan interval {Interval} is above maximum {Max}, using maximum",
                interval, MaxRescanInterval);
            interval = MaxRescanInterval;
        }

        _rescanIntervals[folderId] = interval;

        // Update stats
        var stats = _stats.GetOrAdd(folderId, id => new RescanStats { FolderId = id });
        stats.ConfiguredInterval = interval;

        _logger.LogInformation(
            "Set rescan interval for folder {FolderId} to {Interval}",
            folderId, interval);
    }

    /// <inheritdoc />
    public TimeSpan GetFsWatcherDelay(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (_fsWatcherDelays.TryGetValue(folderId, out var delay))
        {
            return delay;
        }

        return _config.DefaultFsWatcherDelay;
    }

    /// <inheritdoc />
    public void SetFsWatcherDelay(string folderId, TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        // Validate delay
        if (delay < MinFsWatcherDelay)
        {
            delay = MinFsWatcherDelay;
        }
        else if (delay > MaxFsWatcherDelay)
        {
            delay = MaxFsWatcherDelay;
        }

        _fsWatcherDelays[folderId] = delay;

        var stats = _stats.GetOrAdd(folderId, id => new RescanStats { FolderId = id });
        stats.ConfiguredFsWatcherDelay = delay;

        _logger.LogDebug(
            "Set filesystem watcher delay for folder {FolderId} to {Delay}",
            folderId, delay);
    }

    /// <inheritdoc />
    public async Task<DateTime> ScheduleNextRescanAsync(string folderId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        var interval = GetRescanInterval(folderId);
        var nextTime = DateTime.UtcNow.Add(interval);

        // Cancel any existing schedule
        CancelScheduledRescan(folderId);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var schedule = new RescanSchedule
        {
            FolderId = folderId,
            ScheduledTime = nextTime,
            CancellationSource = cts
        };

        _scheduledRescans[folderId] = schedule;

        // Update stats
        var stats = _stats.GetOrAdd(folderId, id => new RescanStats { FolderId = id });
        stats.NextScheduledRescan = nextTime;

        // Start timer
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(interval, cts.Token);

                if (!cts.IsCancellationRequested)
                {
                    _logger.LogDebug("Rescan due for folder {FolderId}", folderId);
                    NotifyRescanDue(folderId);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelled
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in scheduled rescan for folder {FolderId}", folderId);
            }
        }, ct);

        _logger.LogDebug(
            "Scheduled next rescan for folder {FolderId} at {Time}",
            folderId, nextTime);

        return nextTime;
    }

    /// <inheritdoc />
    public void CancelScheduledRescan(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (_scheduledRescans.TryRemove(folderId, out var schedule))
        {
            schedule.CancellationSource?.Cancel();
            schedule.CancellationSource?.Dispose();

            _logger.LogDebug("Cancelled scheduled rescan for folder {FolderId}", folderId);
        }
    }

    /// <inheritdoc />
    public TimeSpan? GetTimeUntilNextRescan(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (_scheduledRescans.TryGetValue(folderId, out var schedule))
        {
            if (!schedule.IsCancelled)
            {
                var remaining = schedule.ScheduledTime - DateTime.UtcNow;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task TriggerImmediateRescanAsync(string folderId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        // Cancel any pending scheduled rescan
        CancelScheduledRescan(folderId);

        _logger.LogInformation("Triggering immediate rescan for folder {FolderId}", folderId);

        // Update stats
        var stats = _stats.GetOrAdd(folderId, id => new RescanStats { FolderId = id });
        stats.TotalRescans++;
        stats.LastRescanTime = DateTime.UtcNow;

        // Notify subscribers
        NotifyRescanDue(folderId);

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public IDisposable OnRescanDue(Action<string> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _rescanCallbacks.Add(callback);
        return new CallbackDisposer(() => { });
    }

    /// <inheritdoc />
    public RescanStats GetStats(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (_stats.TryGetValue(folderId, out var stats))
        {
            // Update time until next rescan
            if (_scheduledRescans.TryGetValue(folderId, out var schedule))
            {
                stats.NextScheduledRescan = schedule.ScheduledTime;
            }
            return stats;
        }

        return new RescanStats
        {
            FolderId = folderId,
            ConfiguredInterval = GetRescanInterval(folderId),
            ConfiguredFsWatcherDelay = GetFsWatcherDelay(folderId)
        };
    }

    private void NotifyRescanDue(string folderId)
    {
        foreach (var callback in _rescanCallbacks)
        {
            try
            {
                callback(folderId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in rescan callback for folder {FolderId}", folderId);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var schedule in _scheduledRescans.Values)
        {
            schedule.CancellationSource?.Cancel();
            schedule.CancellationSource?.Dispose();
        }

        _scheduledRescans.Clear();
    }

    private class CallbackDisposer : IDisposable
    {
        private readonly Action _onDispose;
        public CallbackDisposer(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }
}

/// <summary>
/// Configuration for rescan intervals.
/// </summary>
public class RescanIntervalConfiguration
{
    /// <summary>
    /// Default rescan interval for folders without explicit configuration.
    /// </summary>
    public TimeSpan DefaultRescanInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Default filesystem watcher delay.
    /// </summary>
    public TimeSpan DefaultFsWatcherDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Enable sub-second rescan intervals.
    /// </summary>
    public bool EnableSubSecondIntervals { get; set; } = true;

    /// <summary>
    /// Minimum rescan interval (to prevent excessive CPU usage).
    /// </summary>
    public TimeSpan MinimumRescanInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Enable adaptive interval adjustment based on activity.
    /// </summary>
    public bool EnableAdaptiveIntervals { get; set; } = false;

    /// <summary>
    /// Factor to increase interval when no changes detected.
    /// </summary>
    public double IdleIntervalMultiplier { get; set; } = 1.5;

    /// <summary>
    /// Factor to decrease interval when changes are frequent.
    /// </summary>
    public double ActiveIntervalDivisor { get; set; } = 2.0;

    /// <summary>
    /// Maximum adaptive interval.
    /// </summary>
    public TimeSpan MaxAdaptiveInterval { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Adaptive rescan interval adjuster.
/// </summary>
public class AdaptiveRescanIntervalAdjuster
{
    private readonly IRescanIntervalService _rescanService;
    private readonly RescanIntervalConfiguration _config;
    private readonly ConcurrentDictionary<string, AdaptiveState> _states = new();
    private readonly ILogger<AdaptiveRescanIntervalAdjuster> _logger;

    public AdaptiveRescanIntervalAdjuster(
        IRescanIntervalService rescanService,
        RescanIntervalConfiguration config,
        ILogger<AdaptiveRescanIntervalAdjuster> logger)
    {
        _rescanService = rescanService;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Record that a rescan found changes.
    /// </summary>
    public void RecordChangesFound(string folderId, int changeCount)
    {
        if (!_config.EnableAdaptiveIntervals)
            return;

        var state = _states.GetOrAdd(folderId, _ => new AdaptiveState());
        state.LastChangeCount = changeCount;
        state.ConsecutiveIdleScans = 0;

        // Decrease interval (more frequent scans)
        var currentInterval = _rescanService.GetRescanInterval(folderId);
        var newInterval = TimeSpan.FromTicks((long)(currentInterval.Ticks / _config.ActiveIntervalDivisor));

        if (newInterval >= _config.MinimumRescanInterval)
        {
            _rescanService.SetRescanInterval(folderId, newInterval);
            _logger.LogDebug(
                "Decreased rescan interval for folder {FolderId} to {Interval} due to activity",
                folderId, newInterval);
        }
    }

    /// <summary>
    /// Record that a rescan found no changes.
    /// </summary>
    public void RecordNoChanges(string folderId)
    {
        if (!_config.EnableAdaptiveIntervals)
            return;

        var state = _states.GetOrAdd(folderId, _ => new AdaptiveState());
        state.ConsecutiveIdleScans++;

        // Increase interval after multiple idle scans
        if (state.ConsecutiveIdleScans >= 3)
        {
            var currentInterval = _rescanService.GetRescanInterval(folderId);
            var newInterval = TimeSpan.FromTicks((long)(currentInterval.Ticks * _config.IdleIntervalMultiplier));

            if (newInterval <= _config.MaxAdaptiveInterval)
            {
                _rescanService.SetRescanInterval(folderId, newInterval);
                _logger.LogDebug(
                    "Increased rescan interval for folder {FolderId} to {Interval} due to inactivity",
                    folderId, newInterval);
            }
        }
    }

    private class AdaptiveState
    {
        public int LastChangeCount { get; set; }
        public int ConsecutiveIdleScans { get; set; }
    }
}
