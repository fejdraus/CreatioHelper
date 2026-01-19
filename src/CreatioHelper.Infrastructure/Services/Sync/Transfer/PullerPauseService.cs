using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Transfer;

/// <summary>
/// Service for pausing and resuming file synchronization (pulling).
/// Based on Syncthing's puller pause/resume functionality.
/// </summary>
public interface IPullerPauseService
{
    /// <summary>
    /// Pause pulling for a specific folder.
    /// </summary>
    Task PauseFolderAsync(string folderId, CancellationToken ct = default);

    /// <summary>
    /// Resume pulling for a specific folder.
    /// </summary>
    Task ResumeFolderAsync(string folderId, CancellationToken ct = default);

    /// <summary>
    /// Pause pulling for a specific device.
    /// </summary>
    Task PauseDeviceAsync(string deviceId, CancellationToken ct = default);

    /// <summary>
    /// Resume pulling for a specific device.
    /// </summary>
    Task ResumeDeviceAsync(string deviceId, CancellationToken ct = default);

    /// <summary>
    /// Pause all pulling operations.
    /// </summary>
    Task PauseAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Resume all pulling operations.
    /// </summary>
    Task ResumeAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Check if folder pulling is paused.
    /// </summary>
    bool IsFolderPaused(string folderId);

    /// <summary>
    /// Check if device pulling is paused.
    /// </summary>
    bool IsDevicePaused(string deviceId);

    /// <summary>
    /// Check if all pulling is paused.
    /// </summary>
    bool IsGloballyPaused { get; }

    /// <summary>
    /// Check if pulling should proceed for a folder/device combination.
    /// </summary>
    bool ShouldPull(string folderId, string deviceId);

    /// <summary>
    /// Wait until pulling is allowed for a folder/device combination.
    /// Returns false if cancelled.
    /// </summary>
    Task<bool> WaitForResumeAsync(string folderId, string deviceId, CancellationToken ct = default);

    /// <summary>
    /// Get current pause state.
    /// </summary>
    PullerPauseState GetState();

    /// <summary>
    /// Subscribe to pause state changes.
    /// </summary>
    IDisposable OnStateChanged(Action<PullerPauseState> callback);
}

