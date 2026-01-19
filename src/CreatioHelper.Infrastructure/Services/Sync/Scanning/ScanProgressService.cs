using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Scanning;

/// <summary>
/// Service for emitting scan progress events during folder scanning.
/// Based on Syncthing's ScanProgressIntervalS configuration.
/// </summary>
public interface IScanProgressService
{
    /// <summary>
    /// Start tracking scan progress for a folder.
    /// </summary>
    ScanProgressTracker StartScan(string folderId, long estimatedFiles = 0, long estimatedBytes = 0);

    /// <summary>
    /// Get current progress for a folder scan.
    /// </summary>
    ScanProgress? GetProgress(string folderId);

    /// <summary>
    /// Subscribe to progress events for a folder.
    /// </summary>
    IDisposable Subscribe(string folderId, Action<ScanProgress> onProgress);

    /// <summary>
    /// Subscribe to progress events for all folders.
    /// </summary>
    IDisposable SubscribeAll(Action<ScanProgress> onProgress);

    /// <summary>
    /// Check if a scan is currently in progress for a folder.
    /// </summary>
    bool IsScanInProgress(string folderId);
}

/// <summary>
/// Current scan progress information.
/// </summary>
public class ScanProgress
{
    // Use fields for Interlocked operations
    internal long _filesScanned;
    internal long _filesTotal;
    internal long _bytesScanned;
    internal long _bytesTotal;

    public string FolderId { get; init; } = string.Empty;
    public ScanPhase Phase { get; set; } = ScanPhase.Scanning;
    public long FilesScanned { get => Interlocked.Read(ref _filesScanned); set => Interlocked.Exchange(ref _filesScanned, value); }
    public long FilesTotal { get => Interlocked.Read(ref _filesTotal); set => Interlocked.Exchange(ref _filesTotal, value); }
    public long BytesScanned { get => Interlocked.Read(ref _bytesScanned); set => Interlocked.Exchange(ref _bytesScanned, value); }
    public long BytesTotal { get => Interlocked.Read(ref _bytesTotal); set => Interlocked.Exchange(ref _bytesTotal, value); }
    public string? CurrentFile { get; set; }
    public DateTime StartTime { get; init; }
    public DateTime? EndTime { get; set; }
    public TimeSpan Elapsed => (EndTime ?? DateTime.UtcNow) - StartTime;
    public double PercentComplete => FilesTotal > 0 ? (double)FilesScanned / FilesTotal * 100 : 0;
    public double BytesPercentComplete => BytesTotal > 0 ? (double)BytesScanned / BytesTotal * 100 : 0;
    public long FilesPerSecond => Elapsed.TotalSeconds > 0 ? (long)(FilesScanned / Elapsed.TotalSeconds) : 0;
    public long BytesPerSecond => Elapsed.TotalSeconds > 0 ? (long)(BytesScanned / Elapsed.TotalSeconds) : 0;
    public TimeSpan? EstimatedTimeRemaining
    {
        get
        {
            if (FilesPerSecond <= 0 || FilesTotal <= FilesScanned)
                return null;
            var remaining = FilesTotal - FilesScanned;
            return TimeSpan.FromSeconds(remaining / (double)FilesPerSecond);
        }
    }
}

/// <summary>
/// Phases of the scan process.
/// </summary>
public enum ScanPhase
{
    /// <summary>Initial enumeration of files</summary>
    Enumerating,

    /// <summary>Scanning file contents and computing hashes</summary>
    Scanning,

    /// <summary>Updating the database with scan results</summary>
    Updating,

    /// <summary>Scan completed</summary>
    Completed,

    /// <summary>Scan was cancelled</summary>
    Cancelled,

    /// <summary>Scan failed with error</summary>
    Failed
}

/// <summary>
/// Tracker for a single scan operation.
/// </summary>
public class ScanProgressTracker : IDisposable
{
    private readonly ScanProgressService _service;
    private readonly ScanProgress _progress;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    internal ScanProgressTracker(ScanProgressService service, string folderId, long estimatedFiles, long estimatedBytes)
    {
        _service = service;
        _progress = new ScanProgress
        {
            FolderId = folderId,
            FilesTotal = estimatedFiles,
            BytesTotal = estimatedBytes,
            StartTime = DateTime.UtcNow
        };
    }

