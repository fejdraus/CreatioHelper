using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using CreatioHelper.Infrastructure.Services.Sync;

namespace CreatioHelper.Tests;

public class CompressionEngineTests
{
    private readonly CompressionEngine _compressionEngine;
    private readonly Mock<ILogger<CompressionEngine>> _mockLogger;

    public CompressionEngineTests()
    {
        _mockLogger = new Mock<ILogger<CompressionEngine>>();
        _compressionEngine = new CompressionEngine(_mockLogger.Object);
    }

    [Fact]
    public void CompressBlock_EmptyData_ReturnsOriginalData()
    {
        // Arrange
        var emptyData = Array.Empty<byte>();

        // Act
        var (compressedData, actualType) = _compressionEngine.CompressBlock(emptyData);

        // Assert
        Assert.Equal(emptyData, compressedData);
        Assert.Equal(CompressionType.None, actualType);
    }

    [Fact]
    public void CompressBlock_NullData_ReturnsEmptyArray()
    {
        // Act
        var (compressedData, actualType) = _compressionEngine.CompressBlock(null!);

        // Assert
        Assert.Empty(compressedData);
        Assert.Equal(CompressionType.None, actualType);
    }

    [Fact]
    public void CompressBlock_SmallData_ReturnsOriginalData()
    {
        // Arrange
        var smallData = new byte[100]; // Less than 128 bytes threshold
        Random.Shared.NextBytes(smallData);

        // Act
        var (compressedData, actualType) = _compressionEngine.CompressBlock(smallData);

        // Assert
        Assert.Equal(smallData, compressedData);
        Assert.Equal(CompressionType.None, actualType);
    }

    [Fact]
    public void CompressBlock_CompressibleData_CompressesWithLZ4()
    {
        // Arrange - highly compressible data (repeated pattern)
        var compressibleData = new byte[1000];
        for (int i = 0; i < compressibleData.Length; i++)
        {
            compressibleData[i] = (byte)(i % 10); // Repeating pattern
        }

        // Act
        var (compressedData, actualType) = _compressionEngine.CompressBlock(compressibleData, CompressionType.LZ4);

        // Assert
        Assert.True(compressedData.Length < compressibleData.Length);
        Assert.Equal(CompressionType.LZ4, actualType);
        
        // Compression should be significant for this pattern
        var compressionRatio = (float)compressedData.Length / compressibleData.Length;
        Assert.True(compressionRatio < 0.5f); // At least 50% compression
    }

    [Fact]
    public void CompressBlock_CompressibleData_CompressesWithGZIP()
    {
        // Arrange - highly compressible data
        var compressibleData = new byte[1000];
        for (int i = 0; i < compressibleData.Length; i++)
        {
            compressibleData[i] = (byte)(i % 5); // Very repeating pattern
        }

        // Act
        var (compressedData, actualType) = _compressionEngine.CompressBlock(compressibleData, CompressionType.GZIP);

        // Assert
        Assert.True(compressedData.Length < compressibleData.Length);
        Assert.Equal(CompressionType.GZIP, actualType);
    }

    [Fact]
    public void CompressBlock_RandomData_MayNotCompress()
    {
        // Arrange - random data is typically not compressible
        var randomData = new byte[500];
        Random.Shared.NextBytes(randomData);

        // Act
        var (compressedData, actualType) = _compressionEngine.CompressBlock(randomData, CompressionType.LZ4);

        // Assert
        // Random data may or may not compress effectively
        // If compression wasn't beneficial, should return original data
        if (actualType == CompressionType.None)
        {
            Assert.Equal(randomData, compressedData);
        }
        else
        {
            Assert.True(compressedData.Length < randomData.Length * 0.9f); // At least 10% reduction
        }
    }

    [Fact]
    public void CompressBlock_AutoType_ChoosesBestCompression()
    {
        // Arrange - compressible data
        var compressibleData = new byte[1000];
        for (int i = 0; i < compressibleData.Length; i++)
        {
            compressibleData[i] = (byte)(i % 20);
        }

        // Act
        var (compressedData, actualType) = _compressionEngine.CompressBlock(compressibleData, CompressionType.Auto);

        // Assert
        Assert.True(actualType == CompressionType.LZ4 || actualType == CompressionType.GZIP);
        Assert.True(compressedData.Length < compressibleData.Length);
    }

