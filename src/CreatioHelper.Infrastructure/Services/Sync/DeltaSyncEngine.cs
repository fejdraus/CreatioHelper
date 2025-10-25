using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Delta sync engine for efficient block-level file synchronization
/// Based on Syncthing's block exchange algorithm using weak/strong hash comparison
/// </summary>
public class DeltaSyncEngine
{
    private readonly ILogger<DeltaSyncEngine> _logger;
    private readonly AdaptiveBlockSizer _blockSizer;

    public DeltaSyncEngine(ILogger<DeltaSyncEngine> logger, AdaptiveBlockSizer blockSizer)
    {
        _logger = logger;
        _blockSizer = blockSizer;
    }

    /// <summary>
    /// Compares local and remote file blocks to determine which blocks need to be transferred
    /// </summary>
    /// <param name="localFile">Local file information with blocks</param>
    /// <param name="remoteFile">Remote file information with blocks</param>
    /// <returns>Delta sync plan with blocks to request/send</returns>
    public DeltaSyncPlan CreateSyncPlan(SyncFileInfo localFile, SyncFileInfo remoteFile)
    {
        var plan = new DeltaSyncPlan
        {
            LocalFile = localFile,
            RemoteFile = remoteFile,
            RequiredBlocks = new List<BlockRequest>(),
            AvailableBlocks = new List<BlockInfo>(),
            TransferredBytes = 0
        };

        if (localFile.Hash == remoteFile.Hash)
        {
            _logger.LogDebug("Files are identical: {FileName}", localFile.Name);
            plan.IsSynchronized = true;
            return plan;
        }

        // Create lookup table for local blocks by weak hash for fast comparison
        var localBlocksByWeakHash = CreateWeakHashLookup(localFile.Blocks);

        // Analyze remote blocks to determine what we need
        foreach (var remoteBlock in remoteFile.Blocks)
        {
            var matchingLocalBlocks = FindMatchingBlocks(remoteBlock, localBlocksByWeakHash);
            
            if (matchingLocalBlocks.Any())
            {
                // We have this block locally - no need to transfer
                var bestMatch = SelectBestMatch(remoteBlock, matchingLocalBlocks);
                plan.AvailableBlocks.Add(bestMatch);
                
                _logger.LogTrace("Block at offset {Offset} found locally (weak hash: {WeakHash})", 
                    remoteBlock.Offset, WeakHashCalculator.FormatAdler32((uint)remoteBlock.WeakHash));
            }
            else
            {
                // We need to request this block from remote
                plan.RequiredBlocks.Add(new BlockRequest
                {
                    Offset = remoteBlock.Offset,
                    Size = remoteBlock.Size,
                    Hash = remoteBlock.Hash,
                    WeakHash = (int)remoteBlock.WeakHash
                });
                
                plan.TransferredBytes += remoteBlock.Size;
                
                _logger.LogTrace("Block at offset {Offset} needs to be transferred ({Size} bytes)", 
                    remoteBlock.Offset, remoteBlock.Size);
            }
        }

        // Calculate transfer efficiency
        var totalFileSize = remoteFile.Size;
        var transferPercentage = totalFileSize > 0 ? (plan.TransferredBytes * 100.0 / totalFileSize) : 0;
        
        _logger.LogInformation("Delta sync plan for {FileName}: {RequiredBlocks} blocks to transfer " +
                             "({TransferredBytes} bytes, {TransferPercentage:F1}% of file)",
            remoteFile.Name, plan.RequiredBlocks.Count, plan.TransferredBytes, transferPercentage);

        return plan;
    }

    /// <summary>
    /// Finds blocks that may have been moved within the file using rolling hash
    /// This is an advanced optimization similar to Syncthing's rsync-style algorithm
    /// </summary>
    /// <param name="localFile">Local file information</param>
    /// <param name="remoteFile">Remote file information</param>
    /// <param name="windowSize">Rolling hash window size</param>
    /// <returns>Enhanced delta sync plan with moved block detection</returns>
    public DeltaSyncPlan CreateAdvancedSyncPlan(SyncFileInfo localFile, SyncFileInfo remoteFile, int? windowSize = null)
    {
        var basicPlan = CreateSyncPlan(localFile, remoteFile);
        
        if (basicPlan.IsSynchronized || basicPlan.RequiredBlocks.Count == 0)
        {
            return basicPlan;
        }

        // Use adaptive block size if window size not specified
        var actualWindowSize = windowSize ?? _blockSizer.CalculateBlockSize(remoteFile.Size, useLargeBlocks: true);
        
        _logger.LogDebug("Running advanced sync plan with rolling hash (window size: {WindowSize})", 
            AdaptiveBlockSizer.FormatBlockSize(actualWindowSize));

        // Try to find moved blocks using rolling hash
        var movedBlocks = FindMovedBlocks(localFile, basicPlan.RequiredBlocks, actualWindowSize);
        
        if (movedBlocks.Any())
        {
            // Update plan to reflect found moved blocks
            foreach (var movedBlock in movedBlocks)
            {
                basicPlan.RequiredBlocks.RemoveAll(r => r.Hash == movedBlock.Hash);
                basicPlan.AvailableBlocks.Add(movedBlock);
                basicPlan.TransferredBytes -= movedBlock.Size;
            }
            
            _logger.LogInformation("Found {MovedBlockCount} moved blocks, reducing transfer to {TransferredBytes} bytes",
                movedBlocks.Count, basicPlan.TransferredBytes);
        }

        return basicPlan;
    }

