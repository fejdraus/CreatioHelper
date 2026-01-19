using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.DeviceManagement;

/// <summary>
/// Service for managing introduction removal skipping.
/// Based on Syncthing's skipIntroductionRemovals option.
/// </summary>
public interface ISkipIntroductionRemovalsService
{
    /// <summary>
    /// Check if introduction removals should be skipped for a device.
    /// </summary>
    bool ShouldSkipRemovals(string deviceId);

    /// <summary>
    /// Set skip removals for a device.
    /// </summary>
    void SetSkipRemovals(string deviceId, bool skip);

    /// <summary>
    /// Get all devices with skip removals enabled.
    /// </summary>
    IReadOnlyList<string> GetDevicesWithSkipRemovals();

    /// <summary>
    /// Check if a specific removal should be skipped.
    /// </summary>
    RemovalDecision ShouldSkipRemoval(string deviceId, string introducerDeviceId, RemovalType removalType);

    /// <summary>
    /// Record a skipped removal.
    /// </summary>
    void RecordSkippedRemoval(string deviceId, string introducerDeviceId, RemovalType removalType, string? reason = null);

    /// <summary>
    /// Record an applied removal.
    /// </summary>
    void RecordAppliedRemoval(string deviceId, string introducerDeviceId, RemovalType removalType);

    /// <summary>
    /// Get removal statistics for a device.
    /// </summary>
    RemovalStats GetStats(string deviceId);

    /// <summary>
    /// Get all removal statistics.
    /// </summary>
    IReadOnlyList<RemovalStats> GetAllStats();

    /// <summary>
    /// Get pending removals that were skipped.
    /// </summary>
    IReadOnlyList<PendingRemoval> GetPendingRemovals(string deviceId);

    /// <summary>
    /// Apply a previously skipped removal.
    /// </summary>
    bool ApplyPendingRemoval(string removalId);

    /// <summary>
    /// Clear all pending removals for a device.
    /// </summary>
    void ClearPendingRemovals(string deviceId);

    /// <summary>
    /// Subscribe to removal events.
    /// </summary>
    IDisposable Subscribe(Action<RemovalEvent> handler);
}

/// <summary>
/// Type of removal.
/// </summary>
public enum RemovalType
{
    /// <summary>
    /// Device was removed by introducer.
    /// </summary>
    DeviceRemoval,

    /// <summary>
    /// Folder was removed by introducer.
    /// </summary>
    FolderRemoval,

    /// <summary>
    /// Device was removed from folder by introducer.
    /// </summary>
    FolderDeviceRemoval
}

/// <summary>
/// Decision about removal.
/// </summary>
public enum RemovalDecision
{
    /// <summary>
    /// Apply the removal.
    /// </summary>
    Apply,

    /// <summary>
    /// Skip due to device setting.
    /// </summary>
    SkipDeviceSetting,

    /// <summary>
    /// Skip due to global setting.
    /// </summary>
    SkipGlobalSetting,

    /// <summary>
    /// Skip due to pending confirmation.
    /// </summary>
    SkipPendingConfirmation
}

/// <summary>
/// A pending removal that was skipped.
/// </summary>
public class PendingRemoval
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string DeviceId { get; init; } = string.Empty;
    public string IntroducerDeviceId { get; init; } = string.Empty;
    public RemovalType Type { get; init; }
    public string? TargetId { get; init; }
    public string? Reason { get; init; }
    public DateTime SkippedAt { get; init; } = DateTime.UtcNow;
    public bool Applied { get; set; }
    public DateTime? AppliedAt { get; set; }
}

/// <summary>
/// Removal statistics for a device.
/// </summary>
public class RemovalStats
{
    public string DeviceId { get; set; } = string.Empty;
    public bool SkipRemovalsEnabled { get; set; }
    public long TotalRemovalsReceived { get; set; }
    public long RemovalsApplied { get; set; }
    public long RemovalsSkipped { get; set; }
    public long PendingRemovals { get; set; }
    public DateTime? LastRemovalReceived { get; set; }
    public DateTime? LastRemovalApplied { get; set; }
    public DateTime? LastRemovalSkipped { get; set; }

    public double SkipRate => TotalRemovalsReceived > 0
        ? (double)RemovalsSkipped / TotalRemovalsReceived * 100.0
        : 0.0;
}

