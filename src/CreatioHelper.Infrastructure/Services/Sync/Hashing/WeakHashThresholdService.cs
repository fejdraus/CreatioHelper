using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Hashing;

/// <summary>
/// Service for managing weak hash threshold configuration.
/// Weak hashing (rolling checksum) is faster but less secure.
/// Based on Syncthing's WeakHashThresholdPct configuration.
/// </summary>
public interface IWeakHashThresholdService
{
    /// <summary>
    /// Check if weak hash should be used for a file of given size.
    /// </summary>
    /// <param name="fileSize">Size of the file in bytes</param>
    /// <param name="thresholdPercent">Threshold percentage (0-100, where 0 = never, 100 = always)</param>
    /// <returns>True if weak hash should be used</returns>
    bool ShouldUseWeakHash(long fileSize, int thresholdPercent);

    /// <summary>
    /// Get the recommended threshold for a folder based on its characteristics.
    /// </summary>
    int GetRecommendedThreshold(FolderCharacteristics characteristics);

    /// <summary>
    /// Calculate the block size that should use weak hashing.
    /// </summary>
    int CalculateWeakHashBlockSize(long fileSize, int thresholdPercent);

    /// <summary>
    /// Get statistics about weak hash usage.
    /// </summary>
    WeakHashStats GetStats();

    /// <summary>
    /// Record weak hash usage for statistics.
    /// </summary>
    void RecordUsage(bool usedWeakHash, long fileSize, TimeSpan hashDuration);
}

/// <summary>
/// Characteristics of a folder that affect weak hash decisions.
/// </summary>
public class FolderCharacteristics
{
    /// <summary>
    /// Average file size in the folder.
    /// </summary>
    public long AverageFileSize { get; init; }

    /// <summary>
    /// Total number of files.
    /// </summary>
    public long FileCount { get; init; }

    /// <summary>
    /// Whether files change frequently.
    /// </summary>
    public bool HighChangeRate { get; init; }

    /// <summary>
    /// Whether the folder is on a slow storage device.
    /// </summary>
    public bool SlowStorage { get; init; }

    /// <summary>
    /// Whether the connection is slow.
    /// </summary>
    public bool SlowConnection { get; init; }

    /// <summary>
    /// Whether security is a priority over speed.
    /// </summary>
    public bool SecurityPriority { get; init; }
}

/// <summary>
/// Statistics about weak hash usage.
/// </summary>
public class WeakHashStats
{
    public long TotalFiles { get; set; }
    public long FilesWithWeakHash { get; set; }
    public long FilesWithStrongHash { get; set; }
    public long TotalBytesHashed { get; set; }
    public TimeSpan TotalHashDuration { get; set; }
    public double WeakHashPercentage => TotalFiles > 0 ? (double)FilesWithWeakHash / TotalFiles * 100 : 0;
    public double AverageHashSpeed => TotalHashDuration.TotalSeconds > 0
        ? TotalBytesHashed / TotalHashDuration.TotalSeconds
        : 0;
}

/// <summary>
/// Implementation of weak hash threshold service.
/// </summary>
public class WeakHashThresholdService : IWeakHashThresholdService
{
    private readonly ILogger<WeakHashThresholdService> _logger;
    private readonly WeakHashStats _stats = new();
    private readonly object _statsLock = new();

    // Syncthing uses these constants
    private const int DefaultBlockSize = 128 * 1024; // 128 KB
    private const int MinBlockSize = 128 * 1024;     // 128 KB minimum
    private const int MaxBlockSize = 16 * 1024 * 1024; // 16 MB maximum
    private const long SmallFileThreshold = 256 * 1024; // 256 KB

