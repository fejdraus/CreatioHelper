using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Events;

/// <summary>
/// Service for managing progress update intervals.
/// Based on Syncthing's progressUpdateIntervalS folder configuration.
/// </summary>
public interface IProgressUpdateService
{
    /// <summary>
    /// Get the progress update interval for a folder.
    /// </summary>
    TimeSpan GetUpdateInterval(string folderId);

    /// <summary>
    /// Set the progress update interval for a folder.
    /// </summary>
    void SetUpdateInterval(string folderId, TimeSpan interval);

    /// <summary>
    /// Report progress for a file transfer.
    /// </summary>
    void ReportProgress(string folderId, string filePath, TransferProgress progress);

    /// <summary>
    /// Get current progress for a folder.
    /// </summary>
    FolderProgress GetFolderProgress(string folderId);

    /// <summary>
    /// Get progress for a specific file.
    /// </summary>
    TransferProgress? GetFileProgress(string folderId, string filePath);

    /// <summary>
    /// Subscribe to progress updates.
    /// </summary>
    IDisposable Subscribe(string folderId, Action<ProgressUpdateEventArgs> callback);

    /// <summary>
    /// Subscribe to all progress updates.
    /// </summary>
    IDisposable SubscribeAll(Action<ProgressUpdateEventArgs> callback);

    /// <summary>
    /// Mark a file transfer as complete.
    /// </summary>
    void MarkComplete(string folderId, string filePath);

    /// <summary>
    /// Clear progress for a folder.
    /// </summary>
    void ClearProgress(string folderId);

    /// <summary>
    /// Enable/disable progress tracking for a folder.
    /// </summary>
    void SetProgressTrackingEnabled(string folderId, bool enabled);

    /// <summary>
    /// Check if progress tracking is enabled.
    /// </summary>
    bool IsProgressTrackingEnabled(string folderId);
}

/// <summary>
/// Progress information for a file transfer.
/// </summary>
public class TransferProgress
{
    public string FilePath { get; init; } = string.Empty;
    public long BytesTotal { get; set; }
    public long BytesTransferred { get; set; }
    public int BlocksTotal { get; set; }
    public int BlocksTransferred { get; set; }
    public DateTime StartTime { get; init; } = DateTime.UtcNow;
    public DateTime LastUpdateTime { get; set; } = DateTime.UtcNow;
    public TransferState State { get; set; } = TransferState.InProgress;

    public double PercentComplete =>
        BytesTotal > 0 ? (double)BytesTransferred / BytesTotal * 100.0 : 0.0;

    public TimeSpan Elapsed => DateTime.UtcNow - StartTime;

    public double BytesPerSecond
    {
        get
        {
            var elapsed = Elapsed.TotalSeconds;
            return elapsed > 0 ? BytesTransferred / elapsed : 0;
        }
    }

    public TimeSpan? EstimatedTimeRemaining
    {
        get
        {
            var bps = BytesPerSecond;
            if (bps <= 0 || BytesTransferred >= BytesTotal)
            {
                return null;
            }
            var remaining = BytesTotal - BytesTransferred;
            return TimeSpan.FromSeconds(remaining / bps);
        }
    }
}

/// <summary>
/// State of a file transfer.
/// </summary>
public enum TransferState
{
    Pending,
    InProgress,
    Paused,
    Complete,
    Failed
}

/// <summary>
/// Progress information for a folder.
/// </summary>
public class FolderProgress
{
    public string FolderId { get; init; } = string.Empty;
    public int FilesInProgress { get; init; }
    public int FilesComplete { get; init; }
    public int FilesFailed { get; init; }
    public long BytesTotal { get; init; }
    public long BytesTransferred { get; init; }
    public DateTime? LastUpdateTime { get; init; }

    public double PercentComplete =>
        BytesTotal > 0 ? (double)BytesTransferred / BytesTotal * 100.0 : 0.0;
}

