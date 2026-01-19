using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// Service for managing ignore deletes configuration.
/// Based on Syncthing's ignoreDelete folder option.
/// </summary>
public interface IIgnoreDeletesService
{
    /// <summary>
    /// Check if deletes should be ignored for a folder.
    /// </summary>
    bool ShouldIgnoreDeletes(string folderId);

    /// <summary>
    /// Check if deletes from a specific device should be ignored.
    /// </summary>
    bool ShouldIgnoreDeletesFrom(string folderId, string deviceId);

    /// <summary>
    /// Set ignore deletes for a folder.
    /// </summary>
    void SetIgnoreDeletes(string folderId, bool ignore);

    /// <summary>
    /// Set ignore deletes from a specific device.
    /// </summary>
    void SetIgnoreDeletesFromDevice(string folderId, string deviceId, bool ignore);

    /// <summary>
    /// Get folders with ignore deletes enabled.
    /// </summary>
    IReadOnlyList<string> GetFoldersWithIgnoreDeletes();

    /// <summary>
    /// Check if a delete operation should be applied.
    /// </summary>
    DeleteDecision ShouldApplyDelete(string folderId, string deviceId, string filePath);

    /// <summary>
    /// Record a skipped delete for statistics.
    /// </summary>
    void RecordSkippedDelete(string folderId, string deviceId, string filePath);

    /// <summary>
    /// Get statistics for ignored deletes.
    /// </summary>
    IgnoreDeletesStats GetStats(string folderId);

    /// <summary>
    /// Clear all skipped delete records for a folder.
    /// </summary>
    void ClearSkippedDeletes(string folderId);
}

/// <summary>
/// Decision about whether to apply a delete.
/// </summary>
public enum DeleteDecision
{
    /// <summary>
    /// Apply the delete normally.
    /// </summary>
    Apply,

    /// <summary>
    /// Ignore the delete (folder-level setting).
    /// </summary>
    IgnoreFolder,

    /// <summary>
    /// Ignore the delete (device-specific setting).
    /// </summary>
    IgnoreDevice,

    /// <summary>
    /// Ignore the delete (path pattern match).
    /// </summary>
    IgnorePattern
}

/// <summary>
/// Configuration for ignore deletes service.
/// </summary>
public class IgnoreDeletesConfiguration
{
    /// <summary>
    /// Default setting for ignore deletes.
    /// </summary>
    public bool DefaultIgnoreDeletes { get; set; } = false;

    /// <summary>
    /// Per-folder ignore deletes settings.
    /// </summary>
    public ConcurrentDictionary<string, bool> FolderSettings { get; } = new();

    /// <summary>
    /// Per-device ignore deletes settings (key: folderId:deviceId).
    /// </summary>
    public ConcurrentDictionary<string, bool> DeviceSettings { get; } = new();

    /// <summary>
    /// Path patterns to always ignore deletes for.
    /// </summary>
    public List<string> IgnoreDeletePatterns { get; } = new();

    /// <summary>
    /// Whether to log skipped deletes.
    /// </summary>
    public bool LogSkippedDeletes { get; set; } = true;

    /// <summary>
    /// Maximum number of skipped deletes to track per folder.
    /// </summary>
    public int MaxSkippedDeletesTracked { get; set; } = 1000;
}

/// <summary>
/// Information about a skipped delete.
/// </summary>
public class SkippedDelete
{
    public string FilePath { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public DateTime SkippedAt { get; init; } = DateTime.UtcNow;
    public DeleteDecision Reason { get; init; }
}

/// <summary>
/// Statistics for ignored deletes.
/// </summary>
public class IgnoreDeletesStats
{
    public string FolderId { get; set; } = string.Empty;
    public bool IgnoreDeletesEnabled { get; set; }
    public long TotalDeletesReceived { get; set; }
    public long DeletesApplied { get; set; }
    public long DeletesSkipped { get; set; }
    public IReadOnlyList<SkippedDelete> RecentSkippedDeletes { get; set; } = Array.Empty<SkippedDelete>();
    public double SkipRate => TotalDeletesReceived > 0
        ? (double)DeletesSkipped / TotalDeletesReceived * 100.0
        : 0.0;
}

/// <summary>
/// Implementation of ignore deletes service.
/// </summary>
public class IgnoreDeletesService : IIgnoreDeletesService
{
    private readonly ILogger<IgnoreDeletesService> _logger;
    private readonly IgnoreDeletesConfiguration _config;
    private readonly ConcurrentDictionary<string, bool> _folderSettings = new();
    private readonly ConcurrentDictionary<string, bool> _deviceSettings = new();
    private readonly ConcurrentDictionary<string, List<SkippedDelete>> _skippedDeletes = new();
    private readonly ConcurrentDictionary<string, IgnoreDeletesStats> _stats = new();

    public IgnoreDeletesService(
        ILogger<IgnoreDeletesService> logger,
        IgnoreDeletesConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new IgnoreDeletesConfiguration();

        // Initialize from config
        foreach (var kvp in _config.FolderSettings)
        {
            _folderSettings[kvp.Key] = kvp.Value;
        }

        foreach (var kvp in _config.DeviceSettings)
        {
            _deviceSettings[kvp.Key] = kvp.Value;
        }
    }

    /// <inheritdoc />
    public bool ShouldIgnoreDeletes(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (_folderSettings.TryGetValue(folderId, out var setting))
        {
            return setting;
        }

        return _config.DefaultIgnoreDeletes;
    }