    public WeakHashThresholdService(ILogger<WeakHashThresholdService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool ShouldUseWeakHash(long fileSize, int thresholdPercent)
    {
        // Threshold of 0 means never use weak hash
        if (thresholdPercent <= 0)
        {
            return false;
        }

        // Threshold of 100 means always use weak hash
        if (thresholdPercent >= 100)
        {
            return true;
        }

        // For small files, always use strong hash (weak hash overhead not worth it)
        if (fileSize < SmallFileThreshold)
        {
            return false;
        }

        // Calculate based on file size and threshold
        // Larger files benefit more from weak hashing for delta sync
        var fileSizeScore = CalculateFileSizeScore(fileSize);

        // If file size score exceeds the complement of threshold, use weak hash
        // Higher threshold = more likely to use weak hash
        return fileSizeScore <= thresholdPercent;
    }

    /// <inheritdoc />
    public int GetRecommendedThreshold(FolderCharacteristics characteristics)
    {
        ArgumentNullException.ThrowIfNull(characteristics);

        // Base threshold
        int threshold = 25; // Default Syncthing value

        // Adjust based on characteristics
        if (characteristics.SecurityPriority)
        {
            // Lower threshold for security-sensitive folders
            threshold = Math.Max(0, threshold - 15);
        }

        if (characteristics.HighChangeRate)
        {
            // Higher threshold for frequently changing folders (delta sync benefits)
            threshold = Math.Min(100, threshold + 20);
        }

        if (characteristics.SlowStorage)
        {
            // Lower threshold for slow storage (hashing is expensive)
            threshold = Math.Max(0, threshold - 10);
        }

        if (characteristics.SlowConnection)
        {
            // Higher threshold for slow connections (transfer savings more valuable)
            threshold = Math.Min(100, threshold + 15);
        }

        if (characteristics.AverageFileSize > 10 * 1024 * 1024) // > 10 MB average
        {
            // Higher threshold for large files
            threshold = Math.Min(100, threshold + 10);
        }

        _logger.LogDebug(
            "Recommended weak hash threshold: {Threshold}% (Security: {Security}, HighChange: {HighChange}, SlowStorage: {SlowStorage})",
            threshold, characteristics.SecurityPriority, characteristics.HighChangeRate, characteristics.SlowStorage);

        return threshold;
    }

    /// <inheritdoc />
    public int CalculateWeakHashBlockSize(long fileSize, int thresholdPercent)
    {
        if (fileSize <= 0)
        {
            return DefaultBlockSize;
        }

        // Syncthing's block size calculation
        // Larger files get larger blocks (up to 16 MB)
        var blockSize = DefaultBlockSize;

        if (fileSize > 256 * 1024 * 1024) // > 256 MB
        {
            blockSize = 1 * 1024 * 1024; // 1 MB
        }
        if (fileSize > 512 * 1024 * 1024) // > 512 MB
        {
            blockSize = 2 * 1024 * 1024; // 2 MB
        }
        if (fileSize > 1024 * 1024 * 1024) // > 1 GB
        {
            blockSize = 4 * 1024 * 1024; // 4 MB
        }
        if (fileSize > 2L * 1024 * 1024 * 1024) // > 2 GB
        {
            blockSize = 8 * 1024 * 1024; // 8 MB
        }
        if (fileSize > 4L * 1024 * 1024 * 1024) // > 4 GB
        {
            blockSize = 16 * 1024 * 1024; // 16 MB
        }

        // Adjust based on threshold - lower threshold means smaller blocks for better granularity
        if (thresholdPercent < 25)
        {
            blockSize = Math.Max(MinBlockSize, blockSize / 2);
        }

        return Math.Clamp(blockSize, MinBlockSize, MaxBlockSize);
    }

    /// <inheritdoc />
    public WeakHashStats GetStats()
    {
        lock (_statsLock)
        {
            return new WeakHashStats
            {
                TotalFiles = _stats.TotalFiles,
                FilesWithWeakHash = _stats.FilesWithWeakHash,
                FilesWithStrongHash = _stats.FilesWithStrongHash,
                TotalBytesHashed = _stats.TotalBytesHashed,
                TotalHashDuration = _stats.TotalHashDuration
            };
        }
    }

    /// <inheritdoc />
    public void RecordUsage(bool usedWeakHash, long fileSize, TimeSpan hashDuration)
    {
        lock (_statsLock)
        {
            _stats.TotalFiles++;
            _stats.TotalBytesHashed += fileSize;
            _stats.TotalHashDuration += hashDuration;

            if (usedWeakHash)
            {
                _stats.FilesWithWeakHash++;
            }
            else
            {
                _stats.FilesWithStrongHash++;
            }
        }
    }

    private static int CalculateFileSizeScore(long fileSize)
    {
        // Score from 0-100 based on file size
        // Smaller files get higher scores (less likely to use weak hash)
        // Larger files get lower scores (more likely to use weak hash)

        if (fileSize < 1 * 1024 * 1024) // < 1 MB
            return 90;
        if (fileSize < 10 * 1024 * 1024) // < 10 MB
            return 70;
        if (fileSize < 100 * 1024 * 1024) // < 100 MB
            return 50;
        if (fileSize < 1024 * 1024 * 1024) // < 1 GB
            return 30;

        return 10; // Very large files
    }
}

/// <summary>
/// Configuration for weak hash behavior.
/// </summary>
public class WeakHashConfiguration
{
    /// <summary>
    /// Default threshold percentage (0-100).
    /// 0 = never use weak hash, 100 = always use weak hash.
    /// Default is 25 (Syncthing default).
    /// </summary>
    public int DefaultThresholdPercent { get; set; } = 25;

    /// <summary>
    /// Minimum file size to consider weak hashing (bytes).
    /// Files smaller than this always use strong hash.
    /// </summary>
    public long MinFileSizeForWeakHash { get; set; } = 256 * 1024; // 256 KB

    /// <summary>
    /// Enable automatic threshold adjustment based on folder characteristics.
    /// </summary>
    public bool AutoAdjustThreshold { get; set; } = true;

    /// <summary>
    /// Per-folder threshold overrides.
    /// </summary>
    public ConcurrentDictionary<string, int> FolderThresholds { get; } = new();
}

/// <summary>
/// Extension methods for weak hash threshold.
/// </summary>
public static class WeakHashThresholdExtensions
{
    /// <summary>
    /// Get the effective threshold for a folder, considering overrides.
    /// </summary>
    public static int GetEffectiveThreshold(
        this WeakHashConfiguration config,
        string folderId,
        int? folderConfiguredThreshold = null)
    {
        // Check per-folder override first
        if (config.FolderThresholds.TryGetValue(folderId, out var folderOverride))
        {
            return folderOverride;
        }

        // Then use folder's configured threshold if present
        if (folderConfiguredThreshold.HasValue)
        {
            return folderConfiguredThreshold.Value;
        }

        // Finally use default
        return config.DefaultThresholdPercent;
    }
}