/// <summary>
/// Event for removal operations.
/// </summary>
public class RemovalEvent
{
    public RemovalEventType EventType { get; init; }
    public string DeviceId { get; init; } = string.Empty;
    public string IntroducerDeviceId { get; init; } = string.Empty;
    public RemovalType RemovalType { get; init; }
    public RemovalDecision Decision { get; init; }
    public string? TargetId { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Type of removal event.
/// </summary>
public enum RemovalEventType
{
    RemovalReceived,
    RemovalApplied,
    RemovalSkipped,
    PendingRemovalApplied,
    PendingRemovalCleared
}

/// <summary>
/// Configuration for skip introduction removals.
/// </summary>
public class SkipIntroductionRemovalsConfiguration
{
    /// <summary>
    /// Default skip removals setting for new devices.
    /// </summary>
    public bool DefaultSkipRemovals { get; set; } = false;

    /// <summary>
    /// Global skip removals override.
    /// </summary>
    public bool GlobalSkipRemovals { get; set; } = false;

    /// <summary>
    /// Maximum pending removals to keep per device.
    /// </summary>
    public int MaxPendingRemovalsPerDevice { get; set; } = 100;

    /// <summary>
    /// Auto-apply pending removals after this duration.
    /// </summary>
    public TimeSpan? AutoApplyAfter { get; set; } = null;

    /// <summary>
    /// Log skipped removals.
    /// </summary>
    public bool LogSkippedRemovals { get; set; } = true;

    /// <summary>
    /// Per-device settings.
    /// </summary>
    public Dictionary<string, bool> DeviceSettings { get; } = new();
}

/// <summary>
/// Implementation of skip introduction removals service.
/// </summary>
public class SkipIntroductionRemovalsService : ISkipIntroductionRemovalsService
{
    private readonly ILogger<SkipIntroductionRemovalsService> _logger;
    private readonly SkipIntroductionRemovalsConfiguration _config;
    private readonly ConcurrentDictionary<string, bool> _deviceSettings = new();
    private readonly ConcurrentDictionary<string, RemovalStats> _stats = new();
    private readonly ConcurrentDictionary<string, List<PendingRemoval>> _pendingRemovals = new();
    private readonly List<Action<RemovalEvent>> _subscribers = new();
    private readonly object _subscriberLock = new();

    public SkipIntroductionRemovalsService(
        ILogger<SkipIntroductionRemovalsService> logger,
        SkipIntroductionRemovalsConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new SkipIntroductionRemovalsConfiguration();

        // Load device settings from config
        foreach (var (deviceId, skip) in _config.DeviceSettings)
        {
            _deviceSettings[deviceId] = skip;
        }
    }

    /// <inheritdoc />
    public bool ShouldSkipRemovals(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        if (_config.GlobalSkipRemovals)
        {
            return true;
        }

        return _deviceSettings.GetValueOrDefault(deviceId, _config.DefaultSkipRemovals);
    }

    /// <inheritdoc />
    public void SetSkipRemovals(string deviceId, bool skip)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        _deviceSettings[deviceId] = skip;
        _config.DeviceSettings[deviceId] = skip;

        var stats = GetOrCreateStats(deviceId);
        stats.SkipRemovalsEnabled = skip;

        _logger.LogInformation("Set skip introduction removals to {Skip} for device {DeviceId}", skip, deviceId);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetDevicesWithSkipRemovals()
    {
        return _deviceSettings
            .Where(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();
    }

    /// <inheritdoc />
    public RemovalDecision ShouldSkipRemoval(string deviceId, string introducerDeviceId, RemovalType removalType)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(introducerDeviceId);

        var stats = GetOrCreateStats(deviceId);
        stats.TotalRemovalsReceived++;
        stats.LastRemovalReceived = DateTime.UtcNow;

        if (_config.GlobalSkipRemovals)
        {
            return RemovalDecision.SkipGlobalSetting;
        }

        if (ShouldSkipRemovals(deviceId))
        {
            return RemovalDecision.SkipDeviceSetting;
        }

        return RemovalDecision.Apply;
    }

    /// <inheritdoc />
    public void RecordSkippedRemoval(string deviceId, string introducerDeviceId, RemovalType removalType, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(introducerDeviceId);

        var stats = GetOrCreateStats(deviceId);
        stats.RemovalsSkipped++;
        stats.LastRemovalSkipped = DateTime.UtcNow;

        // Add to pending removals
        var pending = new PendingRemoval
        {
            DeviceId = deviceId,
            IntroducerDeviceId = introducerDeviceId,
            Type = removalType,
            Reason = reason
        };

        var list = _pendingRemovals.GetOrAdd(deviceId, _ => new List<PendingRemoval>());
        lock (list)
        {
            list.Add(pending);
            stats.PendingRemovals = list.Count(p => !p.Applied);

            // Trim if exceeds max
            while (list.Count > _config.MaxPendingRemovalsPerDevice)
            {
                list.RemoveAt(0);
            }
        }

        if (_config.LogSkippedRemovals)
        {
            _logger.LogInformation(
                "Skipped {RemovalType} from introducer {IntroducerId} for device {DeviceId}: {Reason}",
                removalType, introducerDeviceId, deviceId, reason ?? "skipIntroductionRemovals enabled");
        }

        EmitEvent(new RemovalEvent
        {
            EventType = RemovalEventType.RemovalSkipped,
            DeviceId = deviceId,
            IntroducerDeviceId = introducerDeviceId,
            RemovalType = removalType,
            Decision = ShouldSkipRemovals(deviceId) ? RemovalDecision.SkipDeviceSetting : RemovalDecision.SkipGlobalSetting
        });
    }

    /// <inheritdoc />
    public void RecordAppliedRemoval(string deviceId, string introducerDeviceId, RemovalType removalType)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(introducerDeviceId);

        var stats = GetOrCreateStats(deviceId);
        stats.RemovalsApplied++;
        stats.LastRemovalApplied = DateTime.UtcNow;

        _logger.LogDebug("Applied {RemovalType} from introducer {IntroducerId} for device {DeviceId}",
            removalType, introducerDeviceId, deviceId);

        EmitEvent(new RemovalEvent
        {
            EventType = RemovalEventType.RemovalApplied,
            DeviceId = deviceId,
            IntroducerDeviceId = introducerDeviceId,
            RemovalType = removalType,
            Decision = RemovalDecision.Apply
        });
    }

    /// <inheritdoc />
    public RemovalStats GetStats(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        var stats = GetOrCreateStats(deviceId);
        stats.SkipRemovalsEnabled = ShouldSkipRemovals(deviceId);
        return stats;
    }

    /// <inheritdoc />
    public IReadOnlyList<RemovalStats> GetAllStats()
    {
        return _stats.Values.ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<PendingRemoval> GetPendingRemovals(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        if (!_pendingRemovals.TryGetValue(deviceId, out var list))
        {
            return Array.Empty<PendingRemoval>();
        }

        lock (list)
        {
            return list.Where(p => !p.Applied).ToList();
        }
    }

    /// <inheritdoc />
    public bool ApplyPendingRemoval(string removalId)
    {
        ArgumentNullException.ThrowIfNull(removalId);

        foreach (var (deviceId, list) in _pendingRemovals)
        {
            lock (list)
            {
                var pending = list.FirstOrDefault(p => p.Id == removalId);
                if (pending != null && !pending.Applied)
                {
                    pending.Applied = true;
                    pending.AppliedAt = DateTime.UtcNow;

                    var stats = GetOrCreateStats(deviceId);
                    stats.RemovalsApplied++;
                    stats.PendingRemovals = list.Count(p => !p.Applied);
                    stats.LastRemovalApplied = DateTime.UtcNow;

                    _logger.LogInformation("Applied pending removal {RemovalId} for device {DeviceId}",
                        removalId, deviceId);

                    EmitEvent(new RemovalEvent
                    {
                        EventType = RemovalEventType.PendingRemovalApplied,
                        DeviceId = deviceId,
                        IntroducerDeviceId = pending.IntroducerDeviceId,
                        RemovalType = pending.Type
                    });

                    return true;
                }
            }
        }

        return false;
    }

    /// <inheritdoc />
    public void ClearPendingRemovals(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        if (_pendingRemovals.TryGetValue(deviceId, out var list))
        {
            lock (list)
            {
                list.Clear();
            }
        }

        var stats = GetOrCreateStats(deviceId);
        stats.PendingRemovals = 0;

        _logger.LogInformation("Cleared pending removals for device {DeviceId}", deviceId);

        EmitEvent(new RemovalEvent
        {
            EventType = RemovalEventType.PendingRemovalCleared,
            DeviceId = deviceId,
            IntroducerDeviceId = string.Empty,
            RemovalType = RemovalType.DeviceRemoval
        });
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Action<RemovalEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_subscriberLock)
        {
            _subscribers.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_subscriberLock)
            {
                _subscribers.Remove(handler);
            }
        });
    }

    private RemovalStats GetOrCreateStats(string deviceId)
    {
        return _stats.GetOrAdd(deviceId, id => new RemovalStats
        {
            DeviceId = id,
            SkipRemovalsEnabled = ShouldSkipRemovals(id)
        });
    }

    private void EmitEvent(RemovalEvent evt)
    {
        List<Action<RemovalEvent>> handlers;

        lock (_subscriberLock)
        {
            handlers = _subscribers.ToList();
        }

        foreach (var handler in handlers)
        {
            try
            {
                handler(evt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in removal event handler");
            }
        }
    }

    private class Subscription : IDisposable
    {
        private readonly Action _onDispose;

        public Subscription(Action onDispose)
        {
            _onDispose = onDispose;
        }

        public void Dispose() => _onDispose();
    }
}