/// <summary>
/// Event args for progress updates.
/// </summary>
public class ProgressUpdateEventArgs : EventArgs
{
    public string FolderId { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public TransferProgress Progress { get; init; } = new();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Configuration for progress updates.
/// </summary>
public class ProgressUpdateConfiguration
{
    /// <summary>
    /// Default update interval (5 seconds, matching Syncthing default).
    /// </summary>
    public TimeSpan DefaultUpdateInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Per-folder update interval overrides.
    /// </summary>
    public Dictionary<string, TimeSpan> FolderIntervals { get; } = new();

    /// <summary>
    /// Minimum update interval (to prevent flooding).
    /// </summary>
    public TimeSpan MinUpdateInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Maximum update interval.
    /// </summary>
    public TimeSpan MaxUpdateInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether progress tracking is enabled by default.
    /// </summary>
    public bool DefaultProgressTrackingEnabled { get; set; } = true;

    /// <summary>
    /// Folders with progress tracking disabled.
    /// </summary>
    public HashSet<string> DisabledFolders { get; } = new();

    /// <summary>
    /// Get effective interval for a folder.
    /// </summary>
    public TimeSpan GetEffectiveInterval(string folderId)
    {
        if (FolderIntervals.TryGetValue(folderId, out var interval))
        {
            return ClampInterval(interval);
        }
        return DefaultUpdateInterval;
    }

    /// <summary>
    /// Clamp interval to valid range.
    /// </summary>
    public TimeSpan ClampInterval(TimeSpan interval)
    {
        if (interval < MinUpdateInterval) return MinUpdateInterval;
        if (interval > MaxUpdateInterval) return MaxUpdateInterval;
        return interval;
    }
}

/// <summary>
/// Implementation of progress update service.
/// </summary>
public class ProgressUpdateService : IProgressUpdateService, IDisposable
{
    private readonly ILogger<ProgressUpdateService> _logger;
    private readonly ProgressUpdateConfiguration _config;
    private readonly ConcurrentDictionary<string, FolderProgressState> _folderStates = new();
    private readonly ConcurrentDictionary<Guid, Action<ProgressUpdateEventArgs>> _globalSubscribers = new();
    private bool _disposed;

    public ProgressUpdateService(
        ILogger<ProgressUpdateService> logger,
        ProgressUpdateConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new ProgressUpdateConfiguration();
    }

    /// <inheritdoc />
    public TimeSpan GetUpdateInterval(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        return _config.GetEffectiveInterval(folderId);
    }

    /// <inheritdoc />
    public void SetUpdateInterval(string folderId, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        var clamped = _config.ClampInterval(interval);
        _config.FolderIntervals[folderId] = clamped;

        _logger.LogInformation("Set progress update interval for folder {FolderId} to {Interval}",
            folderId, clamped);
    }

    /// <inheritdoc />
    public void ReportProgress(string folderId, string filePath, TransferProgress progress)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(progress);

        if (!IsProgressTrackingEnabled(folderId))
        {
            return;
        }

        var state = GetOrCreateFolderState(folderId);
        var interval = GetUpdateInterval(folderId);

        // Update file progress
        state.FileProgress[filePath] = progress;
        progress.LastUpdateTime = DateTime.UtcNow;

        // Check if we should emit an update
        if (DateTime.UtcNow - state.LastNotificationTime >= interval)
        {
            state.LastNotificationTime = DateTime.UtcNow;
            NotifySubscribers(folderId, filePath, progress);
        }
    }

    /// <inheritdoc />
    public FolderProgress GetFolderProgress(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (!_folderStates.TryGetValue(folderId, out var state))
        {
            return new FolderProgress { FolderId = folderId };
        }

        long bytesTotal = 0;
        long bytesTransferred = 0;
        int inProgress = 0;
        int complete = 0;
        int failed = 0;
        DateTime? lastUpdate = null;

        foreach (var progress in state.FileProgress.Values)
        {
            bytesTotal += progress.BytesTotal;
            bytesTransferred += progress.BytesTransferred;

            switch (progress.State)
            {
                case TransferState.InProgress:
                case TransferState.Pending:
                    inProgress++;
                    break;
                case TransferState.Complete:
                    complete++;
                    break;
                case TransferState.Failed:
                    failed++;
                    break;
            }

            if (!lastUpdate.HasValue || progress.LastUpdateTime > lastUpdate.Value)
            {
                lastUpdate = progress.LastUpdateTime;
            }
        }

        return new FolderProgress
        {
            FolderId = folderId,
            FilesInProgress = inProgress,
            FilesComplete = complete,
            FilesFailed = failed,
            BytesTotal = bytesTotal,
            BytesTransferred = bytesTransferred,
            LastUpdateTime = lastUpdate
        };
    }

    /// <inheritdoc />
    public TransferProgress? GetFileProgress(string folderId, string filePath)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(filePath);

        if (_folderStates.TryGetValue(folderId, out var state) &&
            state.FileProgress.TryGetValue(filePath, out var progress))
        {
            return progress;
        }

        return null;
    }

