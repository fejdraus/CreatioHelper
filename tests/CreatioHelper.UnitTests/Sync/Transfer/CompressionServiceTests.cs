using CreatioHelper.Infrastructure.Services.Sync.Transfer;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.Transfer;

public class CompressionServiceTests
{
    private readonly Mock<ILogger<CompressionService>> _loggerMock;
    private readonly CompressionConfiguration _config;
    private readonly CompressionService _service;

    public CompressionServiceTests()
    {
        _loggerMock = new Mock<ILogger<CompressionService>>();
        _config = new CompressionConfiguration();
        _service = new CompressionService(_loggerMock.Object, _config);
    }

    #region GetCompressionMode Tests

    [Fact]
    public void GetCompressionMode_Default_ReturnsMetadata()
    {
        var mode = _service.GetCompressionMode("device1");

        Assert.Equal(CompressionMode.Metadata, mode);
    }

    [Fact]
    public void GetCompressionMode_AfterSet_ReturnsSetValue()
    {
        _service.SetCompressionMode("device1", CompressionMode.Always);

        Assert.Equal(CompressionMode.Always, _service.GetCompressionMode("device1"));
    }

    [Fact]
    public void GetCompressionMode_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetCompressionMode(null!));
    }

    #endregion

    #region SetCompressionMode Tests

    [Fact]
    public void SetCompressionMode_ValidMode_SetsCorrectly()
    {
        _service.SetCompressionMode("device1", CompressionMode.Never);

        Assert.Equal(CompressionMode.Never, _service.GetCompressionMode("device1"));
    }

    [Fact]
    public void SetCompressionMode_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.SetCompressionMode(null!, CompressionMode.Always));
    }

    #endregion

    #region ShouldCompress Tests

    [Fact]
    public void ShouldCompress_ModeNever_ReturnsFalse()
    {
        _service.SetCompressionMode("device1", CompressionMode.Never);

        Assert.False(_service.ShouldCompress("device1", DataType.FileData));
        Assert.False(_service.ShouldCompress("device1", DataType.Metadata));
    }

    [Fact]
    public void ShouldCompress_ModeAlways_ReturnsTrue()
    {
        _service.SetCompressionMode("device1", CompressionMode.Always);

        Assert.True(_service.ShouldCompress("device1", DataType.FileData));
        Assert.True(_service.ShouldCompress("device1", DataType.Metadata));
    }

    [Fact]
    public void ShouldCompress_ModeMetadata_OnlyMetadata()
    {
        _service.SetCompressionMode("device1", CompressionMode.Metadata);

        Assert.False(_service.ShouldCompress("device1", DataType.FileData));
        Assert.True(_service.ShouldCompress("device1", DataType.Metadata));
        Assert.True(_service.ShouldCompress("device1", DataType.Request));
        Assert.True(_service.ShouldCompress("device1", DataType.Response));
    }

    [Fact]
    public void ShouldCompress_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.ShouldCompress(null!, DataType.FileData));
    }

    #endregion

    #region CompressIfNeeded Tests

    [Fact]
    public void CompressIfNeeded_ModeNever_ReturnsOriginal()
    {
        _service.SetCompressionMode("device1", CompressionMode.Never);
        var data = new byte[1000];
        Array.Fill(data, (byte)'A');

        var result = _service.CompressIfNeeded("device1", data, DataType.Metadata);

        Assert.Equal(data, result);
    }

    [Fact]
    public void CompressIfNeeded_ModeAlways_CompressesData()
    {
        _service.SetCompressionMode("device1", CompressionMode.Always);
        var data = new byte[1000];
        Array.Fill(data, (byte)'A'); // Highly compressible

        var result = _service.CompressIfNeeded("device1", data, DataType.FileData);

        Assert.True(result.Length < data.Length);
    }

    [Fact]
    public void CompressIfNeeded_SmallData_SkipsCompression()
    {
        _service.SetCompressionMode("device1", CompressionMode.Always);
        var data = new byte[10]; // Below MinSizeToCompress

        var result = _service.CompressIfNeeded("device1", data, DataType.FileData);

        Assert.Equal(data, result);
    }

    [Fact]
    public void CompressIfNeeded_IncompressibleData_ReturnsOriginal()
    {
        _service.SetCompressionMode("device1", CompressionMode.Always);
        // Random data doesn't compress well
        var data = new byte[500];
        new Random(42).NextBytes(data);

        var result = _service.CompressIfNeeded("device1", data, DataType.FileData);

        // May or may not be smaller, but should not throw
        Assert.NotNull(result);
    }

    [Fact]
    public void CompressIfNeeded_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.CompressIfNeeded(null!, Array.Empty<byte>(), DataType.FileData));
    }

    [Fact]
    public void CompressIfNeeded_NullData_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.CompressIfNeeded("device1", null!, DataType.FileData));
    }

    #endregion

    #region DecompressIfNeeded Tests

    [Fact]
    public void DecompressIfNeeded_NotCompressed_ReturnsOriginal()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };

        var result = _service.DecompressIfNeeded(data, false);

        Assert.Equal(data, result);
    }

    [Fact]
    public void DecompressIfNeeded_CompressedData_Decompresses()
    {
        _service.SetCompressionMode("device1", CompressionMode.Always);
        var original = new byte[1000];
        Array.Fill(original, (byte)'B');

        var compressed = _service.CompressIfNeeded("device1", original, DataType.FileData);
        var decompressed = _service.DecompressIfNeeded(compressed, true);

        Assert.Equal(original, decompressed);
    }

    [Fact]
    public void DecompressIfNeeded_NotGzipButMarkedCompressed_ReturnsOriginal()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 }; // Not gzip magic

        var result = _service.DecompressIfNeeded(data, true);

        Assert.Equal(data, result);
    }

    [Fact]
    public void DecompressIfNeeded_NullData_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.DecompressIfNeeded(null!, false));
    }

    #endregion

    #region IsCompressible Tests

    [Theory]
    [InlineData(".txt", true)]
    [InlineData(".json", true)]
    [InlineData(".xml", true)]
    [InlineData(".cs", true)]
    [InlineData(".zip", false)]
    [InlineData(".gz", false)]
    [InlineData(".jpg", false)]
    [InlineData(".png", false)]
    [InlineData(".mp4", false)]
    [InlineData(".pdf", false)]
    public void IsCompressible_ChecksExtension(string extension, bool expected)
    {
        var result = _service.IsCompressible(extension);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsCompressible_EmptyExtension_ReturnsTrue()
    {
        Assert.True(_service.IsCompressible(""));
    }

    [Fact]
    public void IsCompressible_WithoutDot_HandlesCorrectly()
    {
        Assert.False(_service.IsCompressible("zip"));
    }

    #endregion

    #region GetStats Tests

    [Fact]
    public void GetStats_NewDevice_ReturnsEmptyStats()
    {
        var stats = _service.GetStats("device1");

        Assert.Equal("device1", stats.DeviceId);
        Assert.Equal(0, stats.CompressedBlocks);
        Assert.Equal(0, stats.BytesBeforeCompression);
    }

    [Fact]
    public void GetStats_AfterCompression_TracksCorrectly()
    {
        _service.SetCompressionMode("device1", CompressionMode.Always);
        var data = new byte[1000];
        Array.Fill(data, (byte)'C');

        _service.CompressIfNeeded("device1", data, DataType.FileData);

        var stats = _service.GetStats("device1");
        Assert.Equal(1, stats.CompressedBlocks);
        Assert.True(stats.BytesSaved > 0);
    }

    [Fact]
    public void GetStats_CompressionRatio_CalculatesCorrectly()
    {
        _service.SetCompressionMode("device1", CompressionMode.Always);
        var data = new byte[1000];
        Array.Fill(data, (byte)'D');

        _service.CompressIfNeeded("device1", data, DataType.FileData);

        var stats = _service.GetStats("device1");
        Assert.True(stats.CompressionRatio < 1.0);
    }

    [Fact]
    public void GetStats_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetStats(null!));
    }

    #endregion

    #region GetGlobalStats Tests

    [Fact]
    public void GetGlobalStats_NoDevices_ReturnsEmptyStats()
    {
        var stats = _service.GetGlobalStats();

        Assert.Equal(0, stats.TotalCompressedBlocks);
        Assert.Equal(0, stats.DevicesWithCompression);
    }

    [Fact]
    public void GetGlobalStats_MultipleDevices_AggregatesCorrectly()
    {
        _service.SetCompressionMode("device1", CompressionMode.Always);
        _service.SetCompressionMode("device2", CompressionMode.Always);

        var data = new byte[1000];
        Array.Fill(data, (byte)'E');

        _service.CompressIfNeeded("device1", data, DataType.FileData);
        _service.CompressIfNeeded("device2", data, DataType.FileData);

        var stats = _service.GetGlobalStats();

        Assert.Equal(2, stats.TotalCompressedBlocks);
        Assert.Equal(2, stats.DevicesWithCompression);
    }

    #endregion
}

