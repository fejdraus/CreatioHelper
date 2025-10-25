using System.Collections.Concurrent;
using System.Security.Cryptography;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Syncthing-compatible block duplication detector for file synchronization
/// Implements position-based block matching with SHA-256 hashing
/// Simplified approach without rolling hashes for better performance and reliability
/// Based on Syncthing v2.0+ architecture
/// </summary>
public class BlockDuplicationDetector
{
    private readonly ILogger<BlockDuplicationDetector> _logger;
    private readonly IBlockInfoRepository _blockRepository;
    private readonly SyncthingBlockCalculator _blockCalculator;
    
    // In-memory cache for block metadata (by SHA-256 hash)
    private readonly ConcurrentDictionary<string, BlockMetadata> _blockCache = new();
    
    // Statistics tracking
    private long _totalBlocksProcessed = 0;
    private long _totalBytesDeduped = 0;
    
    public BlockDuplicationDetector(
        ILogger<BlockDuplicationDetector> logger,
        IBlockInfoRepository blockRepository,
        SyncthingBlockCalculator blockCalculator)
    {
        _logger = logger;
        _blockRepository = blockRepository;
        _blockCalculator = blockCalculator;
    }

    /// <summary>
    /// Analyzes file using Syncthing's block-level deduplication approach
    /// Returns deduplication information for bandwidth optimization
    /// </summary>
    public async Task<SyncthingDeduplicationResult> AnalyzeFileAsync(string filePath, string folderId, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting Syncthing-style deduplication analysis for {FilePath}", filePath);
        
        var result = new SyncthingDeduplicationResult
        {
            FilePath = filePath,
            FolderId = folderId,
            AnalysisStartTime = DateTime.UtcNow
        };

        try
        {
            // Calculate file blocks using Syncthing algorithm
            var blocksResult = await _blockCalculator.CalculateFileBlocksAsync(filePath, cancellationToken);
            if (blocksResult.Error != null)
            {
                result.Error = blocksResult.Error;
                return result;
            }

            result.FileSize = blocksResult.FileSize;
            result.BlockSize = blocksResult.BlockSize;
            result.TotalBlocks = blocksResult.TotalBlocks;
            result.EmptyBlocks = blocksResult.EmptyBlocks;
            result.Blocks = blocksResult.Blocks;

            // Find existing blocks in database for deduplication
            await FindExistingBlocksAsync(result, cancellationToken);

            // Store new blocks in database
            await StoreNewBlocksAsync(result, folderId, cancellationToken);

            // Calculate deduplication statistics
            CalculateDeduplicationStats(result);

            result.AnalysisEndTime = DateTime.UtcNow;
            result.AnalysisDuration = result.AnalysisEndTime - result.AnalysisStartTime;

            // Update statistics
            _totalBlocksProcessed += result.TotalBlocks;
            _totalBytesDeduped += result.BytesDeduped;

            _logger.LogInformation("Syncthing deduplication analysis completed for {FilePath}: {NewBlocks}/{TotalBlocks} new blocks, {BytesDeduped} bytes deduped ({DeduplicationRatio:P1}) in {Duration}ms",
                filePath, result.NewBlocks, result.TotalBlocks, result.BytesDeduped, result.DeduplicationRatio, result.AnalysisDuration.TotalMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error analyzing file {FilePath}", filePath);
            result.Error = ex.Message;
            result.AnalysisEndTime = DateTime.UtcNow;
            result.AnalysisDuration = result.AnalysisEndTime - result.AnalysisStartTime;
            return result;
        }
    }

    /// <summary>
    /// Find existing blocks in database for deduplication using SHA-256 hash lookups
    /// </summary>
    private async Task FindExistingBlocksAsync(SyncthingDeduplicationResult result, CancellationToken cancellationToken)
    {
        var existingCount = 0;
        var newCount = 0;

        foreach (var block in result.Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check cache first
            if (_blockCache.TryGetValue(block.HashString, out var cachedBlock))
            {
                result.ExistingBlocks.Add(block);
                existingCount++;
                continue;
            }

            // Check database
            var existingBlock = await _blockRepository.GetAsync(block.Hash);
            if (existingBlock != null)
            {
                result.ExistingBlocks.Add(block);
                _blockCache.TryAdd(block.HashString, existingBlock);
                existingCount++;
                
                _logger.LogTrace("Found existing block {BlockHash} at offset {Offset}", 
                    block.HashString[..8], block.Offset);
            }
            else
            {
                result.NewBlocks.Add(block);
                newCount++;
            }
        }

        _logger.LogDebug("Block lookup completed: {ExistingBlocks} existing, {NewBlocks} new blocks", 
            existingCount, newCount);
    }

    /// <summary>
    /// Store new blocks in database for future deduplication
    /// </summary>
    private async Task StoreNewBlocksAsync(SyncthingDeduplicationResult result, string folderId, CancellationToken cancellationToken)
    {
        if (!result.NewBlocks.Any())
        {
            _logger.LogDebug("No new blocks to store for file {FilePath}", result.FilePath);
            return;
        }

        var storedCount = 0;
        foreach (var block in result.NewBlocks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var blockMetadata = new BlockMetadata
                {
                    Hash = block.Hash,
                    Size = block.Size,
                    DeviceIdx = 1, // Local device
                    FolderId = folderId,
                    FileName = Path.GetFileName(result.FilePath),
                    BlockIndex = result.Blocks.IndexOf(block),
                    CreatedAt = DateTime.UtcNow,
                    LastAccessed = DateTime.UtcNow
                };

                await _blockRepository.SaveAsync(blockMetadata);
                _blockCache.TryAdd(block.HashString, blockMetadata);
                storedCount++;
                
                _logger.LogTrace("Stored new block {BlockHash} (size: {Size})", 
                    block.HashString[..8], block.Size);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to store block {BlockHash}", block.HashString[..8]);
            }
        }

        _logger.LogDebug("Stored {StoredCount} new blocks in database", storedCount);
    }

