#pragma warning disable CS1998 // Async method lacks await (for placeholder methods)
using System.Collections.Concurrent;
using System.Diagnostics;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Transfer optimizer for efficient data synchronization
/// Implements Syncthing-compatible block transfer optimization with deduplication,
/// compression, and adaptive concurrency control
/// </summary>
public class TransferOptimizer
{
    private readonly ILogger<TransferOptimizer> _logger;
    private readonly BlockDuplicationDetector _duplicationDetector;
    private readonly ParallelBlockTransfer _blockTransfer;
    private readonly ISyncDatabase _database;
    
    // Optimization statistics
    private readonly ConcurrentDictionary<string, TransferStats> _deviceStats = new();
    private readonly TransferMetrics _globalMetrics = new();

    public TransferOptimizer(
        ILogger<TransferOptimizer> logger,
        BlockDuplicationDetector duplicationDetector,
        ParallelBlockTransfer blockTransfer,
        ISyncDatabase database)
    {
        _logger = logger;
        _duplicationDetector = duplicationDetector;
        _blockTransfer = blockTransfer;
        _database = database;
    }

    /// <summary>
    /// Optimizes file transfer using block-level deduplication
    /// Returns optimized transfer plan with minimal data transfer requirements
    /// </summary>
    public async Task<OptimizedTransferPlan> OptimizeFileTransferAsync(
        string deviceId,
        string folderId,
        string filePath,
        SyncFileInfo remoteFileInfo,
        TransferOptions? options = null)
    {
        options ??= new TransferOptions();
        
        _logger.LogDebug("Optimizing transfer for file {FilePath} from device {DeviceId}", 
            filePath, deviceId);

        var plan = new OptimizedTransferPlan
        {
            DeviceId = deviceId,
            FolderId = folderId,
            FilePath = filePath,
            RemoteFileInfo = remoteFileInfo,
            OptimizationStartTime = DateTime.UtcNow
        };

        try
        {
            // Step 1: Analyze local file for deduplication opportunities
            var localAnalysis = File.Exists(filePath) 
                ? await _duplicationDetector.AnalyzeFileAsync(filePath, folderId)
                : null;

            // Step 2: Create block transfer plan
            await CreateBlockTransferPlanAsync(plan, localAnalysis, options);

            // Step 3: Optimize block ordering for maximum efficiency
            OptimizeBlockOrdering(plan, options);

            // Step 4: Apply compression strategies
            await ApplyCompressionOptimizationAsync(plan, options);

            // Step 5: Calculate transfer metrics
            CalculateTransferMetrics(plan);

            plan.OptimizationEndTime = DateTime.UtcNow;
            plan.OptimizationDuration = plan.OptimizationEndTime - plan.OptimizationStartTime;

            _logger.LogInformation("Transfer optimization completed for {FilePath}: " +
                "{RequiredBlocks}/{TotalBlocks} blocks needed, {DataSaved:F1}MB saved ({SavingRatio:P2})",
                filePath, plan.RequiredBlocks.Count, plan.TotalBlocks, 
                plan.DataSavedBytes / (1024.0 * 1024.0), plan.DataSavingRatio);

            return plan;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error optimizing transfer for {FilePath}", filePath);
            plan.Error = ex.Message;
            return plan;
        }
    }

