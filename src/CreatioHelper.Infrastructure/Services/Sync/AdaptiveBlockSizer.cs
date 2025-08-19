using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Implements Syncthing's adaptive block sizing algorithm
/// Based on Syncthing specification: keeps blocks between 1000-2000 per file
/// </summary>
public class AdaptiveBlockSizer
{
    private readonly ILogger<AdaptiveBlockSizer> _logger;

    // Syncthing constants
    private const int MIN_BLOCK_SIZE = 128 * 1024;     // 128 KiB
    private const int MAX_BLOCK_SIZE = 16 * 1024 * 1024; // 16 MiB
    private const int TARGET_BLOCKS_MIN = 1000;
    private const int TARGET_BLOCKS_MAX = 2000;
    private const long LARGE_BLOCKS_THRESHOLD = 256 * 1024 * 1024; // 256 MiB

    public AdaptiveBlockSizer(ILogger<AdaptiveBlockSizer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Calculates optimal block size for a file based on Syncthing's algorithm
    /// </summary>
    /// <param name="fileSize">Size of the file in bytes</param>
    /// <param name="useLargeBlocks">Enable large blocks for files > 256 MiB</param>
    /// <param name="currentBlockSize">Current block size (for hysteresis)</param>
    /// <returns>Optimal block size in bytes</returns>
    public int CalculateBlockSize(long fileSize, bool useLargeBlocks = true, int? currentBlockSize = null)
    {
        if (fileSize <= 0)
        {
            _logger.LogDebug("File size is zero or negative, using minimum block size");
            return MIN_BLOCK_SIZE;
        }

        // For small files, always use minimum block size
        if (fileSize <= MIN_BLOCK_SIZE)
        {
            _logger.LogTrace("Small file ({FileSize} bytes), using min block size {BlockSize}", 
                fileSize, MIN_BLOCK_SIZE);
            return MIN_BLOCK_SIZE;
        }

        // Calculate ideal block size to achieve target block count
        int idealBlockSize = CalculateIdealBlockSize(fileSize, useLargeBlocks);

        // Apply hysteresis if current block size is provided
        if (currentBlockSize.HasValue)
        {
            idealBlockSize = ApplyHysteresis(idealBlockSize, currentBlockSize.Value);
        }

        _logger.LogDebug("Calculated block size for file {FileSize} bytes: {BlockSize} ({BlockCount} blocks)", 
            fileSize, idealBlockSize, (fileSize + idealBlockSize - 1) / idealBlockSize);

        return idealBlockSize;
    }

    /// <summary>
    /// Calculates the ideal block size without hysteresis
    /// </summary>
    private int CalculateIdealBlockSize(long fileSize, bool useLargeBlocks)
    {
        int blockSize = MIN_BLOCK_SIZE;
        int maxAllowedBlockSize = useLargeBlocks || fileSize <= LARGE_BLOCKS_THRESHOLD 
            ? MAX_BLOCK_SIZE 
            : MIN_BLOCK_SIZE;

        // Increase block size until we're within target range or hit maximum
        while (blockSize < maxAllowedBlockSize)
        {
            long blockCount = (fileSize + blockSize - 1) / blockSize;
            
            if (blockCount <= TARGET_BLOCKS_MAX)
            {
                break;
            }

            // Double the block size (next power of 2)
            int nextBlockSize = blockSize * 2;
            if (nextBlockSize > maxAllowedBlockSize)
            {
                blockSize = maxAllowedBlockSize;
                break;
            }
            
            blockSize = nextBlockSize;
        }

        return blockSize;
    }

    /// <summary>
    /// Applies hysteresis to avoid frequent block size changes
    /// Only changes block size if difference exceeds one level (2x)
    /// </summary>
    private int ApplyHysteresis(int idealBlockSize, int currentBlockSize)
    {
        // If ideal size is significantly different (more than one level), use ideal
        if (idealBlockSize >= currentBlockSize * 2 || idealBlockSize * 2 <= currentBlockSize)
        {
            _logger.LogDebug("Block size change significant: {CurrentSize} -> {IdealSize}, applying change", 
                currentBlockSize, idealBlockSize);
            return idealBlockSize;
        }

        // Otherwise, keep current size to avoid thrashing
        _logger.LogTrace("Block size change within hysteresis threshold, keeping current size {CurrentSize}", 
            currentBlockSize);
        return currentBlockSize;
    }

    /// <summary>
    /// Gets all valid block sizes (powers of 2 from 128KB to 16MB)
    /// </summary>
    public static IEnumerable<int> GetValidBlockSizes()
    {
        int size = MIN_BLOCK_SIZE;
        while (size <= MAX_BLOCK_SIZE)
        {
            yield return size;
            size *= 2;
        }
    }

    /// <summary>
    /// Validates if a block size is valid according to Syncthing spec
    /// </summary>
    public static bool IsValidBlockSize(int blockSize)
    {
        return blockSize >= MIN_BLOCK_SIZE && 
               blockSize <= MAX_BLOCK_SIZE && 
               (blockSize & (blockSize - 1)) == 0; // Power of 2 check
    }

    /// <summary>
    /// Formats block size for human-readable display
    /// </summary>
    public static string FormatBlockSize(int blockSize)
    {
        if (blockSize >= 1024 * 1024)
            return $"{blockSize / (1024 * 1024)} MiB";
        else
            return $"{blockSize / 1024} KiB";
    }
}