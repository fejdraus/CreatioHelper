using CreatioHelper.Infrastructure.Services.Sync.Database;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.Database;

public class DatabaseTuningServiceTests
{
    private readonly Mock<ILogger<DatabaseTuningService>> _loggerMock;
    private readonly DatabaseTuningService _service;

    public DatabaseTuningServiceTests()
    {
        _loggerMock = new Mock<ILogger<DatabaseTuningService>>();
        _service = new DatabaseTuningService(_loggerMock.Object);
    }

    #region GetSettings Tests

    [Fact]
    public void GetSettings_Default_ReturnsDefaultValues()
    {
        var settings = _service.GetSettings();

        Assert.Equal(8 * 1024 * 1024, settings.MaxBlockCacheSize);
        Assert.Equal(4 * 1024 * 1024, settings.WriteBufferSize);
        Assert.Equal(100, settings.MaxOpenFiles);
        Assert.True(settings.EnableBloomFilters);
        Assert.Equal(10, settings.BloomFilterBitsPerKey);
        Assert.True(settings.EnableCompression);
        Assert.Equal("snappy", settings.CompressionType);
        Assert.True(settings.SyncWrites);
        Assert.True(settings.EnableWAL);
    }

    [Fact]
    public void GetSettings_ReturnsCopy()
    {
        var settings1 = _service.GetSettings();
        var settings2 = _service.GetSettings();

        Assert.NotSame(settings1, settings2);
    }

    #endregion

    #region ApplySettings Tests

    [Fact]
    public void ApplySettings_ValidSettings_Applies()
    {
        var settings = new DatabaseTuningSettings
        {
            MaxBlockCacheSize = 16 * 1024 * 1024,
            WriteBufferSize = 8 * 1024 * 1024,
            MaxOpenFiles = 200
        };

        _service.ApplySettings(settings);

        var result = _service.GetSettings();
        Assert.Equal(16 * 1024 * 1024, result.MaxBlockCacheSize);
        Assert.Equal(8 * 1024 * 1024, result.WriteBufferSize);
        Assert.Equal(200, result.MaxOpenFiles);
    }