    [Fact]
    public void DecompressBlock_LZ4CompressedData_ReturnsOriginalData()
    {
        // Arrange
        var originalData = new byte[500];
        for (int i = 0; i < originalData.Length; i++)
        {
            originalData[i] = (byte)(i % 15);
        }

        var (compressedData, compressionType) = _compressionEngine.CompressBlock(originalData, CompressionType.LZ4);
        
        // Skip test if compression wasn't applied
        if (compressionType == CompressionType.None)
            return;

        // Act
        var decompressedData = _compressionEngine.DecompressBlock(compressedData, compressionType, originalData.Length);

        // Assert
        Assert.Equal(originalData, decompressedData);
    }

    [Fact]
    public void DecompressBlock_GZIPCompressedData_ReturnsOriginalData()
    {
        // Arrange
        var originalData = new byte[500];
        for (int i = 0; i < originalData.Length; i++)
        {
            originalData[i] = (byte)(i % 8);
        }

        var (compressedData, compressionType) = _compressionEngine.CompressBlock(originalData, CompressionType.GZIP);
        
        // Skip test if compression wasn't applied
        if (compressionType == CompressionType.None)
            return;

        // Act
        var decompressedData = _compressionEngine.DecompressBlock(compressedData, compressionType, originalData.Length);

        // Assert
        Assert.Equal(originalData, decompressedData);
    }

    [Fact]
    public void DecompressBlock_NoCompression_ReturnsOriginalData()
    {
        // Arrange
        var originalData = new byte[200];
        Random.Shared.NextBytes(originalData);

        // Act
        var decompressedData = _compressionEngine.DecompressBlock(originalData, CompressionType.None);

        // Assert
        Assert.Equal(originalData, decompressedData);
    }

    [Fact]
    public void DecompressBlock_EmptyData_ReturnsEmptyArray()
    {
        // Act
        var decompressedData = _compressionEngine.DecompressBlock(Array.Empty<byte>(), CompressionType.LZ4);

        // Assert
        Assert.Empty(decompressedData);
    }

    [Fact]
    public void DecompressBlock_NullData_ReturnsEmptyArray()
    {
        // Act
        var decompressedData = _compressionEngine.DecompressBlock(null!, CompressionType.LZ4);

        // Assert
        Assert.Empty(decompressedData);
    }

    [Theory]
    [InlineData(50)]   // Too small
    [InlineData(100)]  // Too small  
    [InlineData(200)]  // Large enough
    [InlineData(1000)] // Large
    public void ShouldCompress_DifferentSizes_ReturnsExpectedResult(int size)
    {
        // Arrange
        var data = new byte[size];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % 10); // Compressible pattern
        }

        // Act
        var shouldCompress = _compressionEngine.ShouldCompress(data);

        // Assert
        var expected = size >= 128; // Compression threshold
        Assert.Equal(expected, shouldCompress);
    }

    [Fact]
    public void ShouldCompress_NullData_ReturnsFalse()
    {
        // Act
        var shouldCompress = _compressionEngine.ShouldCompress(null!);

        // Assert
        Assert.False(shouldCompress);
    }

    [Fact]
    public void ShouldCompress_EmptyData_ReturnsFalse()
    {
        // Act
        var shouldCompress = _compressionEngine.ShouldCompress(Array.Empty<byte>());

        // Assert
        Assert.False(shouldCompress);
    }

    [Fact]
    public void CompressDecompressRoundTrip_LargeData_MaintainsDataIntegrity()
    {
        // Arrange - large mixed data
        var originalData = new byte[10000];
        for (int i = 0; i < originalData.Length; i++)
        {
            if (i % 100 < 50)
                originalData[i] = (byte)(i % 10); // Compressible sections
            else
                originalData[i] = (byte)Random.Shared.Next(256); // Random sections
        }

        // Act - compress and decompress
        var (compressedData, compressionType) = _compressionEngine.CompressBlock(originalData, CompressionType.Auto);
        
        if (compressionType != CompressionType.None)
        {
            var decompressedData = _compressionEngine.DecompressBlock(compressedData, compressionType, originalData.Length);
            
            // Assert
            Assert.Equal(originalData, decompressedData);
        }
    }
}