    /// <inheritdoc />
    public bool ShouldIgnoreDeletesFrom(string folderId, string deviceId)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(deviceId);

        var key = GetDeviceKey(folderId, deviceId);

        if (_deviceSettings.TryGetValue(key, out var setting))
        {
            return setting;
        }

        // Fall back to folder setting
        return ShouldIgnoreDeletes(folderId);
    }

    /// <inheritdoc />
    public void SetIgnoreDeletes(string folderId, bool ignore)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        _folderSettings[folderId] = ignore;
        _config.FolderSettings[folderId] = ignore;

        _logger.LogInformation("Set ignore deletes for folder {FolderId}: {Ignore}",
            folderId, ignore);
    }

    /// <inheritdoc />
    public void SetIgnoreDeletesFromDevice(string folderId, string deviceId, bool ignore)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(deviceId);

        var key = GetDeviceKey(folderId, deviceId);
        _deviceSettings[key] = ignore;
        _config.DeviceSettings[key] = ignore;

        _logger.LogInformation("Set ignore deletes for folder {FolderId} from device {DeviceId}: {Ignore}",
            folderId, deviceId, ignore);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetFoldersWithIgnoreDeletes()
    {
        return _folderSettings
            .Where(kvp => kvp.Value)
            .Select(kvp => kvp.Key)
            .ToList();
    }

    /// <inheritdoc />
    public DeleteDecision ShouldApplyDelete(string folderId, string deviceId, string filePath)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(filePath);

        var stats = GetOrCreateStats(folderId);
        stats.TotalDeletesReceived++;

        // Check path patterns first
        if (MatchesIgnorePattern(filePath))
        {
            return DeleteDecision.IgnorePattern;
        }

        // Check device-specific setting
        var deviceKey = GetDeviceKey(folderId, deviceId);
        if (_deviceSettings.TryGetValue(deviceKey, out var deviceIgnore) && deviceIgnore)
        {
            return DeleteDecision.IgnoreDevice;
        }

        // Check folder-level setting
        if (ShouldIgnoreDeletes(folderId))
        {
            return DeleteDecision.IgnoreFolder;
        }

        stats.DeletesApplied++;
        return DeleteDecision.Apply;
    }

    /// <inheritdoc />
    public void RecordSkippedDelete(string folderId, string deviceId, string filePath)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(filePath);

        var stats = GetOrCreateStats(folderId);
        stats.DeletesSkipped++;

        var decision = ShouldApplyDelete(folderId, deviceId, filePath);
        if (decision == DeleteDecision.Apply)
        {
            decision = DeleteDecision.IgnoreFolder; // Default reason
        }

        var skipped = new SkippedDelete
        {
            FilePath = filePath,
            DeviceId = deviceId,
            SkippedAt = DateTime.UtcNow,
            Reason = decision
        };

        var list = _skippedDeletes.GetOrAdd(folderId, _ => new List<SkippedDelete>());
        lock (list)
        {
            list.Add(skipped);

            // Trim if exceeds max
            while (list.Count > _config.MaxSkippedDeletesTracked)
            {
                list.RemoveAt(0);
            }
        }

        if (_config.LogSkippedDeletes)
        {
            _logger.LogDebug("Skipped delete for {FilePath} from {DeviceId} in folder {FolderId}: {Reason}",
                filePath, deviceId, folderId, decision);
        }
    }

    /// <inheritdoc />
    public IgnoreDeletesStats GetStats(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        var stats = GetOrCreateStats(folderId);

        IReadOnlyList<SkippedDelete> recentSkipped = Array.Empty<SkippedDelete>();
        if (_skippedDeletes.TryGetValue(folderId, out var list))
        {
            lock (list)
            {
                recentSkipped = list.TakeLast(100).ToList();
            }
        }

        return new IgnoreDeletesStats
        {
            FolderId = stats.FolderId,
            IgnoreDeletesEnabled = ShouldIgnoreDeletes(folderId),
            TotalDeletesReceived = stats.TotalDeletesReceived,
            DeletesApplied = stats.DeletesApplied,
            DeletesSkipped = stats.DeletesSkipped,
            RecentSkippedDeletes = recentSkipped
        };
    }

    /// <inheritdoc />
    public void ClearSkippedDeletes(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (_skippedDeletes.TryGetValue(folderId, out var list))
        {
            lock (list)
            {
                list.Clear();
            }
        }

        _logger.LogInformation("Cleared skipped deletes for folder {FolderId}", folderId);
    }

    private static string GetDeviceKey(string folderId, string deviceId) => $"{folderId}:{deviceId}";

    private bool MatchesIgnorePattern(string filePath)
    {
        foreach (var pattern in _config.IgnoreDeletePatterns)
        {
            if (MatchesPattern(filePath, pattern))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesPattern(string path, string pattern)
    {
        // Simple glob-like matching
        if (pattern.StartsWith("*"))
        {
            return path.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.EndsWith("*"))
        {
            return path.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        }

        if (pattern.Contains('*'))
        {
            var parts = pattern.Split('*');
            var index = 0;
            foreach (var part in parts)
            {
                var found = path.IndexOf(part, index, StringComparison.OrdinalIgnoreCase);
                if (found < 0) return false;
                index = found + part.Length;
            }
            return true;
        }

        return path.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private IgnoreDeletesStats GetOrCreateStats(string folderId)
    {
        return _stats.GetOrAdd(folderId, id => new IgnoreDeletesStats
        {
            FolderId = id,
            IgnoreDeletesEnabled = ShouldIgnoreDeletes(id)
        });
    }
}