    /// <summary>
    /// Calculate deduplication statistics for the analyzed file
    /// </summary>
    private void CalculateDeduplicationStats(SyncthingDeduplicationResult result)
    {
        if (result.TotalBlocks == 0)
        {
            result.DeduplicationRatio = 0.0;
            result.BytesDeduped = 0;
            result.TransferSavings = 0;
            return;
        }

        // Calculate bytes saved by deduplication (existing blocks don't need transfer)
        result.BytesDeduped = result.ExistingBlocks.Sum(b => (long)b.Size);
        
        // Calculate deduplication ratio
        result.DeduplicationRatio = (double)result.BytesDeduped / result.FileSize;
        
        // Calculate transfer savings (bytes that don't need to be sent)
        result.TransferSavings = result.BytesDeduped;
        result.TransferEfficiency = result.DeduplicationRatio;
        
        // Calculate unique block ratio
        var uniqueHashes = result.Blocks.Select(b => b.HashString).Distinct().Count();
        result.UniqueBlockRatio = (double)uniqueHashes / result.TotalBlocks;
        
        _logger.LogDebug("Deduplication stats: {BytesDeduped}/{FileSize} bytes deduped ({DeduplicationRatio:P1}), {UniqueBlocks}/{TotalBlocks} unique blocks",
            result.BytesDeduped, result.FileSize, result.DeduplicationRatio, uniqueHashes, result.TotalBlocks);
    }

    /// <summary>
    /// Compare two files for block-level differences using Syncthing's position-based approach
    /// </summary>
    public async Task<SyncthingFileDiff> CompareFilesAsync(string sourceFilePath, string targetFilePath, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Comparing files for block differences: {SourceFile} vs {TargetFile}", sourceFilePath, targetFilePath);
        
        var diff = new SyncthingFileDiff
        {
            SourceFilePath = sourceFilePath,
            TargetFilePath = targetFilePath,
            ComparisonStartTime = DateTime.UtcNow
        };
        
        try
        {
            // Calculate blocks for both files
            var sourceBlocksTask = _blockCalculator.CalculateFileBlocksAsync(sourceFilePath, cancellationToken);
            var targetBlocksTask = _blockCalculator.CalculateFileBlocksAsync(targetFilePath, cancellationToken);
            
            await Task.WhenAll(sourceBlocksTask, targetBlocksTask);
            
            var sourceBlocks = sourceBlocksTask.Result;
            var targetBlocks = targetBlocksTask.Result;
            
            if (sourceBlocks.Error != null)
            {
                diff.Error = $"Source file error: {sourceBlocks.Error}";
                return diff;
            }
            
            if (targetBlocks.Error != null)
            {
                diff.Error = $"Target file error: {targetBlocks.Error}";
                return diff;
            }
            
            // Compare blocks using Syncthing algorithm
            var blockDiff = _blockCalculator.CompareBlocks(sourceBlocks.Blocks, targetBlocks.Blocks);
            
            diff.SourceBlocks = sourceBlocks.Blocks;
            diff.TargetBlocks = targetBlocks.Blocks;
            diff.BlocksToTransfer = blockDiff.NeededBlocks;
            diff.ReusableBlocks = blockDiff.ReusableBlocks;
            diff.TotalBytesToTransfer = blockDiff.TotalBytesToTransfer;
            diff.TotalBytesReused = blockDiff.TotalBytesReused;
            diff.TransferRatio = blockDiff.TransferRatio;
            
            diff.ComparisonEndTime = DateTime.UtcNow;
            diff.ComparisonDuration = diff.ComparisonEndTime - diff.ComparisonStartTime;
            
            _logger.LogInformation("File comparison completed: {BlocksToTransfer}/{TotalBlocks} blocks to transfer ({TransferBytes} bytes, {TransferRatio:P1}) in {Duration}ms",
                diff.BlocksToTransfer.Count, diff.TargetBlocks.Count, diff.TotalBytesToTransfer, diff.TransferRatio, diff.ComparisonDuration.TotalMilliseconds);
            
            return diff;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error comparing files {SourceFile} and {TargetFile}", sourceFilePath, targetFilePath);
            diff.Error = ex.Message;
            diff.ComparisonEndTime = DateTime.UtcNow;
            diff.ComparisonDuration = diff.ComparisonEndTime - diff.ComparisonStartTime;
            return diff;
        }
    }