    /// <summary>
    /// Creates optimized block transfer plan based on deduplication analysis
    /// </summary>
    private async Task CreateBlockTransferPlanAsync(
        OptimizedTransferPlan plan,
        SyncthingDeduplicationResult? localAnalysis,
        TransferOptions options)
    {
        if (plan.RemoteFileInfo.Blocks == null || plan.RemoteFileInfo.Blocks.Count == 0)
        {
            // No blocks to transfer (empty file or metadata-only)
            return;
        }

        // Create lookup for local blocks if available
        var localBlockLookup = new Dictionary<string, SyncthingBlockInfo>();
        if (localAnalysis != null)
        {
            foreach (var block in localAnalysis.AllBlocks)
            {
                var hashString = block.Hash != null ? System.Text.Encoding.UTF8.GetString(block.Hash) : string.Empty;
                localBlockLookup[hashString] = block;
            }
        }

        // Analyze each remote block
        foreach (var remoteBlock in plan.RemoteFileInfo.Blocks)
        {
            var blockPlan = new BlockTransferPlan
            {
                RemoteBlock = new BepBlockInfo 
                { 
                    Offset = remoteBlock.Offset,
                    Size = remoteBlock.Size,
                    Hash = Convert.FromHexString(remoteBlock.Hash)
                },
                Offset = remoteBlock.Offset,
                Size = remoteBlock.Size,
                Hash = remoteBlock.Hash,
                WeakHash = remoteBlock.WeakHash
            };

            // Check if we have this block locally
            if (localBlockLookup.TryGetValue(remoteBlock.Hash, out var localBlock))
            {
                // Block available locally
                blockPlan.TransferType = BlockTransferType.LocalCopy;
                blockPlan.LocalBlock = new BlockInfo
                {
                    Offset = localBlock.Offset,
                    Size = localBlock.Size,
                    Hash = localBlock.HashString,
                    WeakHash = 0, // SyncthingBlockInfo doesn't have WeakHash
                    Data = localBlock.Data
                };
                blockPlan.EstimatedTransferTime = TimeSpan.Zero;
                plan.AvailableLocally.Add(blockPlan);
                
                _logger.LogTrace("Block {Hash} available locally at offset {Offset}",
                    remoteBlock.Hash, localBlock.Offset);
            }
            else
            {
                // Check if block exists in database (from other files)
                var hashBytes = System.Text.Encoding.UTF8.GetBytes(remoteBlock.Hash);
                var existingBlocks = await _database.BlockInfo.GetBlocksByStrongHashAsync(hashBytes);
                var existingBlock = existingBlocks.FirstOrDefault();

                if (existingBlock != null && existingBlock.IsLocal)
                {
                    // Block exists in database from another file
                    blockPlan.TransferType = BlockTransferType.DatabaseReference;
                    blockPlan.ExistingMetadata = existingBlock;
                    blockPlan.EstimatedTransferTime = TimeSpan.FromMilliseconds(1); // Minimal time for DB lookup
                    plan.AvailableFromDatabase.Add(blockPlan);
                    
                    _logger.LogTrace("Block {Hash} found in database with {RefCount} references",
                        remoteBlock.Hash, existingBlock.ReferenceCount);
                }
                else
                {
                    // Block needs to be transferred from remote
                    blockPlan.TransferType = BlockTransferType.RemoteDownload;
                    blockPlan.EstimatedTransferTime = EstimateBlockTransferTime(remoteBlock.Size, plan.DeviceId);
                    plan.RequiredBlocks.Add(blockPlan);
                    
                    _logger.LogTrace("Block {Hash} requires download from remote ({Size} bytes)",
                        remoteBlock.Hash, remoteBlock.Size);
                }
            }

            plan.AllBlocks.Add(blockPlan);
        }

        _logger.LogDebug("Block transfer plan: {LocalBlocks} local, {DatabaseBlocks} from database, {RemoteBlocks} from remote",
            plan.AvailableLocally.Count, plan.AvailableFromDatabase.Count, plan.RequiredBlocks.Count);
    }

    /// <summary>
    /// Optimizes block ordering for maximum transfer efficiency
    /// </summary>
    private void OptimizeBlockOrdering(OptimizedTransferPlan plan, TransferOptions options)
    {
        if (plan.RequiredBlocks.Count <= 1)
            return;

        _logger.LogDebug("Optimizing block ordering for {BlockCount} blocks", plan.RequiredBlocks.Count);

        switch (options.BlockOrderingStrategy)
        {
            case BlockOrderingStrategy.Sequential:
                // Keep original order (already sorted by offset)
                plan.RequiredBlocks = plan.RequiredBlocks
                    .OrderBy(b => b.Offset)
                    .ToList();
                break;

            case BlockOrderingStrategy.SizeOptimized:
                // Small blocks first for faster completion
                plan.RequiredBlocks = plan.RequiredBlocks
                    .OrderBy(b => b.Size)
                    .ThenBy(b => b.Offset)
                    .ToList();
                break;

            case BlockOrderingStrategy.PriorityBased:
                // Critical blocks first (beginning and end of file)
                plan.RequiredBlocks = plan.RequiredBlocks
                    .OrderBy(b => CalculateBlockPriority(b, plan.RemoteFileInfo.Size))
                    .ThenBy(b => b.Offset)
                    .ToList();
                break;

            case BlockOrderingStrategy.Adaptive:
            default:
                // Adaptive strategy based on device statistics
                var deviceStats = GetDeviceStats(plan.DeviceId);
                if (deviceStats.AverageLatency > TimeSpan.FromMilliseconds(100))
                {
                    // High latency - prioritize larger blocks
                    plan.RequiredBlocks = plan.RequiredBlocks
                        .OrderByDescending(b => b.Size)
                        .ThenBy(b => b.Offset)
                        .ToList();
                }
                else
                {
                    // Low latency - use size-optimized approach
                    plan.RequiredBlocks = plan.RequiredBlocks
                        .OrderBy(b => b.Size)
                        .ThenBy(b => b.Offset)
                        .ToList();
                }
                break;
        }

        _logger.LogDebug("Block ordering optimized using {Strategy} strategy", options.BlockOrderingStrategy);
    }

