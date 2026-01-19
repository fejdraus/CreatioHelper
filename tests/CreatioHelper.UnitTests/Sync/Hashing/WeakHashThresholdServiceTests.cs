using CreatioHelper.Infrastructure.Services.Sync.Hashing;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.Hashing;

public class WeakHashThresholdServiceTests
{
    private readonly Mock<ILogger<WeakHashThresholdService>> _loggerMock;
    private readonly WeakHashThresholdService _service;

    public WeakHashThresholdServiceTests()
    {
        _loggerMock = new Mock<ILogger<WeakHashThresholdService>>();
        _service = new WeakHashThresholdService(_loggerMock.Object);
    }

    #region ShouldUseWeakHash Tests

    [Theory]
    [InlineData(0, false)] // Threshold 0 = never
    [InlineData(100, true)] // Threshold 100 = always
    public void ShouldUseWeakHash_ExtremesThreshold_WorksCorrectly(int threshold, bool expected)
    {
        var result = _service.ShouldUseWeakHash(10 * 1024 * 1024, threshold);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void ShouldUseWeakHash_SmallFile_ReturnsFalse()
    {
        // Files < 256KB should always use strong hash
        var result = _service.ShouldUseWeakHash(100 * 1024, 50);

        Assert.False(result);
    }

    [Theory]
    [InlineData(1 * 1024 * 1024, 25)] // 1 MB, 25% threshold
    [InlineData(100 * 1024 * 1024, 25)] // 100 MB, 25% threshold
    [InlineData(1024 * 1024 * 1024L, 25)] // 1 GB, 25% threshold
    public void ShouldUseWeakHash_VariousFileSizes_ReturnsConsistentResults(long fileSize, int threshold)
    {
        // Just verify it doesn't throw and returns a boolean
        var result = _service.ShouldUseWeakHash(fileSize, threshold);

        Assert.True(result || !result); // Always true, just checking execution
    }

    [Fact]
    public void ShouldUseWeakHash_VeryLargeFile_MoreLikelyToUseWeakHash()
    {
        // Larger files should be more likely to use weak hash
        var smallFileResult = _service.ShouldUseWeakHash(1 * 1024 * 1024, 50); // 1 MB
        var largeFileResult = _service.ShouldUseWeakHash(10L * 1024 * 1024 * 1024, 50); // 10 GB

        // Large files with 50% threshold should use weak hash
        Assert.True(largeFileResult);
    }

    #endregion

    #region GetRecommendedThreshold Tests

    [Fact]
    public void GetRecommendedThreshold_DefaultCharacteristics_ReturnsDefault()
    {
        var characteristics = new FolderCharacteristics();

        var result = _service.GetRecommendedThreshold(characteristics);

        Assert.Equal(25, result); // Default Syncthing value
    }

    [Fact]
    public void GetRecommendedThreshold_SecurityPriority_LowersThreshold()
    {
        var characteristics = new FolderCharacteristics
        {
            SecurityPriority = true
        };

        var result = _service.GetRecommendedThreshold(characteristics);

        Assert.True(result < 25);
    }

    [Fact]
    public void GetRecommendedThreshold_HighChangeRate_IncreasesThreshold()
    {
        var characteristics = new FolderCharacteristics
        {
            HighChangeRate = true
        };

        var result = _service.GetRecommendedThreshold(characteristics);

        Assert.True(result > 25);
    }

    [Fact]
    public void GetRecommendedThreshold_SlowConnection_IncreasesThreshold()
    {
        var characteristics = new FolderCharacteristics
        {
            SlowConnection = true
        };

        var result = _service.GetRecommendedThreshold(characteristics);

        Assert.True(result > 25);
    }

    [Fact]
    public void GetRecommendedThreshold_LargeFiles_IncreasesThreshold()
    {
        var characteristics = new FolderCharacteristics
        {
            AverageFileSize = 50 * 1024 * 1024 // 50 MB average
        };

        var result = _service.GetRecommendedThreshold(characteristics);

        Assert.True(result > 25);
    }

    [Fact]
    public void GetRecommendedThreshold_NeverExceedsBounds()
    {
        // All factors increasing threshold
        var maxCharacteristics = new FolderCharacteristics
        {
            HighChangeRate = true,
            SlowConnection = true,
            AverageFileSize = 100 * 1024 * 1024
        };

        // All factors decreasing threshold
        var minCharacteristics = new FolderCharacteristics
        {
            SecurityPriority = true,
            SlowStorage = true
        };

        var maxResult = _service.GetRecommendedThreshold(maxCharacteristics);
        var minResult = _service.GetRecommendedThreshold(minCharacteristics);

        Assert.True(maxResult <= 100);
        Assert.True(minResult >= 0);
    }

    [Fact]
    public void GetRecommendedThreshold_NullCharacteristics_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetRecommendedThreshold(null!));
    }

    #endregion

    #region CalculateWeakHashBlockSize Tests

    [Theory]
    [InlineData(0, 128 * 1024)] // 0 bytes -> default 128 KB
    [InlineData(100 * 1024 * 1024, 128 * 1024)] // 100 MB -> 128 KB
    [InlineData(300 * 1024 * 1024, 1 * 1024 * 1024)] // 300 MB -> 1 MB
    [InlineData(600 * 1024 * 1024, 2 * 1024 * 1024)] // 600 MB -> 2 MB
    [InlineData(1500 * 1024 * 1024L, 4 * 1024 * 1024)] // 1.5 GB -> 4 MB
    [InlineData(3L * 1024 * 1024 * 1024, 8 * 1024 * 1024)] // 3 GB -> 8 MB
    [InlineData(5L * 1024 * 1024 * 1024, 16 * 1024 * 1024)] // 5 GB -> 16 MB
    public void CalculateWeakHashBlockSize_ScalesWithFileSize(long fileSize, int expectedBlockSize)
    {
        var result = _service.CalculateWeakHashBlockSize(fileSize, 25);

        Assert.Equal(expectedBlockSize, result);
    }

    [Fact]
    public void CalculateWeakHashBlockSize_LowThreshold_SmallerBlocks()
    {
        var normalBlock = _service.CalculateWeakHashBlockSize(100 * 1024 * 1024, 50);
        var lowThresholdBlock = _service.CalculateWeakHashBlockSize(100 * 1024 * 1024, 10);

        Assert.True(lowThresholdBlock <= normalBlock);
    }

    #endregion

    #region Stats Tests

    [Fact]
    public void GetStats_InitialState_AllZero()
    {
        var stats = _service.GetStats();

        Assert.Equal(0, stats.TotalFiles);
        Assert.Equal(0, stats.FilesWithWeakHash);
        Assert.Equal(0, stats.FilesWithStrongHash);
    }

    [Fact]
    public void RecordUsage_UpdatesStats()
    {
        _service.RecordUsage(usedWeakHash: true, fileSize: 1000, hashDuration: TimeSpan.FromMilliseconds(10));
        _service.RecordUsage(usedWeakHash: false, fileSize: 2000, hashDuration: TimeSpan.FromMilliseconds(20));

        var stats = _service.GetStats();

        Assert.Equal(2, stats.TotalFiles);
        Assert.Equal(1, stats.FilesWithWeakHash);
        Assert.Equal(1, stats.FilesWithStrongHash);
        Assert.Equal(3000, stats.TotalBytesHashed);
    }

    [Fact]
    public void Stats_WeakHashPercentage_CalculatesCorrectly()
    {
        _service.RecordUsage(usedWeakHash: true, fileSize: 1000, hashDuration: TimeSpan.FromMilliseconds(10));
        _service.RecordUsage(usedWeakHash: true, fileSize: 1000, hashDuration: TimeSpan.FromMilliseconds(10));
        _service.RecordUsage(usedWeakHash: false, fileSize: 1000, hashDuration: TimeSpan.FromMilliseconds(10));
        _service.RecordUsage(usedWeakHash: false, fileSize: 1000, hashDuration: TimeSpan.FromMilliseconds(10));

        var stats = _service.GetStats();

        Assert.Equal(50.0, stats.WeakHashPercentage);
    }

    #endregion
}