    /// <summary>
    /// Validates that a received block matches the expected hash
    /// </summary>
    /// <param name="blockData">Block data to validate</param>
    /// <param name="expectedStrongHash">Expected SHA-256 hash</param>
    /// <param name="expectedWeakHash">Expected Adler-32 hash</param>
    /// <returns>True if block is valid</returns>
    public bool ValidateBlock(byte[] blockData, string expectedStrongHash, int expectedWeakHash)
    {
        try
        {
            // First check weak hash (faster)
            var actualWeakHash = WeakHashCalculator.CalculateAdler32(blockData);
            if (actualWeakHash != (uint)expectedWeakHash)
            {
                _logger.LogWarning("Block weak hash mismatch: expected {Expected}, got {Actual}",
                    WeakHashCalculator.FormatAdler32((uint)expectedWeakHash),
                    WeakHashCalculator.FormatAdler32(actualWeakHash));
                return false;
            }

            // Then check strong hash (slower but more reliable)
            var (_, actualStrongHash) = WeakHashCalculator.CalculateBlockHashes(blockData);
            if (!string.Equals(actualStrongHash, expectedStrongHash, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Block strong hash mismatch: expected {Expected}, got {Actual}",
                    expectedStrongHash, actualStrongHash);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating block hash");
            return false;
        }
    }

    /// <summary>
    /// Creates a lookup table for blocks indexed by weak hash
    /// </summary>
    private Dictionary<uint, List<BlockInfo>> CreateWeakHashLookup(List<BlockInfo> blocks)
    {
        var lookup = new Dictionary<uint, List<BlockInfo>>();
        
        foreach (var block in blocks)
        {
            var weakHash = (uint)block.WeakHash;
            if (!lookup.ContainsKey(weakHash))
            {
                lookup[weakHash] = new List<BlockInfo>();
            }
            lookup[weakHash].Add(block);
        }
        
        return lookup;
    }

    /// <summary>
    /// Finds local blocks that match a remote block by weak hash
    /// </summary>
    private List<BlockInfo> FindMatchingBlocks(BlockInfo remoteBlock, Dictionary<uint, List<BlockInfo>> localBlocksByWeakHash)
    {
        var weakHash = (uint)remoteBlock.WeakHash;
        
        if (localBlocksByWeakHash.TryGetValue(weakHash, out var candidates))
        {
            // Further filter by strong hash to avoid false positives
            return candidates.Where(b => string.Equals(b.Hash, remoteBlock.Hash, StringComparison.OrdinalIgnoreCase))
                          .ToList();
        }
        
        return new List<BlockInfo>();
    }

    /// <summary>
    /// Selects the best matching block when multiple candidates exist
    /// </summary>
    private BlockInfo SelectBestMatch(BlockInfo remoteBlock, List<BlockInfo> candidates)
    {
        // For now, just return the first match
        // In a more sophisticated implementation, we could prefer blocks at similar offsets
        return candidates.First();
    }

    /// <summary>
    /// Uses rolling hash to find blocks that may have moved within the file
    /// </summary>
    private List<BlockInfo> FindMovedBlocks(SyncFileInfo localFile, List<BlockRequest> missingBlocks, int windowSize)
    {
        var movedBlocks = new List<BlockInfo>();
        
        if (missingBlocks.Count == 0)
        {
            return movedBlocks;
        }

        // For this simplified implementation, construct file path from folder and relative path
        // In a real implementation, this should be available from the SyncFileInfo
        var filePath = Path.Combine(localFile.FolderId, localFile.RelativePath);
        if (!File.Exists(filePath))
        {
            return movedBlocks;
        }

        try
        {
            // Create lookup table for missing blocks by weak hash
            var missingBlocksByWeakHash = new Dictionary<uint, List<BlockRequest>>();
            foreach (var block in missingBlocks)
            {
                var weakHash = (uint)block.WeakHash;
                if (!missingBlocksByWeakHash.ContainsKey(weakHash))
                {
                    missingBlocksByWeakHash[weakHash] = new List<BlockRequest>();
                }
                missingBlocksByWeakHash[weakHash].Add(block);
            }

            _logger.LogDebug("Scanning file {FileName} for {MissingBlockCount} missing blocks using rolling hash (window: {WindowSize})",
                localFile.Name, missingBlocks.Count, AdaptiveBlockSizer.FormatBlockSize(windowSize));

            using var fileStream = File.OpenRead(filePath);
            var buffer = new byte[windowSize];
            
            // Read first window
            var bytesRead = fileStream.Read(buffer, 0, windowSize);
            if (bytesRead < windowSize)
            {
                return movedBlocks; // File too small for rolling hash
            }

            // Calculate initial hash for first window
            uint currentHash = WeakHashCalculator.CalculateAdler32(buffer, 0, windowSize);
            CheckForBlockMatch(buffer, 0, windowSize, currentHash, missingBlocksByWeakHash, movedBlocks);

            // Roll through the rest of the file
            long currentOffset = windowSize;
            int nextByte;
            
            while ((nextByte = fileStream.ReadByte()) != -1)
            {
                // Remove the oldest byte and add the new byte
                byte oldByte = buffer[currentOffset % windowSize];
                byte newByte = (byte)nextByte;
                
                // Update buffer
                buffer[currentOffset % windowSize] = newByte;
                
                // Update rolling hash
                currentHash = WeakHashCalculator.RollingAdler32(currentHash, oldByte, newByte, windowSize);
                
                // Check if this window matches any missing block
                var windowStart = currentOffset - windowSize + 1;
                CheckForBlockMatch(buffer, windowStart, windowSize, currentHash, missingBlocksByWeakHash, movedBlocks);
                
                currentOffset++;
                
                // Early exit if we found all missing blocks
                if (movedBlocks.Count >= missingBlocks.Count)
                {
                    break;
                }
            }

            _logger.LogInformation("Rolling hash found {MovedBlockCount} moved blocks in {FileName}",
                movedBlocks.Count, localFile.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during rolling hash search for moved blocks in {FileName}", localFile.Name);
        }
        
        return movedBlocks;
    }

    /// <summary>
    /// Checks if the current window matches any missing block
    /// </summary>
    private void CheckForBlockMatch(byte[] buffer, long windowOffset, int windowSize, uint currentHash, 
        Dictionary<uint, List<BlockRequest>> missingBlocksByWeakHash, List<BlockInfo> movedBlocks)
    {
        if (missingBlocksByWeakHash.TryGetValue(currentHash, out var candidates))
        {
            // Extract the current window data
            var windowData = new byte[windowSize];
            Array.Copy(buffer, 0, windowData, 0, windowSize);
            
            // Calculate strong hash for verification
            var (_, strongHash) = WeakHashCalculator.CalculateBlockHashes(windowData);
            
            // Check if any candidate matches the strong hash
            foreach (var candidate in candidates)
            {
                if (string.Equals(candidate.Hash, strongHash, StringComparison.OrdinalIgnoreCase))
                {
                    // Found a moved block!
                    var movedBlock = new BlockInfo(windowOffset, windowSize, strongHash, currentHash);
                    movedBlocks.Add(movedBlock);
                    
                    _logger.LogTrace("Found moved block at new offset {NewOffset} (original offset: {OriginalOffset}, hash: {Hash})",
                        windowOffset, candidate.Offset, strongHash[..8] + "...");
                    
                    // Remove this candidate to avoid duplicate matches
                    candidates.Remove(candidate);
                    if (candidates.Count == 0)
                    {
                        missingBlocksByWeakHash.Remove(currentHash);
                    }
                    
                    break;
                }
            }
        }
    }
}

/// <summary>
/// Represents a plan for delta synchronization
/// </summary>
public class DeltaSyncPlan
{
    public SyncFileInfo LocalFile { get; set; } = new("", "", "", 0, DateTime.MinValue);
    public SyncFileInfo RemoteFile { get; set; } = new("", "", "", 0, DateTime.MinValue);
    public List<BlockRequest> RequiredBlocks { get; set; } = new();
    public List<BlockInfo> AvailableBlocks { get; set; } = new();
    public long TransferredBytes { get; set; }
    public bool IsSynchronized { get; set; }
}

/// <summary>
/// Represents a request for a specific block
/// </summary>
public class BlockRequest
{
    public long Offset { get; set; }
    public int Size { get; set; }
    public string Hash { get; set; } = string.Empty;
    public int WeakHash { get; set; }
}