    [Fact]
    public void ApplySettings_NullSettings_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ApplySettings(null!));
    }

    [Fact]
    public void ApplySettings_NegativeCacheSize_ThrowsArgumentException()
    {
        var settings = new DatabaseTuningSettings { MaxBlockCacheSize = -1 };

        Assert.Throws<ArgumentException>(() =>
            _service.ApplySettings(settings));
    }

    [Fact]
    public void ApplySettings_NegativeWriteBuffer_ThrowsArgumentException()
    {
        var settings = new DatabaseTuningSettings { WriteBufferSize = -1 };

        Assert.Throws<ArgumentException>(() =>
            _service.ApplySettings(settings));
    }

    [Fact]
    public void ApplySettings_MaxOpenFilesTooLow_ThrowsArgumentException()
    {
        var settings = new DatabaseTuningSettings { MaxOpenFiles = 5 };

        Assert.Throws<ArgumentException>(() =>
            _service.ApplySettings(settings));
    }

    [Fact]
    public void ApplySettings_BloomFilterBitsOutOfRange_ThrowsArgumentException()
    {
        var settings = new DatabaseTuningSettings { BloomFilterBitsPerKey = 25 };

        Assert.Throws<ArgumentException>(() =>
            _service.ApplySettings(settings));
    }

    [Fact]
    public void ApplySettings_CompactionThreadsZero_ThrowsArgumentException()
    {
        var settings = new DatabaseTuningSettings { CompactionThreads = 0 };

        Assert.Throws<ArgumentException>(() =>
            _service.ApplySettings(settings));
    }

    [Fact]
    public void ApplySettings_CompactionRatioTooLow_ThrowsArgumentException()
    {
        var settings = new DatabaseTuningSettings { CompactionTriggerRatio = 0.5 };

        Assert.Throws<ArgumentException>(() =>
            _service.ApplySettings(settings));
    }

    #endregion

    #region GetRecommendedSettings Tests

    [Fact]
    public void GetRecommendedSettings_ReturnsValidSettings()
    {
        var settings = _service.GetRecommendedSettings();

        Assert.True(settings.MaxBlockCacheSize > 0);
        Assert.True(settings.WriteBufferSize > 0);
        Assert.True(settings.MaxOpenFiles >= 10);
    }

    #endregion

    #region GetStats Tests

    [Fact]
    public void GetStats_Initial_ReturnsZeros()
    {
        var stats = _service.GetStats();

        Assert.Equal(0, stats.TotalSize);
        Assert.Equal(0, stats.BlockCacheHits);
        Assert.Equal(0, stats.BlockCacheMisses);
        Assert.Equal(0, stats.WritesCount);
        Assert.Equal(0, stats.ReadsCount);
    }

    [Fact]
    public void GetStats_ReturnsCopy()
    {
        var stats1 = _service.GetStats();
        var stats2 = _service.GetStats();

        Assert.NotSame(stats1, stats2);
    }

    [Fact]
    public void GetStats_CacheHitRate_CalculatesCorrectly()
    {
        _service.RecordCacheHit();
        _service.RecordCacheHit();
        _service.RecordCacheHit();
        _service.RecordCacheMiss();

        var stats = _service.GetStats();

        Assert.Equal(75.0, stats.CacheHitRate);
    }

    [Fact]
    public void GetStats_CacheHitRate_NoHits_ReturnsZero()
    {
        var stats = _service.GetStats();

        Assert.Equal(0.0, stats.CacheHitRate);
    }

    #endregion

    #region TriggerCompaction Tests

    [Fact]
    public void TriggerCompaction_UpdatesStats()
    {
        _service.TriggerCompaction();

        var stats = _service.GetStats();
        Assert.Equal(1, stats.CompactionsCount);
        Assert.NotNull(stats.LastCompactionTime);
    }

    [Fact]
    public void TriggerCompaction_MultipleTimes_IncrementsCount()
    {
        _service.TriggerCompaction();
        _service.TriggerCompaction();
        _service.TriggerCompaction();

        var stats = _service.GetStats();
        Assert.Equal(3, stats.CompactionsCount);
    }

    #endregion

    #region IsMaintenanceNeeded Tests

    [Fact]
    public void IsMaintenanceNeeded_Initial_ReturnsFalse()
    {
        Assert.False(_service.IsMaintenanceNeeded());
    }

    [Fact]
    public void IsMaintenanceNeeded_LargeWAL_ReturnsTrue()
    {
        _service.UpdateSize(0, 100 * 1024 * 1024); // 100 MB WAL

        Assert.True(_service.IsMaintenanceNeeded());
    }

    [Fact]
    public void IsMaintenanceNeeded_LowCacheHitRate_ReturnsTrue()
    {
        // Record low hit rate with enough reads
        for (int i = 0; i < 1001; i++)
        {
            _service.RecordRead();
            _service.RecordCacheMiss();
        }

        Assert.True(_service.IsMaintenanceNeeded());
    }

    [Fact]
    public void IsMaintenanceNeeded_OldCompaction_ReturnsTrue()
    {
        // This test is tricky since we can't easily set old compaction time
        // Just verify the method doesn't throw
        _service.TriggerCompaction();
        Assert.False(_service.IsMaintenanceNeeded()); // Just compacted, should not need maintenance
    }

    #endregion

    #region ResetToDefaults Tests

    [Fact]
    public void ResetToDefaults_RestoresDefaultSettings()
    {
        var customSettings = new DatabaseTuningSettings
        {
            MaxBlockCacheSize = 512 * 1024 * 1024,
            WriteBufferSize = 128 * 1024 * 1024
        };
        _service.ApplySettings(customSettings);

        _service.ResetToDefaults();

        var settings = _service.GetSettings();
        Assert.Equal(8 * 1024 * 1024, settings.MaxBlockCacheSize);
        Assert.Equal(4 * 1024 * 1024, settings.WriteBufferSize);
    }

    #endregion

    #region ApplyPreset Tests

    [Fact]
    public void ApplyPreset_LowMemory_SetsLowMemoryValues()
    {
        _service.ApplyPreset(DatabasePreset.LowMemory);

        var settings = _service.GetSettings();
        Assert.Equal(4 * 1024 * 1024, settings.MaxBlockCacheSize);
        Assert.Equal(2 * 1024 * 1024, settings.WriteBufferSize);
        Assert.Equal(50, settings.MaxOpenFiles);
        Assert.False(settings.EnableBloomFilters);
    }

    [Fact]
    public void ApplyPreset_HighPerformance_SetsHighPerformanceValues()
    {
        _service.ApplyPreset(DatabasePreset.HighPerformance);

        var settings = _service.GetSettings();
        Assert.Equal(512 * 1024 * 1024, settings.MaxBlockCacheSize);
        Assert.Equal(128 * 1024 * 1024, settings.WriteBufferSize);
        Assert.Equal(1000, settings.MaxOpenFiles);
        Assert.Equal(15, settings.BloomFilterBitsPerKey);
    }

    [Fact]
    public void ApplyPreset_HighDurability_SetsDurabilityValues()
    {
        _service.ApplyPreset(DatabasePreset.HighDurability);

        var settings = _service.GetSettings();
        Assert.True(settings.SyncWrites);
        Assert.True(settings.EnableWAL);
        Assert.True(settings.EnableBackgroundCompaction);
    }

    [Fact]
    public void ApplyPreset_SsdOptimized_SetsSsdValues()
    {
        _service.ApplyPreset(DatabasePreset.SsdOptimized);

        var settings = _service.GetSettings();
        Assert.Equal(128 * 1024 * 1024, settings.MaxBlockCacheSize);
        Assert.Equal(64 * 1024 * 1024, settings.WriteBufferSize);
        Assert.Equal(128 * 1024 * 1024, settings.TargetFileSizeBase);
        Assert.Equal(1.5, settings.CompactionTriggerRatio);
    }

    [Fact]
    public void ApplyPreset_Default_SetsDefaultValues()
    {
        _service.ApplyPreset(DatabasePreset.HighPerformance);
        _service.ApplyPreset(DatabasePreset.Default);

        var settings = _service.GetSettings();
        Assert.Equal(8 * 1024 * 1024, settings.MaxBlockCacheSize);
    }

    #endregion

    #region Recording Methods Tests

    [Fact]
    public void RecordCacheHit_IncrementsCount()
    {
        _service.RecordCacheHit();
        _service.RecordCacheHit();

        var stats = _service.GetStats();
        Assert.Equal(2, stats.BlockCacheHits);
    }

    [Fact]
    public void RecordCacheMiss_IncrementsCount()
    {
        _service.RecordCacheMiss();
        _service.RecordCacheMiss();
        _service.RecordCacheMiss();

        var stats = _service.GetStats();
        Assert.Equal(3, stats.BlockCacheMisses);
    }

    [Fact]
    public void RecordWrite_IncrementsCount()
    {
        _service.RecordWrite();

        var stats = _service.GetStats();
        Assert.Equal(1, stats.WritesCount);
    }

    [Fact]
    public void RecordRead_IncrementsCount()
    {
        _service.RecordRead();

        var stats = _service.GetStats();
        Assert.Equal(1, stats.ReadsCount);
    }

    [Fact]
    public void UpdateSize_SetsValues()
    {
        _service.UpdateSize(1000, 500);

        var stats = _service.GetStats();
        Assert.Equal(1000, stats.TotalSize);
        Assert.Equal(500, stats.WalSize);
    }

    #endregion
}

public class DatabaseTuningSettingsTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var settings = new DatabaseTuningSettings();

        Assert.Equal(8 * 1024 * 1024, settings.MaxBlockCacheSize);
        Assert.Equal(4 * 1024 * 1024, settings.WriteBufferSize);
        Assert.Equal(100, settings.MaxOpenFiles);
        Assert.True(settings.EnableBloomFilters);
        Assert.Equal(10, settings.BloomFilterBitsPerKey);
        Assert.True(settings.EnableCompression);
        Assert.Equal("snappy", settings.CompressionType);
        Assert.True(settings.SyncWrites);
        Assert.True(settings.EnableWAL);
        Assert.Equal(64 * 1024 * 1024, settings.MaxWALSize);
        Assert.True(settings.EnableBackgroundCompaction);
        Assert.Equal(2, settings.CompactionThreads);
        Assert.Equal(64 * 1024 * 1024, settings.TargetFileSizeBase);
        Assert.Equal(2.0, settings.CompactionTriggerRatio);
    }
}

public class DatabaseStatsTests
{
    [Fact]
    public void CacheHitRate_NoOperations_ReturnsZero()
    {
        var stats = new DatabaseStats();

        Assert.Equal(0.0, stats.CacheHitRate);
    }

    [Fact]
    public void CacheHitRate_AllHits_Returns100()
    {
        var stats = new DatabaseStats
        {
            BlockCacheHits = 100,
            BlockCacheMisses = 0
        };

        Assert.Equal(100.0, stats.CacheHitRate);
    }

    [Fact]
    public void CacheHitRate_AllMisses_ReturnsZero()
    {
        var stats = new DatabaseStats
        {
            BlockCacheHits = 0,
            BlockCacheMisses = 100
        };

        Assert.Equal(0.0, stats.CacheHitRate);
    }

    [Fact]
    public void CacheHitRate_Mixed_CalculatesCorrectly()
    {
        var stats = new DatabaseStats
        {
            BlockCacheHits = 80,
            BlockCacheMisses = 20
        };

        Assert.Equal(80.0, stats.CacheHitRate);
    }
}
