using System.Security.Cryptography;
using System.Text;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Syncthing-compatible block calculator implementing the exact algorithms from Syncthing v2.0+
/// Uses SHA-256 for strong hashing without rolling hash support for simplicity and performance
/// </summary>
public class SyncthingBlockCalculator
{
    private readonly ILogger<SyncthingBlockCalculator> _logger;
    
    // Syncthing block size constants - powers of 2 from 128KB to 16MB
    public const int MinBlockSize = 128 * 1024;      // 128KB
    public const int MaxBlockSize = 16 * 1024 * 1024; // 16MB
    public const int DesiredPerFileBlocks = 2000;     // Target blocks per file
    
    // Valid block sizes array (powers of 2)
    public static readonly int[] BlockSizes = new[]
    {
        128 * 1024,   // 128KB
        256 * 1024,   // 256KB
        512 * 1024,   // 512KB
        1024 * 1024,  // 1MB
        2048 * 1024,  // 2MB
        4096 * 1024,  // 4MB
        8192 * 1024,  // 8MB
        16384 * 1024  // 16MB
    };
    
    // Pre-calculated SHA-256 hashes for empty blocks of each size
    private static readonly Dictionary<int, byte[]> EmptyBlockHashes = new();
    
    static SyncthingBlockCalculator()
    {
        // Pre-calculate hashes for empty blocks of each size
        foreach (var blockSize in BlockSizes)
        {
            var emptyBlock = new byte[blockSize];
            EmptyBlockHashes[blockSize] = SHA256.HashData(emptyBlock);
        }
    }
    
    public SyncthingBlockCalculator(ILogger<SyncthingBlockCalculator> logger)
    {
        _logger = logger;
    }
    
    /// <summary>
    /// Calculate optimal block size for file using Syncthing's algorithm
    /// Aims for approximately 2000 blocks per file
    /// </summary>
    public int CalculateBlockSize(long fileSize)
    {
        if (fileSize <= 0) return MinBlockSize;
        
        foreach (var blockSize in BlockSizes)
        {
            if (fileSize < DesiredPerFileBlocks * (long)blockSize)
            {
                return blockSize;
            }
        }
        
        return MaxBlockSize; // Use max block size for very large files
    }
    