/// <summary>
/// Current pause state of the puller.
/// </summary>
public class PullerPauseState
{
    public bool GloballyPaused { get; init; }
    public IReadOnlyCollection<string> PausedFolders { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> PausedDevices { get; init; } = Array.Empty<string>();
    public DateTime? GlobalPausedAt { get; init; }
    public DateTime StateChangedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Reason for pause.
/// </summary>
public enum PauseReason
{
    /// <summary>User requested pause</summary>
    UserRequested,

    /// <summary>Paused due to low disk space</summary>
    LowDiskSpace,

    /// <summary>Paused due to network issues</summary>
    NetworkIssue,

    /// <summary>Paused due to high CPU usage</summary>
    HighCpuUsage,

    /// <summary>Scheduled pause (e.g., during work hours)</summary>
    Scheduled,

    /// <summary>Paused for maintenance</summary>
    Maintenance
}

/// <summary>
/// Implementation of puller pause service.
/// </summary>
public class PullerPauseService : IPullerPauseService, IDisposable
{
    private readonly ILogger<PullerPauseService> _logger;
    private readonly ConcurrentDictionary<string, PauseInfo> _pausedFolders = new();
    private readonly ConcurrentDictionary<string, PauseInfo> _pausedDevices = new();
    private readonly ConcurrentBag<Action<PullerPauseState>> _stateChangeCallbacks = new();
    private readonly SemaphoreSlim _globalPauseLock = new(1, 1);
    private readonly ManualResetEventSlim _globalResumeEvent = new(true);
    private readonly ConcurrentDictionary<string, ManualResetEventSlim> _folderResumeEvents = new();
    private readonly ConcurrentDictionary<string, ManualResetEventSlim> _deviceResumeEvents = new();

    private bool _globallyPaused;
    private DateTime? _globalPausedAt;
    private bool _disposed;

    public PullerPauseService(ILogger<PullerPauseService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsGloballyPaused => _globallyPaused;

    /// <inheritdoc />
    public async Task PauseFolderAsync(string folderId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        var info = new PauseInfo(PauseReason.UserRequested, DateTime.UtcNow);
        _pausedFolders[folderId] = info;

        var evt = _folderResumeEvents.GetOrAdd(folderId, _ => new ManualResetEventSlim(true));
        evt.Reset();

        _logger.LogInformation("Paused pulling for folder {FolderId}", folderId);
        NotifyStateChanged();

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task ResumeFolderAsync(string folderId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        _pausedFolders.TryRemove(folderId, out _);

        if (_folderResumeEvents.TryGetValue(folderId, out var evt))
        {
            evt.Set();
        }

        _logger.LogInformation("Resumed pulling for folder {FolderId}", folderId);
        NotifyStateChanged();

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task PauseDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        var info = new PauseInfo(PauseReason.UserRequested, DateTime.UtcNow);
        _pausedDevices[deviceId] = info;

        var evt = _deviceResumeEvents.GetOrAdd(deviceId, _ => new ManualResetEventSlim(true));
        evt.Reset();

        _logger.LogInformation("Paused pulling for device {DeviceId}", deviceId);
        NotifyStateChanged();

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task ResumeDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        _pausedDevices.TryRemove(deviceId, out _);

        if (_deviceResumeEvents.TryGetValue(deviceId, out var evt))
        {
            evt.Set();
        }

        _logger.LogInformation("Resumed pulling for device {DeviceId}", deviceId);
        NotifyStateChanged();

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task PauseAllAsync(CancellationToken ct = default)
    {
        await _globalPauseLock.WaitAsync(ct);
        try
        {
            _globallyPaused = true;
            _globalPausedAt = DateTime.UtcNow;
            _globalResumeEvent.Reset();

            _logger.LogInformation("Paused all pulling operations");
            NotifyStateChanged();
        }
        finally
        {
            _globalPauseLock.Release();
        }
    }

    /// <inheritdoc />
    public async Task ResumeAllAsync(CancellationToken ct = default)
    {
        await _globalPauseLock.WaitAsync(ct);
        try
        {
            _globallyPaused = false;
            _globalPausedAt = null;
            _globalResumeEvent.Set();

            _logger.LogInformation("Resumed all pulling operations");
            NotifyStateChanged();
        }
        finally
        {
            _globalPauseLock.Release();
        }
    }

    /// <inheritdoc />
    public bool IsFolderPaused(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        return _pausedFolders.ContainsKey(folderId);
    }

    /// <inheritdoc />
    public bool IsDevicePaused(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        return _pausedDevices.ContainsKey(deviceId);
    }

    /// <inheritdoc />
    public bool ShouldPull(string folderId, string deviceId)
    {
        if (_globallyPaused)
            return false;

        if (!string.IsNullOrEmpty(folderId) && _pausedFolders.ContainsKey(folderId))
            return false;

        if (!string.IsNullOrEmpty(deviceId) && _pausedDevices.ContainsKey(deviceId))
            return false;

        return true;
    }

    /// <inheritdoc />
    public async Task<bool> WaitForResumeAsync(string folderId, string deviceId, CancellationToken ct = default)
    {
        try
        {
            // Wait for global resume
            if (_globallyPaused)
            {
                await Task.Run(() => _globalResumeEvent.Wait(ct), ct);
            }

            // Wait for folder resume
            if (!string.IsNullOrEmpty(folderId) && _folderResumeEvents.TryGetValue(folderId, out var folderEvt))
            {
                if (!folderEvt.IsSet)
                {
                    await Task.Run(() => folderEvt.Wait(ct), ct);
                }
            }

            // Wait for device resume
            if (!string.IsNullOrEmpty(deviceId) && _deviceResumeEvents.TryGetValue(deviceId, out var deviceEvt))
            {
                if (!deviceEvt.IsSet)
                {
                    await Task.Run(() => deviceEvt.Wait(ct), ct);
                }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public PullerPauseState GetState()
    {
        return new PullerPauseState
        {
            GloballyPaused = _globallyPaused,
            GlobalPausedAt = _globalPausedAt,
            PausedFolders = _pausedFolders.Keys.ToArray(),
            PausedDevices = _pausedDevices.Keys.ToArray(),
            StateChangedAt = DateTime.UtcNow
        };
    }

    /// <inheritdoc />
    public IDisposable OnStateChanged(Action<PullerPauseState> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _stateChangeCallbacks.Add(callback);
        return new CallbackDisposer(() => { /* ConcurrentBag doesn't support removal */ });
    }

    private void NotifyStateChanged()
    {
        var state = GetState();
        foreach (var callback in _stateChangeCallbacks)
        {
            try
            {
                callback(state);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error notifying pause state change");
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _globalPauseLock.Dispose();
        _globalResumeEvent.Dispose();

        foreach (var evt in _folderResumeEvents.Values)
        {
            evt.Dispose();
        }

        foreach (var evt in _deviceResumeEvents.Values)
        {
            evt.Dispose();
        }
    }

    private record PauseInfo(PauseReason Reason, DateTime PausedAt);

    private class CallbackDisposer : IDisposable
    {
        private readonly Action _onDispose;
        public CallbackDisposer(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }
}

/// <summary>
/// Scheduled pause configuration.
/// </summary>
public class ScheduledPauseConfiguration
{
    /// <summary>
    /// Enable scheduled pausing.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Days of week to apply the schedule.
    /// </summary>
    public DayOfWeek[] DaysOfWeek { get; set; } = Array.Empty<DayOfWeek>();

    /// <summary>
    /// Time to start pausing (local time).
    /// </summary>
    public TimeSpan PauseStartTime { get; set; }

    /// <summary>
    /// Time to resume (local time).
    /// </summary>
    public TimeSpan PauseEndTime { get; set; }

    /// <summary>
    /// Folders to pause (empty = all folders).
    /// </summary>
    public string[] FolderIds { get; set; } = Array.Empty<string>();
}
