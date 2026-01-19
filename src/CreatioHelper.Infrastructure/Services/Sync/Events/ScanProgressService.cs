using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Events;

/// <summary>
/// Service for tracking and reporting scan progress.
/// Based on Syncthing's scan progress events.
/// </summary>
public interface IScanProgressService
{
    /// <summary>
    /// Start tracking a scan operation.
    /// </summary>
    ScanOperation StartScan(string folderId, ScanType scanType);

    /// <summary>
    /// Update scan progress.
    /// </summary>
    void UpdateProgress(string scanId, long filesScanned, long bytesScanned, string? currentFile = null);

    /// <summary>
    /// Complete a scan operation.
    /// </summary>
    void CompleteScan(string scanId, ScanResult result);

    /// <summary>
    /// Cancel a scan operation.
    /// </summary>
    void CancelScan(string scanId);

    /// <summary>
    /// Get current scan progress for a folder.
    /// </summary>
    ScanProgress? GetProgress(string folderId);

    /// <summary>
    /// Get all active scans.
    /// </summary>
    IReadOnlyList<ScanProgress> GetActiveScans();

    /// <summary>
    /// Check if a folder is currently being scanned.
    /// </summary>
    bool IsScanning(string folderId);

    /// <summary>
    /// Subscribe to scan progress events.
    /// </summary>
    IDisposable Subscribe(Action<ScanProgressEvent> handler);

    /// <summary>
    /// Get scan history for a folder.
    /// </summary>
    IReadOnlyList<ScanHistoryEntry> GetHistory(string folderId, int limit = 10);

    /// <summary>
    /// Get scan statistics for a folder.
    /// </summary>
    ScanStatistics GetStatistics(string folderId);
}

/// <summary>
/// Type of scan operation.
/// </summary>
public enum ScanType
{
    /// <summary>
    /// Full scan of all files.
    /// </summary>
    Full,

    /// <summary>
    /// Partial scan (specific subdirectory).
    /// </summary>
    Partial,

    /// <summary>
    /// Quick scan (only recently modified).
    /// </summary>
    Quick,

    /// <summary>
    /// Watch-triggered scan (filesystem event).
    /// </summary>
    Watch
}

/// <summary>
/// Result of a scan operation.
/// </summary>
public enum ScanResult
{
    Success,
    PartialSuccess,
    Cancelled,
    Failed,
    Aborted
}