    /// <summary>
    /// Applies compression optimization based on file content analysis
    /// </summary>
    private async Task ApplyCompressionOptimizationAsync(OptimizedTransferPlan plan, TransferOptions options)
    {
        if (!options.EnableCompression)
            return;

        _logger.LogDebug("Analyzing compression opportunities for {BlockCount} blocks", plan.RequiredBlocks.Count);

        var compressibleBlocks = 0;
        var totalCompressedSize = 0L;

        foreach (var block in plan.RequiredBlocks)
        {
            // Estimate compression ratio based on file type and block characteristics
            var compressionRatio = EstimateCompressionRatio(plan.FilePath, block);
            
            if (compressionRatio > 0.1) // Only compress if we can save more than 10%
            {
                block.CompressionEnabled = true;
                block.EstimatedCompressedSize = (int)(block.Size * compressionRatio);
                totalCompressedSize += block.EstimatedCompressedSize;
                compressibleBlocks++;
            }
            else
            {
                block.CompressionEnabled = false;
                totalCompressedSize += block.Size;
            }
        }

        plan.CompressionEnabled = compressibleBlocks > 0;
        plan.EstimatedCompressedTransferSize = totalCompressedSize;
        
        if (compressibleBlocks > 0)
        {
            var originalSize = plan.RequiredBlocks.Sum(b => b.Size);
            var compressionSavings = originalSize - totalCompressedSize;
            var compressionRatio = (double)compressionSavings / originalSize;

            _logger.LogDebug("Compression optimization: {CompressibleBlocks}/{TotalBlocks} blocks compressible, " +
                "{CompressionSavings:F1}KB saved ({CompressionRatio:P2})",
                compressibleBlocks, plan.RequiredBlocks.Count, 
                compressionSavings / 1024.0, compressionRatio);
        }
    }

    /// <summary>
    /// Calculates comprehensive transfer metrics
    /// </summary>
    private void CalculateTransferMetrics(OptimizedTransferPlan plan)
    {
        // Original transfer size (without optimization)
        plan.OriginalTransferSize = plan.RemoteFileInfo.Size;

        // Optimized transfer size (only required blocks)
        plan.OptimizedTransferSize = plan.RequiredBlocks.Sum(b => b.Size);
        
        // Compressed transfer size (if compression enabled)
        plan.FinalTransferSize = plan.CompressionEnabled 
            ? plan.EstimatedCompressedTransferSize 
            : plan.OptimizedTransferSize;

        // Data savings from deduplication
        plan.DataSavedBytes = plan.OriginalTransferSize - plan.OptimizedTransferSize;
        plan.DataSavingRatio = plan.OriginalTransferSize > 0 
            ? (double)plan.DataSavedBytes / plan.OriginalTransferSize 
            : 0.0;

        // Compression savings
        plan.CompressionSavedBytes = plan.OptimizedTransferSize - plan.FinalTransferSize;
        plan.CompressionSavingRatio = plan.OptimizedTransferSize > 0
            ? (double)plan.CompressionSavedBytes / plan.OptimizedTransferSize
            : 0.0;

        // Total savings
        plan.TotalSavedBytes = plan.DataSavedBytes + plan.CompressionSavedBytes;
        plan.TotalSavingRatio = plan.OriginalTransferSize > 0
            ? (double)plan.TotalSavedBytes / plan.OriginalTransferSize
            : 0.0;

        // Estimated transfer time
        var deviceStats = GetDeviceStats(plan.DeviceId);
        var estimatedSeconds = plan.FinalTransferSize / Math.Max(deviceStats.AverageThroughput, 1024 * 1024); // Min 1MB/s
        plan.EstimatedTransferTime = TimeSpan.FromSeconds(estimatedSeconds);

        // Efficiency score (0-100)
        plan.EfficiencyScore = CalculateEfficiencyScore(plan);
    }