    public string FolderId => _progress.FolderId;
    public ScanProgress Progress => _progress;
    public CancellationToken CancellationToken => _cts.Token;

    /// <summary>
    /// Update the current phase.
    /// </summary>
    public void SetPhase(ScanPhase phase)
    {
        _progress.Phase = phase;
        _service.NotifyProgress(_progress);
    }

    /// <summary>
    /// Update the estimated totals (e.g., after enumeration).
    /// </summary>
    public void SetEstimates(long totalFiles, long totalBytes)
    {
        _progress.FilesTotal = totalFiles;
        _progress.BytesTotal = totalBytes;
        _service.NotifyProgress(_progress);
    }

    /// <summary>
    /// Report progress on a file.
    /// </summary>
    public void ReportFile(string filePath, long fileSize)
    {
        Interlocked.Increment(ref _progress._filesScanned);
        Interlocked.Add(ref _progress._bytesScanned, fileSize);
        _progress.CurrentFile = filePath;
        _service.NotifyProgress(_progress);
    }

    /// <summary>
    /// Report multiple files (batch update).
    /// </summary>
    public void ReportFiles(int count, long totalBytes)
    {
        Interlocked.Add(ref _progress._filesScanned, count);
        Interlocked.Add(ref _progress._bytesScanned, totalBytes);
        _service.NotifyProgress(_progress);
    }

    /// <summary>
    /// Complete the scan successfully.
    /// </summary>
    public void Complete()
    {
        _progress.Phase = ScanPhase.Completed;
        _progress.EndTime = DateTime.UtcNow;
        _service.NotifyProgress(_progress);
        _service.EndScan(_progress.FolderId);
    }

    /// <summary>
    /// Cancel the scan.
    /// </summary>
    public void Cancel()
    {
        _cts.Cancel();
        _progress.Phase = ScanPhase.Cancelled;
        _progress.EndTime = DateTime.UtcNow;
        _service.NotifyProgress(_progress);
        _service.EndScan(_progress.FolderId);
    }

    /// <summary>
    /// Mark scan as failed.
    /// </summary>
    public void Fail(string? error = null)
    {
        _progress.Phase = ScanPhase.Failed;
        _progress.EndTime = DateTime.UtcNow;
        _service.NotifyProgress(_progress);
        _service.EndScan(_progress.FolderId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_progress.Phase == ScanPhase.Scanning || _progress.Phase == ScanPhase.Enumerating)
        {
            Cancel();
        }

        _cts.Dispose();
    }
}

/// <summary>
/// Implementation of scan progress service.
/// </summary>
public class ScanProgressService : IScanProgressService
{
    private readonly ILogger<ScanProgressService> _logger;
    private readonly ConcurrentDictionary<string, ScanProgressTracker> _activeScans = new();
    private readonly ConcurrentDictionary<string, ConcurrentBag<Action<ScanProgress>>> _folderSubscribers = new();
    private readonly ConcurrentBag<Action<ScanProgress>> _globalSubscribers = new();
    private readonly int _progressIntervalMs;
    private readonly ConcurrentDictionary<string, DateTime> _lastNotification = new();

    public ScanProgressService(ILogger<ScanProgressService> logger, int progressIntervalSeconds = 1)
    {
        _logger = logger;
        _progressIntervalMs = progressIntervalSeconds * 1000;
    }

    /// <inheritdoc />
    public ScanProgressTracker StartScan(string folderId, long estimatedFiles = 0, long estimatedBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        // Cancel any existing scan for this folder
        if (_activeScans.TryGetValue(folderId, out var existingTracker))
        {
            _logger.LogWarning("Cancelling existing scan for folder {FolderId}", folderId);
            existingTracker.Cancel();
        }

        var tracker = new ScanProgressTracker(this, folderId, estimatedFiles, estimatedBytes);
        _activeScans[folderId] = tracker;

        _logger.LogInformation(
            "Started scan for folder {FolderId}. Estimated: {Files} files, {Bytes} bytes",
            folderId, estimatedFiles, estimatedBytes);

        NotifyProgress(tracker.Progress);
        return tracker;
    }