/// <summary>
/// Represents an active scan operation.
/// </summary>
public class ScanOperation
{
    public string ScanId { get; init; } = Guid.NewGuid().ToString("N");
    public string FolderId { get; init; } = string.Empty;
    public ScanType Type { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public string? SubPath { get; init; }
}

/// <summary>
/// Current progress of a scan.
/// </summary>
public class ScanProgress
{
    public string ScanId { get; set; } = string.Empty;
    public string FolderId { get; set; } = string.Empty;
    public ScanType Type { get; set; }
    public DateTime StartedAt { get; set; }
    public long FilesScanned { get; set; }
    public long BytesScanned { get; set; }
    public long? TotalFiles { get; set; }
    public long? TotalBytes { get; set; }
    public string? CurrentFile { get; set; }
    public double ProgressPercent => TotalFiles > 0 ? (double)FilesScanned / TotalFiles.Value * 100.0 : 0.0;
    public TimeSpan Elapsed => DateTime.UtcNow - StartedAt;
    public double FilesPerSecond => Elapsed.TotalSeconds > 0 ? FilesScanned / Elapsed.TotalSeconds : 0.0;
}

/// <summary>
/// Event for scan progress updates.
/// </summary>
public class ScanProgressEvent
{
    public ScanProgressEventType EventType { get; init; }
    public string ScanId { get; init; } = string.Empty;
    public string FolderId { get; init; } = string.Empty;
    public ScanProgress? Progress { get; init; }
    public ScanResult? Result { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Type of scan progress event.
/// </summary>
public enum ScanProgressEventType
{
    Started,
    Progress,
    Completed,
    Cancelled,
    Failed
}

/// <summary>
/// Entry in scan history.
/// </summary>
public class ScanHistoryEntry
{
    public string ScanId { get; init; } = string.Empty;
    public string FolderId { get; init; } = string.Empty;
    public ScanType Type { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime CompletedAt { get; init; }
    public ScanResult Result { get; init; }
    public long FilesScanned { get; init; }
    public long BytesScanned { get; init; }
    public TimeSpan Duration => CompletedAt - StartedAt;
}

/// <summary>
/// Statistics for scan operations.
/// </summary>
public class ScanStatistics
{
    public string FolderId { get; set; } = string.Empty;
    public long TotalScans { get; set; }
    public long SuccessfulScans { get; set; }
    public long FailedScans { get; set; }
    public long CancelledScans { get; set; }
    public TimeSpan TotalScanTime { get; set; }
    public TimeSpan AverageScanTime => TotalScans > 0
        ? TimeSpan.FromTicks(TotalScanTime.Ticks / TotalScans)
        : TimeSpan.Zero;
    public long TotalFilesScanned { get; set; }
    public long TotalBytesScanned { get; set; }
    public DateTime? LastScanAt { get; set; }
    public DateTime? LastSuccessfulScanAt { get; set; }
}

/// <summary>
/// Configuration for scan progress service.
/// </summary>
public class ScanProgressConfiguration
{
    /// <summary>
    /// Minimum interval between progress events.
    /// </summary>
    public TimeSpan MinProgressInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Maximum number of history entries per folder.
    /// </summary>
    public int MaxHistoryEntries { get; set; } = 100;

    /// <summary>
    /// Whether to emit progress events.
    /// </summary>
    public bool EmitProgressEvents { get; set; } = true;
}

/// <summary>
/// Implementation of scan progress service.
/// </summary>
public class ScanProgressService : IScanProgressService
{
    private readonly ILogger<ScanProgressService> _logger;
    private readonly ScanProgressConfiguration _config;
    private readonly ConcurrentDictionary<string, ScanProgress> _activeScans = new();
    private readonly ConcurrentDictionary<string, List<ScanHistoryEntry>> _history = new();
    private readonly ConcurrentDictionary<string, ScanStatistics> _statistics = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastProgressUpdate = new();
    private readonly List<Action<ScanProgressEvent>> _subscribers = new();
    private readonly object _subscriberLock = new();

    public ScanProgressService(
        ILogger<ScanProgressService> logger,
        ScanProgressConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new ScanProgressConfiguration();
    }

    /// <inheritdoc />
    public ScanOperation StartScan(string folderId, ScanType scanType)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        var operation = new ScanOperation
        {
            FolderId = folderId,
            Type = scanType
        };

        var progress = new ScanProgress
        {
            ScanId = operation.ScanId,
            FolderId = folderId,
            Type = scanType,
            StartedAt = operation.StartedAt
        };

        _activeScans[operation.ScanId] = progress;

        _logger.LogInformation("Started {ScanType} scan for folder {FolderId}: {ScanId}",
            scanType, folderId, operation.ScanId);

        EmitEvent(new ScanProgressEvent
        {
            EventType = ScanProgressEventType.Started,
            ScanId = operation.ScanId,
            FolderId = folderId,
            Progress = progress
        });

        return operation;
    }

    /// <inheritdoc />
    public void UpdateProgress(string scanId, long filesScanned, long bytesScanned, string? currentFile = null)
    {
        ArgumentNullException.ThrowIfNull(scanId);

        if (!_activeScans.TryGetValue(scanId, out var progress))
        {
            return;
        }

        progress.FilesScanned = filesScanned;
        progress.BytesScanned = bytesScanned;
        progress.CurrentFile = currentFile;

        // Throttle progress events
        if (_config.EmitProgressEvents)
        {
            var now = DateTime.UtcNow;
            var shouldEmit = !_lastProgressUpdate.TryGetValue(scanId, out var lastUpdate) ||
                             (now - lastUpdate) >= _config.MinProgressInterval;

            if (shouldEmit)
            {
                _lastProgressUpdate[scanId] = now;

                EmitEvent(new ScanProgressEvent
                {
                    EventType = ScanProgressEventType.Progress,
                    ScanId = scanId,
                    FolderId = progress.FolderId,
                    Progress = progress
                });
            }
        }
    }

    /// <inheritdoc />
    public void CompleteScan(string scanId, ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(scanId);

        if (!_activeScans.TryRemove(scanId, out var progress))
        {
            return;
        }

        _lastProgressUpdate.TryRemove(scanId, out _);

        var completedAt = DateTime.UtcNow;
        var historyEntry = new ScanHistoryEntry
        {
            ScanId = scanId,
            FolderId = progress.FolderId,
            Type = progress.Type,
            StartedAt = progress.StartedAt,
            CompletedAt = completedAt,
            Result = result,
            FilesScanned = progress.FilesScanned,
            BytesScanned = progress.BytesScanned
        };

        // Add to history
        AddToHistory(progress.FolderId, historyEntry);

        // Update statistics
        UpdateStatistics(progress.FolderId, historyEntry);

        _logger.LogInformation("Completed {ScanType} scan for folder {FolderId}: {Result} ({FilesScanned} files in {Duration})",
            progress.Type, progress.FolderId, result, progress.FilesScanned, historyEntry.Duration);

        var eventType = result switch
        {
            ScanResult.Success or ScanResult.PartialSuccess => ScanProgressEventType.Completed,
            ScanResult.Cancelled or ScanResult.Aborted => ScanProgressEventType.Cancelled,
            _ => ScanProgressEventType.Failed
        };

        EmitEvent(new ScanProgressEvent
        {
            EventType = eventType,
            ScanId = scanId,
            FolderId = progress.FolderId,
            Progress = progress,
            Result = result
        });
    }

    /// <inheritdoc />
    public void CancelScan(string scanId)
    {
        ArgumentNullException.ThrowIfNull(scanId);

        CompleteScan(scanId, ScanResult.Cancelled);
    }

    /// <inheritdoc />
    public ScanProgress? GetProgress(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        return _activeScans.Values.FirstOrDefault(p => p.FolderId == folderId);
    }

    /// <inheritdoc />
    public IReadOnlyList<ScanProgress> GetActiveScans()
    {
        return _activeScans.Values.ToList();
    }

    /// <inheritdoc />
    public bool IsScanning(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        return _activeScans.Values.Any(p => p.FolderId == folderId);
    }

    /// <inheritdoc />
    public IDisposable Subscribe(Action<ScanProgressEvent> handler)
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

    /// <inheritdoc />
    public IReadOnlyList<ScanHistoryEntry> GetHistory(string folderId, int limit = 10)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (!_history.TryGetValue(folderId, out var history))
        {
            return Array.Empty<ScanHistoryEntry>();
        }

        lock (history)
        {
            return history.TakeLast(limit).Reverse().ToList();
        }
    }

    /// <inheritdoc />
    public ScanStatistics GetStatistics(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        return _statistics.GetOrAdd(folderId, id => new ScanStatistics { FolderId = id });
    }

    private void AddToHistory(string folderId, ScanHistoryEntry entry)
    {
        var history = _history.GetOrAdd(folderId, _ => new List<ScanHistoryEntry>());

        lock (history)
        {
            history.Add(entry);

            while (history.Count > _config.MaxHistoryEntries)
            {
                history.RemoveAt(0);
            }
        }
    }

    private void UpdateStatistics(string folderId, ScanHistoryEntry entry)
    {
        var stats = _statistics.GetOrAdd(folderId, id => new ScanStatistics { FolderId = id });

        lock (stats)
        {
            stats.TotalScans++;
            stats.TotalFilesScanned += entry.FilesScanned;
            stats.TotalBytesScanned += entry.BytesScanned;
            stats.TotalScanTime += entry.Duration;
            stats.LastScanAt = entry.CompletedAt;

            switch (entry.Result)
            {
                case ScanResult.Success:
                case ScanResult.PartialSuccess:
                    stats.SuccessfulScans++;
                    stats.LastSuccessfulScanAt = entry.CompletedAt;
                    break;
                case ScanResult.Failed:
                    stats.FailedScans++;
                    break;
                case ScanResult.Cancelled:
                case ScanResult.Aborted:
                    stats.CancelledScans++;
                    break;
            }
        }
    }

    private void EmitEvent(ScanProgressEvent evt)
    {
        List<Action<ScanProgressEvent>> handlers;

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
                _logger.LogError(ex, "Error in scan progress event handler");
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