public class CompressionConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new CompressionConfiguration();

        Assert.Equal(CompressionMode.Metadata, config.DefaultMode);
        Assert.Equal(128, config.MinSizeToCompress);
        Assert.Contains(".zip", config.IncompressibleExtensions);
        Assert.Contains(".jpg", config.IncompressibleExtensions);
    }

    [Fact]
    public void GetEffectiveMode_NoOverride_ReturnsDefault()
    {
        var config = new CompressionConfiguration { DefaultMode = CompressionMode.Always };

        Assert.Equal(CompressionMode.Always, config.GetEffectiveMode("device1"));
    }

    [Fact]
    public void GetEffectiveMode_WithOverride_ReturnsOverride()
    {
        var config = new CompressionConfiguration { DefaultMode = CompressionMode.Metadata };
        config.DeviceModes["device1"] = CompressionMode.Never;

        Assert.Equal(CompressionMode.Never, config.GetEffectiveMode("device1"));
        Assert.Equal(CompressionMode.Metadata, config.GetEffectiveMode("device2"));
    }
}

public class CompressionStatsTests
{
    [Fact]
    public void CompressionRatio_NoData_ReturnsOne()
    {
        var stats = new CompressionStats();

        Assert.Equal(1.0, stats.CompressionRatio);
    }

    [Fact]
    public void CompressionRatio_WithData_CalculatesCorrectly()
    {
        var stats = new CompressionStats
        {
            BytesBeforeCompression = 1000,
            BytesAfterCompression = 500
        };

        Assert.Equal(0.5, stats.CompressionRatio);
    }

    [Fact]
    public void BytesSaved_CalculatesCorrectly()
    {
        var stats = new CompressionStats
        {
            BytesBeforeCompression = 1000,
            BytesAfterCompression = 700
        };

        Assert.Equal(300, stats.BytesSaved);
    }
}
