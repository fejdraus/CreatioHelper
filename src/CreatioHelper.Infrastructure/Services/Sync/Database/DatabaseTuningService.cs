using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Database;

/// <summary>
/// Service for database performance tuning.
/// Based on Syncthing's database tuning options.
/// </summary>
public interface IDatabaseTuningService
{
    /// <summary>
    /// Get current database tuning settings.
    /// </summary>
    DatabaseTuningSettings GetSettings();

    /// <summary>
    /// Apply new tuning settings.
    /// </summary>
    void ApplySettings(DatabaseTuningSettings settings);

    /// <summary>
    /// Get recommended settings based on system resources.
    /// </summary>
    DatabaseTuningSettings GetRecommendedSettings();

    /// <summary>
    /// Get database statistics.
    /// </summary>
    DatabaseStats GetStats();

    /// <summary>
    /// Trigger database compaction.
    /// </summary>
    void TriggerCompaction();

    /// <summary>
    /// Check if maintenance is needed.
    /// </summary>
    bool IsMaintenanceNeeded();

    /// <summary>
    /// Reset to default settings.
    /// </summary>
    void ResetToDefaults();
}

/// <summary>
/// Database tuning settings.
/// </summary>
public class DatabaseTuningSettings
{
    /// <summary>
    /// Maximum size of the block cache in bytes.
    /// Higher values improve read performance but use more memory.
    /// Default: 8 MB (Syncthing default is based on memory)
    /// </summary>
    public long MaxBlockCacheSize { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// Maximum size of the write buffer in bytes.
    /// Larger buffers reduce disk writes but increase memory usage.
    /// Default: 4 MB
    /// </summary>
    public long WriteBufferSize { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Maximum number of open files for the database.
    /// Higher values can improve concurrent access.
    /// Default: 100
    /// </summary>
    public int MaxOpenFiles { get; set; } = 100;

    /// <summary>
    /// Enable bloom filters to speed up reads.
    /// Default: true
    /// </summary>
    public bool EnableBloomFilters { get; set; } = true;

    /// <summary>
    /// Bloom filter bits per key.
    /// Higher values reduce false positives but use more memory.
    /// Default: 10
    /// </summary>
    public int BloomFilterBitsPerKey { get; set; } = 10;

    /// <summary>
    /// Enable compression for database blocks.
    /// Default: true
    /// </summary>
    public bool EnableCompression { get; set; } = true;

    /// <summary>
    /// Compression type (snappy, zstd, none).
    /// Default: snappy
    /// </summary>
    public string CompressionType { get; set; } = "snappy";

    /// <summary>
    /// Sync writes to disk (safer but slower).
    /// Default: true
    /// </summary>
    public bool SyncWrites { get; set; } = true;

    /// <summary>
    /// Enable write-ahead logging for crash recovery.
    /// Default: true
    /// </summary>
    public bool EnableWAL { get; set; } = true;

    /// <summary>
    /// Maximum size of write-ahead log before compaction.
    /// Default: 64 MB
    /// </summary>
    public long MaxWALSize { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    /// Background compaction enabled.
    /// Default: true
    /// </summary>
    public bool EnableBackgroundCompaction { get; set; } = true;

    /// <summary>
    /// Number of background compaction threads.
    /// Default: 2
    /// </summary>
    public int CompactionThreads { get; set; } = 2;

    /// <summary>
    /// Target file size for level-1 (bytes).
    /// Default: 64 MB
    /// </summary>
    public long TargetFileSizeBase { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    /// Ratio of database size to trigger compaction.
    /// Default: 2.0 (compact when size doubles)
    /// </summary>
    public double CompactionTriggerRatio { get; set; } = 2.0;
}

/// <summary>
/// Database statistics.
/// </summary>
public class DatabaseStats
{
    public long TotalSize { get; set; }
    public long BlockCacheHits { get; set; }
    public long BlockCacheMisses { get; set; }
    public long WritesCount { get; set; }
    public long ReadsCount { get; set; }
    public long CompactionsCount { get; set; }
    public DateTime? LastCompactionTime { get; set; }
    public long WalSize { get; set; }
    public int OpenFilesCount { get; set; }
    public double CacheHitRate => BlockCacheHits + BlockCacheMisses > 0
        ? (double)BlockCacheHits / (BlockCacheHits + BlockCacheMisses) * 100.0
        : 0.0;
}

/// <summary>
/// Preset configurations for different use cases.
/// </summary>
public enum DatabasePreset
{
    /// <summary>
    /// Default balanced settings.
    /// </summary>
    Default,

    /// <summary>
    /// Low memory usage (for constrained systems).
    /// </summary>
    LowMemory,

    /// <summary>
    /// High performance (uses more memory).
    /// </summary>
    HighPerformance,

    /// <summary>
    /// High durability (slower writes, safer).
    /// </summary>
    HighDurability,

    /// <summary>
    /// SSD optimized settings.
    /// </summary>
    SsdOptimized
}

/// <summary>
/// Implementation of database tuning service.
/// </summary>
public class DatabaseTuningService : IDatabaseTuningService
{
    private readonly ILogger<DatabaseTuningService> _logger;
    private DatabaseTuningSettings _settings;
    private readonly DatabaseStats _stats = new();

    public DatabaseTuningService(
        ILogger<DatabaseTuningService> logger,
        DatabaseTuningSettings? initialSettings = null)
    {
        _logger = logger;
        _settings = initialSettings ?? new DatabaseTuningSettings();
    }

    /// <inheritdoc />
    public DatabaseTuningSettings GetSettings()
    {
        return new DatabaseTuningSettings
        {
            MaxBlockCacheSize = _settings.MaxBlockCacheSize,
            WriteBufferSize = _settings.WriteBufferSize,
            MaxOpenFiles = _settings.MaxOpenFiles,
            EnableBloomFilters = _settings.EnableBloomFilters,
            BloomFilterBitsPerKey = _settings.BloomFilterBitsPerKey,
            EnableCompression = _settings.EnableCompression,
            CompressionType = _settings.CompressionType,
            SyncWrites = _settings.SyncWrites,
            EnableWAL = _settings.EnableWAL,
            MaxWALSize = _settings.MaxWALSize,
            EnableBackgroundCompaction = _settings.EnableBackgroundCompaction,
            CompactionThreads = _settings.CompactionThreads,
            TargetFileSizeBase = _settings.TargetFileSizeBase,
            CompactionTriggerRatio = _settings.CompactionTriggerRatio
        };
    }

    /// <inheritdoc />
    public void ApplySettings(DatabaseTuningSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ValidateSettings(settings);

        _settings = settings;
        _logger.LogInformation("Applied database tuning settings. Cache: {CacheSize}MB, WriteBuffer: {WriteBuffer}MB",
            settings.MaxBlockCacheSize / (1024 * 1024),
            settings.WriteBufferSize / (1024 * 1024));
    }

    /// <inheritdoc />
    public DatabaseTuningSettings GetRecommendedSettings()
    {
        var totalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var settings = new DatabaseTuningSettings();

        if (totalMemory > 8L * 1024 * 1024 * 1024) // > 8 GB
        {
            // High memory system
            settings.MaxBlockCacheSize = 256 * 1024 * 1024; // 256 MB
            settings.WriteBufferSize = 64 * 1024 * 1024;    // 64 MB
            settings.MaxOpenFiles = 500;
            settings.CompactionThreads = 4;
        }
        else if (totalMemory > 4L * 1024 * 1024 * 1024) // > 4 GB
        {
            // Medium memory system
            settings.MaxBlockCacheSize = 64 * 1024 * 1024;  // 64 MB
            settings.WriteBufferSize = 16 * 1024 * 1024;    // 16 MB
            settings.MaxOpenFiles = 200;
            settings.CompactionThreads = 2;
        }
        else
        {
            // Low memory system
            settings.MaxBlockCacheSize = 8 * 1024 * 1024;   // 8 MB
            settings.WriteBufferSize = 4 * 1024 * 1024;     // 4 MB
            settings.MaxOpenFiles = 50;
            settings.CompactionThreads = 1;
        }

        return settings;
    }

    /// <inheritdoc />
    public DatabaseStats GetStats()
    {
        return new DatabaseStats
        {
            TotalSize = _stats.TotalSize,
            BlockCacheHits = _stats.BlockCacheHits,
            BlockCacheMisses = _stats.BlockCacheMisses,
            WritesCount = _stats.WritesCount,
            ReadsCount = _stats.ReadsCount,
            CompactionsCount = _stats.CompactionsCount,
            LastCompactionTime = _stats.LastCompactionTime,
            WalSize = _stats.WalSize,
            OpenFilesCount = _stats.OpenFilesCount
        };
    }

    /// <inheritdoc />
    public void TriggerCompaction()
    {
        _logger.LogInformation("Triggering database compaction");
        _stats.CompactionsCount++;
        _stats.LastCompactionTime = DateTime.UtcNow;
        // Actual compaction would be performed by the database implementation
    }

    /// <inheritdoc />
    public bool IsMaintenanceNeeded()
    {
        // Check if WAL size exceeds threshold
        if (_stats.WalSize > _settings.MaxWALSize)
        {
            return true;
        }

        // Check cache hit rate
        if (_stats.CacheHitRate < 50.0 && _stats.ReadsCount > 1000)
        {
            return true;
        }

        // Check time since last compaction
        if (_stats.LastCompactionTime.HasValue &&
            DateTime.UtcNow - _stats.LastCompactionTime.Value > TimeSpan.FromDays(7))
        {
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public void ResetToDefaults()
    {
        _settings = new DatabaseTuningSettings();
        _logger.LogInformation("Reset database tuning to default settings");
    }

    /// <summary>
    /// Apply a preset configuration.
    /// </summary>
    public void ApplyPreset(DatabasePreset preset)
    {
        var settings = preset switch
        {
            DatabasePreset.LowMemory => new DatabaseTuningSettings
            {
                MaxBlockCacheSize = 4 * 1024 * 1024,
                WriteBufferSize = 2 * 1024 * 1024,
                MaxOpenFiles = 50,
                EnableBloomFilters = false,
                CompactionThreads = 1
            },
            DatabasePreset.HighPerformance => new DatabaseTuningSettings
            {
                MaxBlockCacheSize = 512 * 1024 * 1024,
                WriteBufferSize = 128 * 1024 * 1024,
                MaxOpenFiles = 1000,
                EnableBloomFilters = true,
                BloomFilterBitsPerKey = 15,
                CompactionThreads = 4
            },
            DatabasePreset.HighDurability => new DatabaseTuningSettings
            {
                SyncWrites = true,
                EnableWAL = true,
                MaxWALSize = 32 * 1024 * 1024,
                EnableBackgroundCompaction = true
            },
            DatabasePreset.SsdOptimized => new DatabaseTuningSettings
            {
                MaxBlockCacheSize = 128 * 1024 * 1024,
                WriteBufferSize = 64 * 1024 * 1024,
                TargetFileSizeBase = 128 * 1024 * 1024,
                CompactionTriggerRatio = 1.5
            },
            _ => new DatabaseTuningSettings()
        };

        ApplySettings(settings);
        _logger.LogInformation("Applied database preset: {Preset}", preset);
    }

    /// <summary>
    /// Record a cache hit for statistics.
    /// </summary>
    public void RecordCacheHit()
    {
        _stats.BlockCacheHits++;
    }

    /// <summary>
    /// Record a cache miss for statistics.
    /// </summary>
    public void RecordCacheMiss()
    {
        _stats.BlockCacheMisses++;
    }

    /// <summary>
    /// Record a write operation.
    /// </summary>
    public void RecordWrite()
    {
        _stats.WritesCount++;
    }

    /// <summary>
    /// Record a read operation.
    /// </summary>
    public void RecordRead()
    {
        _stats.ReadsCount++;
    }

    /// <summary>
    /// Update database size.
    /// </summary>
    public void UpdateSize(long totalSize, long walSize)
    {
        _stats.TotalSize = totalSize;
        _stats.WalSize = walSize;
    }

    private void ValidateSettings(DatabaseTuningSettings settings)
    {
        if (settings.MaxBlockCacheSize < 0)
            throw new ArgumentException("MaxBlockCacheSize cannot be negative");

        if (settings.WriteBufferSize < 0)
            throw new ArgumentException("WriteBufferSize cannot be negative");

        if (settings.MaxOpenFiles < 10)
            throw new ArgumentException("MaxOpenFiles must be at least 10");

        if (settings.BloomFilterBitsPerKey < 1 || settings.BloomFilterBitsPerKey > 20)
            throw new ArgumentException("BloomFilterBitsPerKey must be between 1 and 20");

        if (settings.CompactionThreads < 1)
            throw new ArgumentException("CompactionThreads must be at least 1");

        if (settings.CompactionTriggerRatio < 1.0)
            throw new ArgumentException("CompactionTriggerRatio must be at least 1.0");
    }
}