    /// <inheritdoc />
    public IDisposable Subscribe(string folderId, Action<ProgressUpdateEventArgs> callback)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(callback);

        var state = GetOrCreateFolderState(folderId);
        var id = Guid.NewGuid();
        state.Subscribers[id] = callback;

        return new Subscription(() => state.Subscribers.TryRemove(id, out _));
    }

    /// <inheritdoc />
    public IDisposable SubscribeAll(Action<ProgressUpdateEventArgs> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var id = Guid.NewGuid();
        _globalSubscribers[id] = callback;

        return new Subscription(() => _globalSubscribers.TryRemove(id, out _));
    }

    /// <inheritdoc />
    public void MarkComplete(string folderId, string filePath)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(filePath);

        if (_folderStates.TryGetValue(folderId, out var state) &&
            state.FileProgress.TryGetValue(filePath, out var progress))
        {
            progress.State = TransferState.Complete;
            progress.BytesTransferred = progress.BytesTotal;
            progress.LastUpdateTime = DateTime.UtcNow;

            NotifySubscribers(folderId, filePath, progress);
        }
    }

    /// <inheritdoc />
    public void ClearProgress(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (_folderStates.TryGetValue(folderId, out var state))
        {
            state.FileProgress.Clear();
        }

        _logger.LogDebug("Cleared progress for folder {FolderId}", folderId);
    }

    /// <inheritdoc />
    public void SetProgressTrackingEnabled(string folderId, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (enabled)
        {
            _config.DisabledFolders.Remove(folderId);
        }
        else
        {
            _config.DisabledFolders.Add(folderId);
        }

        _logger.LogInformation("Set progress tracking for folder {FolderId} to {Enabled}", folderId, enabled);
    }

    /// <inheritdoc />
    public bool IsProgressTrackingEnabled(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (_config.DisabledFolders.Contains(folderId))
        {
            return false;
        }

        return _config.DefaultProgressTrackingEnabled;
    }

    private FolderProgressState GetOrCreateFolderState(string folderId)
    {
        return _folderStates.GetOrAdd(folderId, _ => new FolderProgressState());
    }

    private void NotifySubscribers(string folderId, string filePath, TransferProgress progress)
    {
        var args = new ProgressUpdateEventArgs
        {
            FolderId = folderId,
            FilePath = filePath,
            Progress = progress
        };

        // Notify folder-specific subscribers
        if (_folderStates.TryGetValue(folderId, out var state))
        {
            foreach (var callback in state.Subscribers.Values)
            {
                try
                {
                    callback(args);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in progress subscriber callback");
                }
            }
        }

        // Notify global subscribers
        foreach (var callback in _globalSubscribers.Values)
        {
            try
            {
                callback(args);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in global progress subscriber callback");
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _folderStates.Clear();
            _globalSubscribers.Clear();
        }
    }

    private class FolderProgressState
    {
        public ConcurrentDictionary<string, TransferProgress> FileProgress { get; } = new();
        public ConcurrentDictionary<Guid, Action<ProgressUpdateEventArgs>> Subscribers { get; } = new();
        public DateTime LastNotificationTime { get; set; } = DateTime.MinValue;
    }

    private class Subscription : IDisposable
    {
        private readonly Action _unsubscribe;
        private bool _disposed;

        public Subscription(Action unsubscribe)
        {
            _unsubscribe = unsubscribe;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _unsubscribe();
            }
        }
    }
}
