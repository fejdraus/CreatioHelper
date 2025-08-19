using CreatioHelper.Infrastructure.Services.Sync;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests;

public class AdaptiveBlockSizerTests
{
    private readonly AdaptiveBlockSizer _sizer;

    public AdaptiveBlockSizerTests()
    {
        var loggerMock = new Mock<ILogger<AdaptiveBlockSizer>>();
        _sizer = new AdaptiveBlockSizer(loggerMock.Object);
    }

    [Theory]
    [InlineData(0, 128 * 1024)]           // Zero file -> min block size
    [InlineData(1024, 128 * 1024)]       // Small file -> min block size
    [InlineData(100 * 1024, 128 * 1024)] // 100KB file -> min block size
    public void CalculateBlockSize_SmallFiles_ReturnsMinimumBlockSize(long fileSize, int expectedBlockSize)
    {
        // Act
        var blockSize = _sizer.CalculateBlockSize(fileSize);

        // Assert
        Assert.Equal(expectedBlockSize, blockSize);
    }

    [Theory]
    [InlineData(1024 * 1024, 128 * 1024)]         // 1MB -> 128KB (8 blocks)
    [InlineData(10 * 1024 * 1024, 128 * 1024)]    // 10MB -> 128KB (80 blocks)
    [InlineData(100 * 1024 * 1024, 128 * 1024)]   // 100MB -> 128KB (800 blocks)
    [InlineData(200 * 1024 * 1024, 128 * 1024)]   // 200MB -> 128KB (1600 blocks)
    public void CalculateBlockSize_MediumFiles_Uses128KB(long fileSize, int expectedBlockSize)
    {
        // Act
        var blockSize = _sizer.CalculateBlockSize(fileSize);

        // Assert
        Assert.Equal(expectedBlockSize, blockSize);
    }

    [Theory]
    [InlineData(300L * 1024 * 1024, 256 * 1024)]   // 300MB -> 256KB (1200 blocks)
    [InlineData(500L * 1024 * 1024, 256 * 1024)]   // 500MB -> 256KB (2000 blocks)
    [InlineData(1000L * 1024 * 1024, 512 * 1024)]  // 1GB -> 512KB (2000 blocks)
    [InlineData(2000L * 1024 * 1024, 1024 * 1024)] // 2GB -> 1MB (2000 blocks)
    public void CalculateBlockSize_LargeFiles_UsesLargerBlocks(long fileSize, int expectedBlockSize)
    {
        // Act
        var blockSize = _sizer.CalculateBlockSize(fileSize, useLargeBlocks: true);

        // Assert
        Assert.Equal(expectedBlockSize, blockSize);
    }

    [Fact]
    public void CalculateBlockSize_WithoutLargeBlocks_LimitsTo128KB()
    {
        // Arrange
        long fileSize = 1000L * 1024 * 1024; // 1GB

        // Act
        var blockSize = _sizer.CalculateBlockSize(fileSize, useLargeBlocks: false);

        // Assert
        Assert.Equal(128 * 1024, blockSize); // Should not exceed 128KB
    }

    [Fact]
    public void CalculateBlockSize_VeryLargeFile_CapsAtMaximum()
    {
        // Arrange - File larger than would need 16MB blocks
        long fileSize = 100L * 1024 * 1024 * 1024; // 100GB

        // Act
        var blockSize = _sizer.CalculateBlockSize(fileSize, useLargeBlocks: true);

        // Assert
        Assert.Equal(16 * 1024 * 1024, blockSize); // Should cap at 16MB
    }

    [Theory]
    [InlineData(128 * 1024, true)]
    [InlineData(256 * 1024, true)]
    [InlineData(512 * 1024, true)]
    [InlineData(1024 * 1024, true)]
    [InlineData(16 * 1024 * 1024, true)]
    [InlineData(64 * 1024, false)]        // Below minimum
    [InlineData(32 * 1024 * 1024, false)] // Above maximum
    [InlineData(200 * 1024, false)]       // Not power of 2
    public void IsValidBlockSize_ReturnsCorrectResult(int blockSize, bool expected)
    {
        // Act
        var result = AdaptiveBlockSizer.IsValidBlockSize(blockSize);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(128 * 1024, "128 KiB")]
    [InlineData(256 * 1024, "256 KiB")]
    [InlineData(1024 * 1024, "1 MiB")]
    [InlineData(16 * 1024 * 1024, "16 MiB")]
    public void FormatBlockSize_ReturnsCorrectFormat(int blockSize, string expected)
    {
        // Act
        var result = AdaptiveBlockSizer.FormatBlockSize(blockSize);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void CalculateBlockSize_WithHysteresis_KeepsCurrentSize()
    {
        // Arrange
        long fileSize = 250L * 1024 * 1024; // 250MB
        int currentBlockSize = 128 * 1024;   // Current is 128KB
        // Ideal would be 256KB, but within hysteresis threshold

        // Act
        var blockSize = _sizer.CalculateBlockSize(fileSize, useLargeBlocks: true, currentBlockSize);

        // Assert - Should keep current size due to hysteresis
        Assert.Equal(currentBlockSize, blockSize);
    }

    [Fact]
    public void CalculateBlockSize_WithSignificantChange_UpdatesSize()
    {
        // Arrange
        long fileSize = 1000L * 1024 * 1024; // 1GB - needs 512KB blocks
        int currentBlockSize = 128 * 1024;    // Current is 128KB (4x smaller than ideal)

        // Act
        var blockSize = _sizer.CalculateBlockSize(fileSize, useLargeBlocks: true, currentBlockSize);

        // Assert - Should update to ideal size due to significant difference
        Assert.Equal(512 * 1024, blockSize);
    }

    [Fact]
    public void GetValidBlockSizes_ReturnsCorrectSequence()
    {
        // Act
        var sizes = AdaptiveBlockSizer.GetValidBlockSizes().ToList();

        // Assert
        var expected = new[] { 
            128 * 1024,   // 128 KiB
            256 * 1024,   // 256 KiB
            512 * 1024,   // 512 KiB
            1024 * 1024,  // 1 MiB
            2 * 1024 * 1024,  // 2 MiB
            4 * 1024 * 1024,  // 4 MiB
            8 * 1024 * 1024,  // 8 MiB
            16 * 1024 * 1024  // 16 MiB
        };
        
        Assert.Equal(expected, sizes);
    }
}