    /// <summary>
    /// Get deduplication statistics for the detector
    /// </summary>
    public DeduplicationStatistics GetStatistics()
    {
        return new DeduplicationStatistics
        {
            TotalBlocksProcessed = _totalBlocksProcessed,
            TotalBytesDeduped = _totalBytesDeduped,
            CacheSize = _blockCache.Count,
            CacheHitRatio = _blockCache.Count > 0 ? 1.0 : 0.0 // Simplified calculation
        };
    }




    /// <summary>
    /// Clear deduplication caches and reset statistics
    /// </summary>
    public void ClearCaches()
    {
        _blockCache.Clear();
        _totalBlocksProcessed = 0;
        _totalBytesDeduped = 0;
        
        _logger.LogDebug("Deduplication caches cleared and statistics reset");
    }
}

/// <summary>
/// Result of Syncthing-style deduplication analysis
/// </summary>
public class SyncthingDeduplicationResult
{
    public string FilePath { get; set; } = string.Empty;
    public string FolderId { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public int BlockSize { get; set; }
    public DateTime AnalysisStartTime { get; set; }
    public DateTime AnalysisEndTime { get; set; }
    public TimeSpan AnalysisDuration { get; set; }
    
    public List<SyncthingBlockInfo> Blocks { get; set; } = new();
    public List<SyncthingBlockInfo> ExistingBlocks { get; set; } = new();
    public List<SyncthingBlockInfo> NewBlocks { get; set; } = new();
    public List<SyncthingBlockInfo> AllBlocks { get; set; } = new();
    
    public int TotalBlocks { get; set; }
    public int EmptyBlocks { get; set; }
    public long BytesDeduped { get; set; }
    public double DeduplicationRatio { get; set; }
    public long TransferSavings { get; set; }
    public double TransferEfficiency { get; set; }
    public double UniqueBlockRatio { get; set; }
    
    public string? Error { get; set; }
}

/// <summary>
/// Result of comparing two files for block-level differences
/// </summary>
public class SyncthingFileDiff
{
    public string SourceFilePath { get; set; } = string.Empty;
    public string TargetFilePath { get; set; } = string.Empty;
    public DateTime ComparisonStartTime { get; set; }
    public DateTime ComparisonEndTime { get; set; }
    public TimeSpan ComparisonDuration { get; set; }
    
    public List<SyncthingBlockInfo> SourceBlocks { get; set; } = new();
    public List<SyncthingBlockInfo> TargetBlocks { get; set; } = new();
    public List<SyncthingBlockInfo> BlocksToTransfer { get; set; } = new();
    public List<SyncthingBlockInfo> ReusableBlocks { get; set; } = new();
    
    public long TotalBytesToTransfer { get; set; }
    public long TotalBytesReused { get; set; }
    public double TransferRatio { get; set; }
    
    public string? Error { get; set; }
}

/// <summary>
/// Deduplication statistics for the block detector
/// </summary>
public class DeduplicationStatistics
{
    public long TotalBlocksProcessed { get; set; }
    public long TotalBytesDeduped { get; set; }
    public int CacheSize { get; set; }
    public double CacheHitRatio { get; set; }
    public DateTime LastReset { get; set; } = DateTime.UtcNow;
}