    /// <inheritdoc />
    public ScanProgress? GetProgress(string folderId)
    {
        return _activeScans.TryGetValue(folderId, out var tracker)
            ? tracker.Progress
            : null;
    }

    /// <inheritdoc />
    public IDisposable Subscribe(string folderId, Action<ScanProgress> onProgress)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(onProgress);

        var subscribers = _folderSubscribers.GetOrAdd(folderId, _ => new ConcurrentBag<Action<ScanProgress>>());
        subscribers.Add(onProgress);

        return new Subscription(() =>
        {
            // Note: ConcurrentBag doesn't support removal, so we use a wrapper
            // In production, consider using a different collection
        });
    }

    /// <inheritdoc />
    public IDisposable SubscribeAll(Action<ScanProgress> onProgress)
    {
        ArgumentNullException.ThrowIfNull(onProgress);

        _globalSubscribers.Add(onProgress);
        return new Subscription(() => { });
    }

    /// <inheritdoc />
    public bool IsScanInProgress(string folderId)
    {
        return _activeScans.ContainsKey(folderId);
    }

    internal void NotifyProgress(ScanProgress progress)
    {
        // Throttle notifications based on interval
        var now = DateTime.UtcNow;
        if (_lastNotification.TryGetValue(progress.FolderId, out var lastTime))
        {
            if ((now - lastTime).TotalMilliseconds < _progressIntervalMs &&
                progress.Phase != ScanPhase.Completed &&
                progress.Phase != ScanPhase.Cancelled &&
                progress.Phase != ScanPhase.Failed)
            {
                return;
            }
        }
        _lastNotification[progress.FolderId] = now;

        // Notify folder-specific subscribers
        if (_folderSubscribers.TryGetValue(progress.FolderId, out var folderSubs))
        {
            foreach (var subscriber in folderSubs)
            {
                try
                {
                    subscriber(progress);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error notifying folder subscriber for {FolderId}", progress.FolderId);
                }
            }
        }

        // Notify global subscribers
        foreach (var subscriber in _globalSubscribers)
        {
            try
            {
                subscriber(progress);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error notifying global subscriber for {FolderId}", progress.FolderId);
            }
        }
    }

    internal void EndScan(string folderId)
    {
        _activeScans.TryRemove(folderId, out _);
        _lastNotification.TryRemove(folderId, out _);

        _logger.LogInformation("Scan ended for folder {FolderId}", folderId);
    }

    private class Subscription : IDisposable
    {
        private readonly Action _onDispose;
        private bool _disposed;

        public Subscription(Action onDispose)
        {
            _onDispose = onDispose;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _onDispose();
        }
    }
}

/// <summary>
/// Event arguments for scan progress events.
/// </summary>
public class ScanProgressEventArgs : EventArgs
{
    public ScanProgress Progress { get; }

    public ScanProgressEventArgs(ScanProgress progress)
    {
        Progress = progress;
    }
}

/// <summary>
/// Alternative implementation using events instead of callbacks.
/// </summary>
public class EventBasedScanProgressService : IScanProgressService
{
    private readonly ScanProgressService _inner;

    public event EventHandler<ScanProgressEventArgs>? ProgressChanged;

    public EventBasedScanProgressService(ILogger<ScanProgressService> logger, int progressIntervalSeconds = 1)
    {
        _inner = new ScanProgressService(logger, progressIntervalSeconds);
    }

    public ScanProgressTracker StartScan(string folderId, long estimatedFiles = 0, long estimatedBytes = 0)
    {
        var tracker = _inner.StartScan(folderId, estimatedFiles, estimatedBytes);
        _inner.SubscribeAll(p => ProgressChanged?.Invoke(this, new ScanProgressEventArgs(p)));
        return tracker;
    }

    public ScanProgress? GetProgress(string folderId) => _inner.GetProgress(folderId);

    public IDisposable Subscribe(string folderId, Action<ScanProgress> onProgress)
        => _inner.Subscribe(folderId, onProgress);

    public IDisposable SubscribeAll(Action<ScanProgress> onProgress)
        => _inner.SubscribeAll(onProgress);

    public bool IsScanInProgress(string folderId) => _inner.IsScanInProgress(folderId);
}
