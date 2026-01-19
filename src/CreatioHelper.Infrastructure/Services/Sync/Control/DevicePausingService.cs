using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Control;

/// <summary>
/// Service for pausing and resuming individual devices.
/// Based on Syncthing's Paused device configuration option.
/// </summary>
public interface IDevicePausingService
{
    /// <summary>
    /// Check if a device is paused.
    /// </summary>
    bool IsDevicePaused(string deviceId);

    /// <summary>
    /// Pause a device.
    /// </summary>
    Task PauseDeviceAsync(string deviceId, string? reason = null);

    /// <summary>
    /// Resume a device.
    /// </summary>
    Task ResumeDeviceAsync(string deviceId);

    /// <summary>
    /// Toggle device pause state.
    /// </summary>
    Task<bool> ToggleDevicePauseAsync(string deviceId);

    /// <summary>
    /// Get all paused devices.
    /// </summary>
    IReadOnlyList<string> GetPausedDevices();

    /// <summary>
    /// Get pause information for a device.
    /// </summary>
    DevicePauseInfo? GetPauseInfo(string deviceId);

    /// <summary>
    /// Wait until device is resumed (or cancellation).
    /// </summary>
    Task WaitUntilResumedAsync(string deviceId, CancellationToken ct = default);

    /// <summary>
    /// Check if sync should proceed for a device/folder combination.
    /// </summary>
    bool ShouldSync(string deviceId, string folderId, IFolderPausingService? folderPausingService = null);

    /// <summary>
    /// Event raised when a device is paused.
    /// </summary>
    event EventHandler<DevicePauseEventArgs>? DevicePaused;

    /// <summary>
    /// Event raised when a device is resumed.
    /// </summary>
    event EventHandler<DevicePauseEventArgs>? DeviceResumed;
}

/// <summary>
/// Information about a paused device.
/// </summary>
public class DevicePauseInfo
{
    public string DeviceId { get; init; } = string.Empty;
    public bool IsPaused { get; init; }
    public string? PauseReason { get; init; }
    public DateTime? PausedAt { get; init; }
    public TimeSpan? PauseDuration => PausedAt.HasValue ? DateTime.UtcNow - PausedAt.Value : null;
    public int PendingBytes { get; init; }
    public int PendingItems { get; init; }
}

