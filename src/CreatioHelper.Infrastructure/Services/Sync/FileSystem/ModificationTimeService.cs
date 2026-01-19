using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// Service for managing file modification time comparisons with configurable tolerance.
/// Based on Syncthing's modification time window functionality.
/// </summary>
public interface IModificationTimeService
{
    /// <summary>
    /// Check if two modification times are considered equal within the configured window.
    /// </summary>
    bool AreTimesEqual(string folderId, DateTime time1, DateTime time2);

    /// <summary>
    /// Get the modification time window for a folder.
    /// </summary>
    TimeSpan GetTimeWindow(string folderId);

    /// <summary>
    /// Set the modification time window for a folder.
    /// </summary>
    void SetTimeWindow(string folderId, TimeSpan window);

    /// <summary>
    /// Reset to default time window for a folder.
    /// </summary>
    void ResetTimeWindow(string folderId);

    /// <summary>
    /// Normalize a modification time (truncate to configured precision).
    /// </summary>
    DateTime NormalizeTime(string folderId, DateTime time);

    /// <summary>
    /// Check if a file needs sync based on modification time.
    /// </summary>
    bool NeedsSync(string folderId, DateTime localTime, DateTime remoteTime);

    /// <summary>
    /// Get statistics for modification time comparisons.
    /// </summary>
    ModificationTimeStats GetStats(string folderId);
}

/// <summary>
/// Configuration for modification time service.
/// </summary>
public class ModificationTimeConfiguration
{
    /// <summary>
    /// Default time window for comparison (Syncthing default is 2 seconds).
    /// </summary>
    public TimeSpan DefaultTimeWindow { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Per-folder time window overrides.
    /// </summary>
    public ConcurrentDictionary<string, TimeSpan> FolderTimeWindows { get; } = new();

    /// <summary>
    /// Precision for time normalization.
    /// </summary>
    public TimePrecision DefaultPrecision { get; set; } = TimePrecision.Second;

    /// <summary>
    /// Whether to use FAT filesystem compatibility mode (2-second precision).
    /// </summary>
    public bool FatCompatibilityMode { get; set; } = false;

    /// <summary>
    /// Maximum allowed time window.
    /// </summary>
    public TimeSpan MaxTimeWindow { get; set; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Time precision levels.
/// </summary>
public enum TimePrecision
{
    Nanosecond,
    Microsecond,
    Millisecond,
    Second,
    TwoSeconds // FAT filesystem compatibility
}

/// <summary>
/// Statistics for modification time comparisons.
/// </summary>
public class ModificationTimeStats
{
    public string FolderId { get; set; } = string.Empty;
    public TimeSpan TimeWindow { get; set; }
    public long ComparisonsCount { get; set; }
    public long MatchesWithinWindow { get; set; }
    public long ExactMatches { get; set; }
    public long Mismatches { get; set; }
    public double MatchRate => ComparisonsCount > 0
        ? (double)(MatchesWithinWindow + ExactMatches) / ComparisonsCount * 100.0
        : 0.0;
}

/// <summary>
/// Implementation of modification time service.
/// </summary>
public class ModificationTimeService : IModificationTimeService
{
    private readonly ILogger<ModificationTimeService> _logger;
    private readonly ModificationTimeConfiguration _config;
    private readonly ConcurrentDictionary<string, TimeSpan> _folderWindows = new();
    private readonly ConcurrentDictionary<string, ModificationTimeStats> _stats = new();

    public ModificationTimeService(
        ILogger<ModificationTimeService> logger,
        ModificationTimeConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new ModificationTimeConfiguration();

        // Initialize from config
        foreach (var kvp in _config.FolderTimeWindows)
        {
            _folderWindows[kvp.Key] = kvp.Value;
        }
    }

    /// <inheritdoc />
    public bool AreTimesEqual(string folderId, DateTime time1, DateTime time2)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        var stats = GetOrCreateStats(folderId);
        stats.ComparisonsCount++;

        // Normalize times first
        var normalized1 = NormalizeTime(folderId, time1);
        var normalized2 = NormalizeTime(folderId, time2);

        // Check exact match
        if (normalized1 == normalized2)
        {
            stats.ExactMatches++;
            return true;
        }

        // Check within window
        var window = GetTimeWindow(folderId);
        var diff = (normalized1 - normalized2).Duration();

        if (diff <= window)
        {
            stats.MatchesWithinWindow++;
            return true;
        }

        stats.Mismatches++;
        return false;
    }

    /// <inheritdoc />
    public TimeSpan GetTimeWindow(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (_folderWindows.TryGetValue(folderId, out var window))
        {
            return window;
        }

        return _config.FatCompatibilityMode
            ? TimeSpan.FromSeconds(2)
            : _config.DefaultTimeWindow;
    }

    /// <inheritdoc />
    public void SetTimeWindow(string folderId, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (window < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "Time window cannot be negative");
        }

        if (window > _config.MaxTimeWindow)
        {
            throw new ArgumentOutOfRangeException(nameof(window),
                $"Time window cannot exceed {_config.MaxTimeWindow}");
        }

        _folderWindows[folderId] = window;
        _config.FolderTimeWindows[folderId] = window;

        _logger.LogInformation("Set modification time window for folder {FolderId}: {Window}",
            folderId, window);
    }

    /// <inheritdoc />
    public void ResetTimeWindow(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        _folderWindows.TryRemove(folderId, out _);
        _config.FolderTimeWindows.TryRemove(folderId, out _);

        _logger.LogInformation("Reset modification time window for folder {FolderId} to default", folderId);
    }

    /// <inheritdoc />
    public DateTime NormalizeTime(string folderId, DateTime time)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        var precision = _config.FatCompatibilityMode
            ? TimePrecision.TwoSeconds
            : _config.DefaultPrecision;

        return precision switch
        {
            TimePrecision.Nanosecond => time,
            TimePrecision.Microsecond => new DateTime(time.Ticks / 10 * 10, time.Kind),
            TimePrecision.Millisecond => new DateTime(time.Ticks / TimeSpan.TicksPerMillisecond * TimeSpan.TicksPerMillisecond, time.Kind),
            TimePrecision.Second => new DateTime(time.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, time.Kind),
            TimePrecision.TwoSeconds => new DateTime(time.Ticks / (TimeSpan.TicksPerSecond * 2) * (TimeSpan.TicksPerSecond * 2), time.Kind),
            _ => time
        };
    }

    /// <inheritdoc />
    public bool NeedsSync(string folderId, DateTime localTime, DateTime remoteTime)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        // If times are equal within window, no sync needed
        if (AreTimesEqual(folderId, localTime, remoteTime))
        {
            return false;
        }

        // Remote is newer - needs sync
        return remoteTime > localTime;
    }

    /// <inheritdoc />
    public ModificationTimeStats GetStats(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        var stats = GetOrCreateStats(folderId);

        return new ModificationTimeStats
        {
            FolderId = stats.FolderId,
            TimeWindow = GetTimeWindow(folderId),
            ComparisonsCount = stats.ComparisonsCount,
            MatchesWithinWindow = stats.MatchesWithinWindow,
            ExactMatches = stats.ExactMatches,
            Mismatches = stats.Mismatches
        };
    }

    private ModificationTimeStats GetOrCreateStats(string folderId)
    {
        return _stats.GetOrAdd(folderId, id => new ModificationTimeStats
        {
            FolderId = id,
            TimeWindow = GetTimeWindow(id)
        });
    }
}
