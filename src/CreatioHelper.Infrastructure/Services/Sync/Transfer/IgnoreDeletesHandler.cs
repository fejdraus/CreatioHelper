using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Transfer;

/// <summary>
/// Handles the IgnoreDeletes folder option.
/// When enabled, delete operations from remote devices are not applied locally.
/// Useful for archive folders where you want to keep all files even if deleted on remote.
/// Based on Syncthing's IgnoreDelete configuration.
/// </summary>
public interface IIgnoreDeletesHandler
{
    /// <summary>
    /// Check if a delete operation should be applied for the given folder.
    /// </summary>
    /// <param name="folder">The folder configuration</param>
    /// <param name="filePath">The file path being deleted</param>
    /// <param name="sourceDeviceId">The device that initiated the delete</param>
    /// <returns>True if delete should be applied, false if it should be ignored</returns>
    bool ShouldApplyDelete(SyncFolder folder, string filePath, string sourceDeviceId);

    /// <summary>
    /// Record an ignored delete for auditing/reporting purposes.
    /// </summary>
    Task RecordIgnoredDeleteAsync(SyncFolder folder, string filePath, string sourceDeviceId, CancellationToken ct = default);

    /// <summary>
    /// Get statistics about ignored deletes for a folder.
    /// </summary>
    IgnoredDeleteStats GetStats(string folderId);

    /// <summary>
    /// Clear recorded ignored deletes for a folder.
    /// </summary>
    void ClearStats(string folderId);
}

/// <summary>
/// Statistics about ignored delete operations.
/// </summary>
public class IgnoredDeleteStats
{
    internal long _totalIgnoredDeletes;

    public string FolderId { get; init; } = string.Empty;
    public long TotalIgnoredDeletes { get => Interlocked.Read(ref _totalIgnoredDeletes); set => Interlocked.Exchange(ref _totalIgnoredDeletes, value); }
    public DateTime? FirstIgnoredDelete { get; set; }
    public DateTime? LastIgnoredDelete { get; set; }
    public ConcurrentDictionary<string, int> IgnoredDeletesByDevice { get; } = new();
    public ConcurrentQueue<IgnoredDeleteRecord> RecentDeletes { get; } = new();
}

/// <summary>
/// Record of a single ignored delete operation.
/// </summary>
public record IgnoredDeleteRecord(
    string FilePath,
    string SourceDeviceId,
    DateTime Timestamp);

/// <summary>
/// Implementation of ignore deletes handler.
/// </summary>
public class IgnoreDeletesHandler : IIgnoreDeletesHandler
{
    private readonly ILogger<IgnoreDeletesHandler> _logger;
    private readonly ConcurrentDictionary<string, IgnoredDeleteStats> _stats = new();
    private const int MaxRecentDeletes = 100;

    public IgnoreDeletesHandler(ILogger<IgnoreDeletesHandler> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool ShouldApplyDelete(SyncFolder folder, string filePath, string sourceDeviceId)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(filePath);

        // If IgnoreDelete is not enabled, always apply deletes
        if (!folder.IgnoreDelete)
        {
            return true;
        }

        // Log that we're ignoring this delete
        _logger.LogDebug(
            "Ignoring delete for {FilePath} in folder {FolderId} from device {DeviceId} (IgnoreDelete is enabled)",
            filePath, folder.Id, sourceDeviceId);

        return false;
    }