    /// <summary>
    /// Calculate blocks for a file using Syncthing's algorithm
    /// Returns list of BlockInfo with SHA-256 hashes
    /// </summary>
    public async Task<SyncthingBlocksResult> CalculateFileBlocksAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var result = new SyncthingBlocksResult
        {
            FilePath = filePath,
            StartTime = DateTime.UtcNow
        };
        
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                result.Error = $"File not found: {filePath}";
                return result;
            }
            
            result.FileSize = fileInfo.Length;
            result.BlockSize = CalculateBlockSize(fileInfo.Length);
            
            _logger.LogDebug("Calculating blocks for file {FilePath}: size={FileSize}, blockSize={BlockSize}", 
                filePath, result.FileSize, result.BlockSize);
            
            // Calculate blocks
            result.Blocks = await CalculateBlocksAsync(filePath, result.BlockSize, cancellationToken);
            
            // Calculate statistics
            result.TotalBlocks = result.Blocks.Count;
            result.EmptyBlocks = result.Blocks.Count(b => b.IsEmpty);
            result.UniqueBlocks = result.Blocks.GroupBy(b => b.Hash).Count();
            
            result.EndTime = DateTime.UtcNow;
            result.Duration = result.EndTime - result.StartTime;
            
            _logger.LogInformation("Block calculation completed for {FilePath}: {TotalBlocks} total blocks, {EmptyBlocks} empty, {UniqueBlocks} unique in {Duration}ms",
                filePath, result.TotalBlocks, result.EmptyBlocks, result.UniqueBlocks, result.Duration.TotalMilliseconds);
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calculating blocks for file {FilePath}", filePath);
            result.Error = ex.Message;
            result.EndTime = DateTime.UtcNow;
            result.Duration = result.EndTime - result.StartTime;
            return result;
        }
    }
    
    /// <summary>
    /// Calculate blocks for file data using Syncthing's approach
    /// </summary>
    private async Task<List<SyncthingBlockInfo>> CalculateBlocksAsync(string filePath, int blockSize, CancellationToken cancellationToken)
    {
        var blocks = new List<SyncthingBlockInfo>();
        var buffer = new byte[blockSize];
        
        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long offset = 0;
        
        while (offset < fileStream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, blockSize), cancellationToken);
            if (bytesRead == 0) break;
            
            // Create block data (only the bytes we actually read)
            var blockData = new byte[bytesRead];
            Array.Copy(buffer, blockData, bytesRead);
            
            // Calculate SHA-256 hash
            var hash = SHA256.HashData(blockData);
            
            var block = new SyncthingBlockInfo
            {
                Offset = offset,
                Size = bytesRead,
                Hash = hash,
                HashString = Convert.ToHexString(hash).ToLowerInvariant(),
                IsEmpty = IsEmptyBlock(hash, bytesRead),
                Data = blockData // Store data for deduplication analysis
            };
            
            blocks.Add(block);
            offset += bytesRead;
        }
        
        return blocks;
    }
    
    /// <summary>
    /// Check if block is empty (all zeros) using pre-calculated hashes
    /// </summary>
    private bool IsEmptyBlock(byte[] hash, int blockSize)
    {
        if (EmptyBlockHashes.TryGetValue(blockSize, out var emptyHash))
        {
            return hash.SequenceEqual(emptyHash);
        }
        
        // Fallback: calculate empty hash for non-standard block size
        var emptyBlock = new byte[blockSize];
        var calculatedEmptyHash = SHA256.HashData(emptyBlock);
        return hash.SequenceEqual(calculatedEmptyHash);
    }
    
    /// <summary>
    /// Compare two sets of blocks and identify differences (Syncthing-style)
    /// Returns blocks that need to be transferred
    /// </summary>
    public SyncthingBlockDiff CompareBlocks(List<SyncthingBlockInfo> sourceBlocks, List<SyncthingBlockInfo> targetBlocks)
    {
        var diff = new SyncthingBlockDiff
        {
            SourceBlocks = sourceBlocks,
            TargetBlocks = targetBlocks
        };
        
        var maxBlocks = Math.Max(sourceBlocks.Count, targetBlocks.Count);
        
        for (int i = 0; i < maxBlocks; i++)
        {
            var sourceBlock = i < sourceBlocks.Count ? sourceBlocks[i] : null;
            var targetBlock = i < targetBlocks.Count ? targetBlocks[i] : null;
            
            if (targetBlock == null)
            {
                // No more target blocks - source blocks beyond this point can be reused
                if (sourceBlock != null)
                {
                    diff.ReusableBlocks.Add(sourceBlock);
                }
            }
            else if (sourceBlock == null)
            {
                // No source block at this position - need to download target block
                diff.NeededBlocks.Add(targetBlock);
            }
            else if (!sourceBlock.Hash.SequenceEqual(targetBlock.Hash))
            {
                // Blocks differ at same position - need to download target block
                diff.NeededBlocks.Add(targetBlock);
                // Note: source block could potentially be reused elsewhere, but Syncthing doesn't do this
            }
            else
            {
                // Blocks match at same position - can reuse
                diff.ReusableBlocks.Add(sourceBlock);
            }
        }
        
        // Calculate statistics
        diff.TotalBytesToTransfer = diff.NeededBlocks.Sum(b => (long)b.Size);
        diff.TotalBytesReused = diff.ReusableBlocks.Sum(b => (long)b.Size);
        diff.TransferRatio = diff.TotalBytesToTransfer / (double)Math.Max(1, diff.TotalBytesToTransfer + diff.TotalBytesReused);
        
        _logger.LogDebug("Block comparison: {NeededBlocks}/{TotalBlocks} blocks need transfer ({TransferBytes} bytes, {TransferRatio:P1})",
            diff.NeededBlocks.Count, targetBlocks.Count, diff.TotalBytesToTransfer, diff.TransferRatio);
        
        return diff;
    }
    
    /// <summary>
    /// Verify block integrity by recalculating hash
    /// </summary>
    public bool VerifyBlock(byte[] blockData, byte[] expectedHash)
    {
        var actualHash = SHA256.HashData(blockData);
        return actualHash.SequenceEqual(expectedHash);
    }
    
    /// <summary>
    /// Get statistics about block distribution
    /// </summary>
    public BlockDistributionStats GetBlockDistributionStats(List<SyncthingBlockInfo> blocks)
    {
        var stats = new BlockDistributionStats
        {
            TotalBlocks = blocks.Count,
            TotalSize = blocks.Sum(b => (long)b.Size),
            EmptyBlocks = blocks.Count(b => b.IsEmpty),
            EmptySize = blocks.Where(b => b.IsEmpty).Sum(b => (long)b.Size)
        };
        
        // Group by hash to find duplicates
        var hashGroups = blocks.GroupBy(b => b.HashString).ToList();
        stats.UniqueBlocks = hashGroups.Count;
        stats.DuplicateBlocks = blocks.Count - stats.UniqueBlocks;
        
        // Calculate size distribution
        var sizeGroups = blocks.GroupBy(b => b.Size);
        stats.SizeDistribution = sizeGroups.ToDictionary(g => g.Key, g => g.Count());
        
        return stats;
    }
}

/// <summary>
/// Result of Syncthing block calculation
/// </summary>
public class SyncthingBlocksResult
{
    public string FilePath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public int BlockSize { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public TimeSpan Duration { get; set; }
    
    public List<SyncthingBlockInfo> Blocks { get; set; } = new();
    public int TotalBlocks { get; set; }
    public int EmptyBlocks { get; set; }
    public int UniqueBlocks { get; set; }
    
    public string? Error { get; set; }
}

/// <summary>
/// Syncthing-compatible block information
/// </summary>
public class SyncthingBlockInfo
{
    public long Offset { get; set; }
    public int Size { get; set; }
    public byte[] Hash { get; set; } = Array.Empty<byte>();
    public string HashString { get; set; } = string.Empty;
    public bool IsEmpty { get; set; }
    public byte[]? Data { get; set; } // Optional: store block data for analysis
}

/// <summary>
/// Result of comparing two sets of blocks
/// </summary>
public class SyncthingBlockDiff
{
    public List<SyncthingBlockInfo> SourceBlocks { get; set; } = new();
    public List<SyncthingBlockInfo> TargetBlocks { get; set; } = new();
    
    public List<SyncthingBlockInfo> NeededBlocks { get; set; } = new();
    public List<SyncthingBlockInfo> ReusableBlocks { get; set; } = new();
    
    public long TotalBytesToTransfer { get; set; }
    public long TotalBytesReused { get; set; }
    public double TransferRatio { get; set; }
}

/// <summary>
/// Statistics about block distribution in a file
/// </summary>
public class BlockDistributionStats
{
    public int TotalBlocks { get; set; }
    public long TotalSize { get; set; }
    public int EmptyBlocks { get; set; }
    public long EmptySize { get; set; }
    public int UniqueBlocks { get; set; }
    public int DuplicateBlocks { get; set; }
    public Dictionary<int, int> SizeDistribution { get; set; } = new();
}