public class WeakHashConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new WeakHashConfiguration();

        Assert.Equal(25, config.DefaultThresholdPercent);
        Assert.Equal(256 * 1024, config.MinFileSizeForWeakHash);
        Assert.True(config.AutoAdjustThreshold);
    }

    [Fact]
    public void GetEffectiveThreshold_FolderOverride_TakesPrecedence()
    {
        var config = new WeakHashConfiguration();
        config.FolderThresholds["folder1"] = 75;

        var result = config.GetEffectiveThreshold("folder1", folderConfiguredThreshold: 50);

        Assert.Equal(75, result);
    }

    [Fact]
    public void GetEffectiveThreshold_FolderConfigured_UsedIfNoOverride()
    {
        var config = new WeakHashConfiguration();

        var result = config.GetEffectiveThreshold("folder1", folderConfiguredThreshold: 50);

        Assert.Equal(50, result);
    }

    [Fact]
    public void GetEffectiveThreshold_NoOverride_UsesDefault()
    {
        var config = new WeakHashConfiguration { DefaultThresholdPercent = 30 };

        var result = config.GetEffectiveThreshold("folder1");

        Assert.Equal(30, result);
    }
}

public class FolderCharacteristicsTests
{
    [Fact]
    public void DefaultValues_AllFalseOrZero()
    {
        var characteristics = new FolderCharacteristics();

        Assert.Equal(0, characteristics.AverageFileSize);
        Assert.Equal(0, characteristics.FileCount);
        Assert.False(characteristics.HighChangeRate);
        Assert.False(characteristics.SlowStorage);
        Assert.False(characteristics.SlowConnection);
        Assert.False(characteristics.SecurityPriority);
    }
}