    /// <inheritdoc />
    public Task RecordIgnoredDeleteAsync(SyncFolder folder, string filePath, string sourceDeviceId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(filePath);

        var stats = _stats.GetOrAdd(folder.Id, id => new IgnoredDeleteStats { FolderId = id });
        var now = DateTime.UtcNow;

        Interlocked.Increment(ref stats._totalIgnoredDeletes);

        if (stats.FirstIgnoredDelete == null)
        {
            stats.FirstIgnoredDelete = now;
        }
        stats.LastIgnoredDelete = now;

        // Track by device
        if (!string.IsNullOrEmpty(sourceDeviceId))
        {
            stats.IgnoredDeletesByDevice.AddOrUpdate(
                sourceDeviceId,
                1,
                (_, count) => count + 1);
        }

        // Keep recent deletes (limited queue)
        var record = new IgnoredDeleteRecord(filePath, sourceDeviceId ?? "unknown", now);
        stats.RecentDeletes.Enqueue(record);

        // Trim queue if needed
        while (stats.RecentDeletes.Count > MaxRecentDeletes)
        {
            stats.RecentDeletes.TryDequeue(out _);
        }

        _logger.LogInformation(
            "Recorded ignored delete: {FilePath} from device {DeviceId} in folder {FolderId}. Total ignored: {Total}",
            filePath, sourceDeviceId, folder.Id, stats.TotalIgnoredDeletes);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public IgnoredDeleteStats GetStats(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        return _stats.TryGetValue(folderId, out var stats)
            ? stats
            : new IgnoredDeleteStats { FolderId = folderId };
    }

    /// <inheritdoc />
    public void ClearStats(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        _stats.TryRemove(folderId, out _);
        _logger.LogDebug("Cleared ignored delete stats for folder {FolderId}", folderId);
    }
}

/// <summary>
/// Configuration for ignore deletes behavior.
/// </summary>
public class IgnoreDeletesConfiguration
{
    /// <summary>
    /// Enable logging of ignored deletes.
    /// </summary>
    public bool EnableLogging { get; set; } = true;

    /// <summary>
    /// Maximum number of recent delete records to keep per folder.
    /// </summary>
    public int MaxRecentRecords { get; set; } = 100;

    /// <summary>
    /// Patterns to always delete even when IgnoreDelete is enabled.
    /// Useful for temporary files that should always be cleaned up.
    /// </summary>
    public List<string> AlwaysDeletePatterns { get; set; } = new()
    {
        "*.tmp",
        "*.temp",
        "~$*",
        ".~lock.*"
    };
}

/// <summary>
/// Extended handler with pattern matching support.
/// </summary>
public class PatternAwareIgnoreDeletesHandler : IIgnoreDeletesHandler
{
    private readonly IgnoreDeletesHandler _baseHandler;
    private readonly IgnoreDeletesConfiguration _config;
    private readonly ILogger<PatternAwareIgnoreDeletesHandler> _logger;

    public PatternAwareIgnoreDeletesHandler(
        IgnoreDeletesHandler baseHandler,
        IgnoreDeletesConfiguration config,
        ILogger<PatternAwareIgnoreDeletesHandler> logger)
    {
        _baseHandler = baseHandler;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool ShouldApplyDelete(SyncFolder folder, string filePath, string sourceDeviceId)
    {
        // Check if file matches AlwaysDelete patterns
        if (MatchesAlwaysDeletePattern(filePath))
        {
            _logger.LogDebug(
                "File {FilePath} matches AlwaysDelete pattern, allowing delete even though IgnoreDelete is enabled",
                filePath);
            return true;
        }

        return _baseHandler.ShouldApplyDelete(folder, filePath, sourceDeviceId);
    }

    /// <inheritdoc />
    public Task RecordIgnoredDeleteAsync(SyncFolder folder, string filePath, string sourceDeviceId, CancellationToken ct = default)
    {
        return _baseHandler.RecordIgnoredDeleteAsync(folder, filePath, sourceDeviceId, ct);
    }

    /// <inheritdoc />
    public IgnoredDeleteStats GetStats(string folderId)
    {
        return _baseHandler.GetStats(folderId);
    }

    /// <inheritdoc />
    public void ClearStats(string folderId)
    {
        _baseHandler.ClearStats(folderId);
    }

    private bool MatchesAlwaysDeletePattern(string filePath)
    {
        var fileName = Path.GetFileName(filePath);

        foreach (var pattern in _config.AlwaysDeletePatterns)
        {
            if (MatchesPattern(fileName, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesPattern(string fileName, string pattern)
    {
        // Simple wildcard matching
        if (pattern.StartsWith('*') && pattern.EndsWith('*'))
        {
            var middle = pattern[1..^1];
            return fileName.Contains(middle, StringComparison.OrdinalIgnoreCase);
        }
        if (pattern.StartsWith('*'))
        {
            var suffix = pattern[1..];
            return fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }
        if (pattern.EndsWith('*'))
        {
            var prefix = pattern[..^1];
            return fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        return fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