/// <summary>
/// Event args for device pause events.
/// </summary>
public class DevicePauseEventArgs : EventArgs
{
    public string DeviceId { get; init; } = string.Empty;
    public bool IsPaused { get; init; }
    public string? Reason { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Configuration for device pausing.
/// </summary>
public class DevicePausingConfiguration
{
    /// <summary>
    /// Initially paused devices.
    /// </summary>
    public HashSet<string> InitiallyPausedDevices { get; } = new();

    /// <summary>
    /// Whether to persist pause state across restarts.
    /// </summary>
    public bool PersistPauseState { get; set; } = true;

    /// <summary>
    /// Whether to disconnect immediately when paused.
    /// </summary>
    public bool DisconnectOnPause { get; set; } = false;

    /// <summary>
    /// Auto-resume after duration (null = never auto-resume).
    /// </summary>
    public TimeSpan? AutoResumeAfter { get; set; }
}

/// <summary>
/// Implementation of device pausing service.
/// </summary>
public class DevicePausingService : IDevicePausingService, IDisposable
{
    private readonly ILogger<DevicePausingService> _logger;
    private readonly DevicePausingConfiguration _config;
    private readonly ConcurrentDictionary<string, DevicePauseState> _pauseStates = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private bool _disposed;

    public event EventHandler<DevicePauseEventArgs>? DevicePaused;
    public event EventHandler<DevicePauseEventArgs>? DeviceResumed;

    public DevicePausingService(
        ILogger<DevicePausingService> logger,
        DevicePausingConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new DevicePausingConfiguration();

        // Initialize from config
        foreach (var deviceId in _config.InitiallyPausedDevices)
        {
            _pauseStates[deviceId] = new DevicePauseState
            {
                IsPaused = true,
                PausedAt = DateTime.UtcNow,
                Reason = "Initially paused from configuration"
            };
        }
    }

    /// <inheritdoc />
    public bool IsDevicePaused(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        if (_pauseStates.TryGetValue(deviceId, out var state))
        {
            return state.IsPaused;
        }
        return false;
    }

    /// <inheritdoc />
    public Task PauseDeviceAsync(string deviceId, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        var state = _pauseStates.GetOrAdd(deviceId, _ => new DevicePauseState());

        lock (state)
        {
            if (state.IsPaused)
            {
                _logger.LogDebug("Device {DeviceId} is already paused", deviceId);
                return Task.CompletedTask;
            }

            state.IsPaused = true;
            state.PausedAt = DateTime.UtcNow;
            state.Reason = reason;
            state.ResumeEvent.Reset();
        }

        _logger.LogInformation("Paused device {DeviceId}. Reason: {Reason}", deviceId, reason ?? "No reason provided");

        // Start auto-resume timer if configured
        if (_config.AutoResumeAfter.HasValue)
        {
            _ = AutoResumeAfterDelayAsync(deviceId, _config.AutoResumeAfter.Value);
        }

        RaiseDevicePaused(deviceId, reason);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResumeDeviceAsync(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        if (!_pauseStates.TryGetValue(deviceId, out var state))
        {
            return Task.CompletedTask;
        }

        lock (state)
        {
            if (!state.IsPaused)
            {
                _logger.LogDebug("Device {DeviceId} is not paused", deviceId);
                return Task.CompletedTask;
            }

            state.IsPaused = false;
            state.PausedAt = null;
            state.Reason = null;
            state.ResumeEvent.Set();
        }

        _logger.LogInformation("Resumed device {DeviceId}", deviceId);

        RaiseDeviceResumed(deviceId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<bool> ToggleDevicePauseAsync(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        if (IsDevicePaused(deviceId))
        {
            await ResumeDeviceAsync(deviceId);
            return false; // Now not paused
        }
        else
        {
            await PauseDeviceAsync(deviceId);
            return true; // Now paused
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetPausedDevices()
    {
        var paused = new List<string>();
        foreach (var kvp in _pauseStates)
        {
            if (kvp.Value.IsPaused)
            {
                paused.Add(kvp.Key);
            }
        }
        return paused;
    }

    /// <inheritdoc />
    public DevicePauseInfo? GetPauseInfo(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        if (_pauseStates.TryGetValue(deviceId, out var state))
        {
            lock (state)
            {
                return new DevicePauseInfo
                {
                    DeviceId = deviceId,
                    IsPaused = state.IsPaused,
                    PauseReason = state.Reason,
                    PausedAt = state.PausedAt,
                    PendingBytes = state.PendingBytes,
                    PendingItems = state.PendingItems
                };
            }
        }

        return new DevicePauseInfo
        {
            DeviceId = deviceId,
            IsPaused = false
        };
    }

    /// <inheritdoc />
    public async Task WaitUntilResumedAsync(string deviceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        if (!_pauseStates.TryGetValue(deviceId, out var state))
        {
            return; // Not paused
        }

        if (!state.IsPaused)
        {
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);

        try
        {
            await Task.Run(() => state.ResumeEvent.Wait(linkedCts.Token), linkedCts.Token);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(DevicePausingService));
        }
    }

    /// <inheritdoc />
    public bool ShouldSync(string deviceId, string folderId, IFolderPausingService? folderPausingService = null)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(folderId);

        // Check device pause
        if (IsDevicePaused(deviceId))
        {
            return false;
        }

        // Check folder pause if service provided
        if (folderPausingService != null && folderPausingService.IsFolderPaused(folderId))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Update pending items/bytes for a paused device.
    /// </summary>
    public void UpdatePendingStats(string deviceId, int pendingItems, int pendingBytes)
    {
        if (_pauseStates.TryGetValue(deviceId, out var state))
        {
            lock (state)
            {
                state.PendingItems = pendingItems;
                state.PendingBytes = pendingBytes;
            }
        }
    }

    private async Task AutoResumeAfterDelayAsync(string deviceId, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _disposeCts.Token);

            if (IsDevicePaused(deviceId))
            {
                _logger.LogInformation("Auto-resuming device {DeviceId} after {Delay}", deviceId, delay);
                await ResumeDeviceAsync(deviceId);
            }
        }
        catch (OperationCanceledException)
        {
            // Service is disposing
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during auto-resume of device {DeviceId}", deviceId);
        }
    }

    private void RaiseDevicePaused(string deviceId, string? reason)
    {
        DevicePaused?.Invoke(this, new DevicePauseEventArgs
        {
            DeviceId = deviceId,
            IsPaused = true,
            Reason = reason
        });
    }

    private void RaiseDeviceResumed(string deviceId)
    {
        DeviceResumed?.Invoke(this, new DevicePauseEventArgs
        {
            DeviceId = deviceId,
            IsPaused = false
        });
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _disposeCts.Cancel();
            _disposeCts.Dispose();

            foreach (var state in _pauseStates.Values)
            {
                state.Dispose();
            }
        }
    }

    private class DevicePauseState : IDisposable
    {
        public bool IsPaused { get; set; }
        public DateTime? PausedAt { get; set; }
        public string? Reason { get; set; }
        public int PendingItems { get; set; }
        public int PendingBytes { get; set; }
        public ManualResetEventSlim ResumeEvent { get; } = new(true);

        public void Dispose()
        {
            ResumeEvent.Dispose();
        }
    }
}