    /// <summary>
    /// Estimates block transfer time based on device performance
    /// </summary>
    private TimeSpan EstimateBlockTransferTime(int blockSize, string deviceId)
    {
        var deviceStats = GetDeviceStats(deviceId);
        var transferSeconds = blockSize / Math.Max(deviceStats.AverageThroughput, 1024 * 1024);
        var latencySeconds = deviceStats.AverageLatency.TotalSeconds;
        
        return TimeSpan.FromSeconds(transferSeconds + latencySeconds);
    }

    /// <summary>
    /// Calculates block priority for ordering optimization
    /// </summary>
    private int CalculateBlockPriority(BlockTransferPlan block, long totalFileSize)
    {
        // Higher priority (lower number) for:
        // 1. Beginning of file (first 10%)
        // 2. End of file (last 10%)
        // 3. Smaller blocks
        
        var relativePosition = (double)block.Offset / totalFileSize;
        
        if (relativePosition <= 0.1 || relativePosition >= 0.9)
        {
            return 1; // High priority
        }
        else if (block.Size < 64 * 1024) // Small blocks
        {
            return 2; // Medium priority
        }
        else
        {
            return 3; // Normal priority
        }
    }

    /// <summary>
    /// Estimates compression ratio for a block based on file type and content
    /// </summary>
    private double EstimateCompressionRatio(string filePath, BlockTransferPlan block)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        
        return extension switch
        {
            ".txt" or ".log" or ".xml" or ".json" or ".html" or ".css" or ".js" => 0.3, // Text files
            ".doc" or ".docx" or ".pdf" => 0.2, // Documents
            ".bmp" or ".wav" => 0.4, // Uncompressed media
            ".jpg" or ".png" or ".mp3" or ".mp4" or ".zip" or ".gz" => 0.05, // Already compressed
            ".exe" or ".dll" => 0.15, // Executables
            _ => 0.2 // Default compression ratio
        };
    }

    /// <summary>
    /// Gets or creates device transfer statistics
    /// </summary>
    private TransferStats GetDeviceStats(string deviceId)
    {
        return _deviceStats.GetOrAdd(deviceId, _ => new TransferStats
        {
            DeviceId = deviceId,
            AverageThroughput = 10 * 1024 * 1024, // 10 MB/s default
            AverageLatency = TimeSpan.FromMilliseconds(50), // 50ms default
            SuccessRate = 1.0
        });
    }

    /// <summary>
    /// Calculates efficiency score for the transfer plan
    /// </summary>
    private int CalculateEfficiencyScore(OptimizedTransferPlan plan)
    {
        var score = 50; // Base score
        
        // Deduplication bonus
        score += (int)(plan.DataSavingRatio * 30);
        
        // Compression bonus
        score += (int)(plan.CompressionSavingRatio * 15);
        
        // Block count penalty (too many small blocks)
        if (plan.RequiredBlocks.Count > 100)
        {
            score -= Math.Min(20, (plan.RequiredBlocks.Count - 100) / 10);
        }
        
        // Time efficiency bonus
        if (plan.EstimatedTransferTime < TimeSpan.FromMinutes(1))
        {
            score += 10;
        }
        
        return Math.Max(0, Math.Min(100, score));
    }

    /// <summary>
    /// Updates transfer statistics after successful transfer
    /// </summary>
    public void UpdateTransferStats(string deviceId, TimeSpan duration, long bytesTransferred, bool success)
    {
        var stats = GetDeviceStats(deviceId);
        
        stats.TotalTransfers++;
        if (success)
        {
            stats.SuccessfulTransfers++;
            stats.TotalBytesTransferred += bytesTransferred;
            stats.TotalTransferTime = stats.TotalTransferTime.Add(duration);
            
            // Update averages
            stats.AverageThroughput = stats.TotalBytesTransferred / Math.Max(1, stats.TotalTransferTime.TotalSeconds);
            stats.AverageLatency = TimeSpan.FromMilliseconds(Math.Min(stats.AverageLatency.TotalMilliseconds * 0.9 + duration.TotalMilliseconds * 0.1, 5000));
        }
        
        stats.SuccessRate = (double)stats.SuccessfulTransfers / stats.TotalTransfers;
        stats.LastUpdateTime = DateTime.UtcNow;
        
        _logger.LogDebug("Updated transfer stats for device {DeviceId}: {Throughput:F1} MB/s, {Latency}ms latency, {SuccessRate:P1} success rate",
            deviceId, stats.AverageThroughput / (1024 * 1024), stats.AverageLatency.TotalMilliseconds, stats.SuccessRate);
    }
    
    /// <summary>
    /// Executes an optimized transfer plan
    /// </summary>
    public async Task<OptimizedTransferResult> ExecuteTransferPlanAsync(
        string deviceId, 
        OptimizedTransferPlan plan, 
        string destinationPath, 
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new OptimizedTransferResult
        {
            FileName = Path.GetFileName(destinationPath)
        };

        try
        {
            _logger.LogInformation("Executing optimized transfer plan for {FileName}: {RequiredBlocks} blocks to transfer",
                result.FileName, plan.RequiredBlocks.Count);

            // Create directory if needed
            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write);
            
            var totalBlocks = plan.AllBlocks.Count;
            var transferredBlocks = 0;
            var dedupedBlocks = 0;
            var totalBytesTransferred = 0L;
            var totalBytesDeduped = 0L;

            // Process blocks in optimized order
            foreach (var blockPlan in plan.AllBlocks.OrderBy(b => b.Offset))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Set file position
                fileStream.Seek(blockPlan.Offset, SeekOrigin.Begin);
                
                switch (blockPlan.TransferType)
                {
                    case BlockTransferType.LocalCopy:
                        // Copy from existing local block
                        if (blockPlan.LocalBlock?.Data != null)
                        {
                            await fileStream.WriteAsync(blockPlan.LocalBlock.Data, cancellationToken);
                            totalBytesDeduped += blockPlan.Size;
                            dedupedBlocks++;
                        }
                        break;

                    case BlockTransferType.DatabaseReference:
                        // Use block from database
                        if (blockPlan.ExistingMetadata != null)
                        {
                            var hashString = blockPlan.ExistingMetadata.Hash != null ? 
                                System.Text.Encoding.UTF8.GetString(blockPlan.ExistingMetadata.Hash) : string.Empty;
                            var blockData = await GetBlockDataFromDatabaseAsync(hashString);
                            if (blockData != null)
                            {
                                await fileStream.WriteAsync(blockData, cancellationToken);
                                totalBytesDeduped += blockPlan.Size;
                                dedupedBlocks++;
                            }
                        }
                        break;

                    case BlockTransferType.RemoteDownload:
                        // Download from remote device
                        var remoteBlockData = await DownloadBlockFromRemoteAsync(
                            deviceId, plan.FolderId, blockPlan, cancellationToken);
                        if (remoteBlockData != null)
                        {
                            await fileStream.WriteAsync(remoteBlockData, cancellationToken);
                            totalBytesTransferred += blockPlan.Size;
                            transferredBlocks++;
                        }
                        break;
                }
            }

            result.Success = true;
            result.BytesTransferred = totalBytesTransferred;
            result.BytesDeduped = totalBytesDeduped;
            result.BlocksTransferred = transferredBlocks;
            result.BlocksDeduped = dedupedBlocks;
            
            // Update global metrics
            _globalMetrics.TotalFilesOptimized++;
            _globalMetrics.TotalBytesOptimized += totalBytesTransferred;
            _globalMetrics.TotalBytesSaved += totalBytesDeduped;
            
            _logger.LogInformation("Successfully executed transfer plan for {FileName}: " +
                                 "{TransferredBlocks}/{TotalBlocks} blocks transferred ({BytesTransferred} bytes), " +
                                 "{DedupedBlocks} blocks deduplicated ({BytesDeduped} bytes)",
                result.FileName, transferredBlocks, totalBlocks, totalBytesTransferred,
                dedupedBlocks, totalBytesDeduped);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = ex.Message;
            _logger.LogError(ex, "Error executing transfer plan for {FileName}", result.FileName);
        }
        finally
        {
            stopwatch.Stop();
            result.Duration = stopwatch.Elapsed;
            
            // Update device statistics
            UpdateDeviceStatsMethod(deviceId, result.BytesTransferred, stopwatch.Elapsed, result.Success);
        }

        return result;
    }
    
    private void UpdateDeviceStatsMethod(string deviceId, long bytesTransferred, TimeSpan duration, bool success)
    {
        var stats = GetOrCreateDeviceStats(deviceId);
        stats.RecordRequest(duration, 0, (int)bytesTransferred);
        
        _logger.LogTrace("Updated device stats for {DeviceId}: {BytesTransferred} bytes in {Duration}ms",
            deviceId, bytesTransferred, duration.TotalMilliseconds);
    }
    
    private TransferStats GetOrCreateDeviceStats(string deviceId)
    {
        return _deviceStats.GetOrAdd(deviceId, _ => new TransferStats { DeviceId = deviceId });
    }
    
    private async Task<byte[]?> GetBlockDataFromDatabaseAsync(string blockHash)
    {
        try
        {
            var hashBytes = System.Text.Encoding.UTF8.GetBytes(blockHash);
            var blockInfo = await _database.BlockInfo.GetByHashAsync(hashBytes);
            return blockInfo?.Data;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error retrieving block data from database for hash {BlockHash}", blockHash);
            return null;
        }
    }
    
    private async Task<byte[]?> DownloadBlockFromRemoteAsync(
        string deviceId, 
        string folderId, 
        BlockTransferPlan blockPlan, 
        CancellationToken cancellationToken)
    {
        try
        {
            // This would normally use the protocol to request the block
            // For now, simulate block download
            _logger.LogTrace("Downloading block {Hash} from device {DeviceId}", blockPlan.Hash, deviceId);
            
            // TODO: Implement actual block download using ISyncProtocol
            await Task.Delay(50, cancellationToken); // Simulate network delay
            
            // Return mock data for now
            return new byte[blockPlan.Size];
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading block {Hash} from device {DeviceId}", blockPlan.Hash, deviceId);
            return null;
        }
    }
}

/// <summary>
/// Optimized transfer plan with deduplication and compression
/// </summary>
public class OptimizedTransferPlan
{
    public string DeviceId { get; set; } = string.Empty;
    public string FolderId { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public SyncFileInfo RemoteFileInfo { get; set; } = new("", "", "", 0, DateTime.UtcNow);
    
    public DateTime OptimizationStartTime { get; set; }
    public DateTime OptimizationEndTime { get; set; }
    public TimeSpan OptimizationDuration { get; set; }
    
    // Block categorization
    public List<BlockTransferPlan> AllBlocks { get; set; } = new();
    public List<BlockTransferPlan> RequiredBlocks { get; set; } = new(); // Need to download
    public List<BlockTransferPlan> AvailableLocally { get; set; } = new(); // Available locally
    public List<BlockTransferPlan> AvailableFromDatabase { get; set; } = new(); // Available from DB
    
    public int TotalBlocks => AllBlocks.Count;
    public int DeduplicatedBlocks => AvailableLocally.Count + AvailableFromDatabase.Count;
    
    // Transfer metrics
    public long OriginalTransferSize { get; set; }
    public long OptimizedTransferSize { get; set; }
    public long EstimatedTransferSize => CompressionEnabled ? EstimatedCompressedTransferSize : OptimizedTransferSize;
    public long FinalTransferSize { get; set; }
    public long EstimatedCompressedTransferSize { get; set; }
    
    public long DataSavedBytes { get; set; }
    public double DataSavingRatio { get; set; }
    public long CompressionSavedBytes { get; set; }
    public double CompressionSavingRatio { get; set; }
    public long TotalSavedBytes { get; set; }
    public double TotalSavingRatio { get; set; }
    
    public TimeSpan EstimatedTransferTime { get; set; }
    public int EfficiencyScore { get; set; } // 0-100
    
    public bool CompressionEnabled { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Block transfer plan with optimization details
/// </summary>
public class BlockTransferPlan
{
    public BepBlockInfo RemoteBlock { get; set; } = new();
    public BlockInfo? LocalBlock { get; set; }
    public BlockMetadata? ExistingMetadata { get; set; }
    
    public long Offset { get; set; }
    public int Size { get; set; }
    public string Hash { get; set; } = string.Empty;
    public uint WeakHash { get; set; }
    
    public BlockTransferType TransferType { get; set; }
    public TimeSpan EstimatedTransferTime { get; set; }
    
    public bool CompressionEnabled { get; set; }
    public int EstimatedCompressedSize { get; set; }
}

/// <summary>
/// Types of block transfer strategies
/// </summary>
public enum BlockTransferType
{
    LocalCopy,        // Copy from local file
    DatabaseReference, // Reference existing block in database
    RemoteDownload    // Download from remote device
}

/// <summary>
/// Transfer optimization options
/// </summary>
public class TransferOptions
{
    public bool EnableCompression { get; set; } = true;
    public bool EnableDeduplication { get; set; } = true;
    public bool EnableEncryption { get; set; } = false;
    public BlockOrderingStrategy BlockOrderingStrategy { get; set; } = BlockOrderingStrategy.Adaptive;
    public int MaxConcurrentBlocks { get; set; } = 10;
    public int BlockSize { get; set; } = 128 * 1024; // 128KB default
    public bool UseAdaptiveStrategy { get; set; } = true;
    public TimeSpan MaxTransferTime { get; set; } = TimeSpan.FromMinutes(30);
}

/// <summary>
/// Block ordering strategies for optimization
/// </summary>
public enum BlockOrderingStrategy
{
    Sequential,    // Keep original order
    SizeOptimized, // Small blocks first
    PriorityBased, // Critical blocks first
    Adaptive       // Adapt based on device performance
}

/// <summary>
/// Transfer statistics for a device
/// </summary>
public class TransferStats
{
    public string DeviceId { get; set; } = string.Empty;
    public double AverageThroughput { get; set; } // bytes per second
    public TimeSpan AverageLatency { get; set; }
    public double SuccessRate { get; set; }
    
    public int TotalTransfers { get; set; }
    public int SuccessfulTransfers { get; set; }
    public long TotalBytesTransferred { get; set; }
    public TimeSpan TotalTransferTime { get; set; }
    public DateTime LastUpdateTime { get; set; }
    
    /// <summary>
    /// Records a transfer request for metrics tracking
    /// </summary>
    public void RecordRequest(TimeSpan duration, int requestBytes, int responseBytes)
    {
        TotalTransfers++;
        SuccessfulTransfers++; // Assuming success if we're recording
        TotalBytesTransferred += responseBytes;
        TotalTransferTime += duration;
        
        // Update averages
        AverageLatency = TimeSpan.FromMilliseconds(TotalTransferTime.TotalMilliseconds / TotalTransfers);
        AverageThroughput = TotalTransferTime.TotalSeconds > 0 ? TotalBytesTransferred / TotalTransferTime.TotalSeconds : 0;
        SuccessRate = TotalTransfers > 0 ? (double)SuccessfulTransfers / TotalTransfers : 0;
        
        LastUpdateTime = DateTime.UtcNow;
    }
}

/// <summary>
/// Global transfer metrics
/// </summary>
public class TransferMetrics
{
    public long TotalFilesOptimized { get; set; }
    public long TotalBytesOptimized { get; set; }
    public long TotalBytesSaved { get; set; }
    public double AverageDeduplicationRatio { get; set; }
    public double AverageCompressionRatio { get; set; }
    public TimeSpan TotalOptimizationTime { get; set; }
}

/// <summary>
/// Result of optimized transfer execution
/// </summary>
public class OptimizedTransferResult
{
    public bool Success { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string? Error { get; set; }
    public long BytesTransferred { get; set; }
    public long BytesDeduped { get; set; }
    public double OptimizationRatio => BytesTransferred + BytesDeduped > 0 ? 
        (double)BytesDeduped / (BytesTransferred + BytesDeduped) * 100 : 0;
    public TimeSpan Duration { get; set; }
    public int BlocksTransferred { get; set; }
    public int BlocksDeduped { get; set; }
}