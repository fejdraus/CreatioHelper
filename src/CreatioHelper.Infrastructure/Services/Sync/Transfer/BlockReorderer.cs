using CreatioHelper.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Transfer;

/// <summary>
/// Reorders blocks for download based on configured block pull order.
/// Supports Syncthing-compatible block ordering strategies.
/// </summary>
public class BlockReorderer
{
    private readonly ILogger<BlockReorderer> _logger;
    private readonly Random _random = new();

    public BlockReorderer(ILogger<BlockReorderer> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Reorders a list of block requests according to the specified order.
    /// </summary>
    /// <typeparam name="T">Block request type</typeparam>
    /// <param name="blocks">List of block requests to reorder</param>
    /// <param name="order">Block pull order strategy</param>
    /// <param name="offsetSelector">Function to get block offset</param>
    /// <param name="deviceSelector">Optional function to get device ID for device-aware ordering</param>
    /// <returns>Reordered list of block requests</returns>
    public IList<T> ReorderBlocks<T>(
        IList<T> blocks,
        SyncBlockPullOrder order,
        Func<T, long> offsetSelector,
        Func<T, string>? deviceSelector = null)
    {
        if (blocks.Count <= 1)
            return blocks;

        _logger.LogDebug("Reordering {Count} blocks with order {Order}", blocks.Count, order);

        return order switch
        {
            SyncBlockPullOrder.Standard => ReorderStandard(blocks, deviceSelector),
            SyncBlockPullOrder.Random => ReorderRandom(blocks),
            SyncBlockPullOrder.InOrder => ReorderSequential(blocks, offsetSelector),
            _ => blocks
        };
    }

    /// <summary>
    /// Standard device-aware chunking for optimal parallel downloads.
    /// Interleaves blocks from different devices to maximize throughput.
    /// </summary>
    private IList<T> ReorderStandard<T>(IList<T> blocks, Func<T, string>? deviceSelector)
    {
        if (deviceSelector == null)
        {
            // Fall back to random if no device info available
            return ReorderRandom(blocks);
        }

        // Group blocks by device
        var byDevice = blocks
            .GroupBy(deviceSelector)
            .ToDictionary(g => g.Key, g => new Queue<T>(g));

        if (byDevice.Count <= 1)
        {
            // Only one device, use random order
            return ReorderRandom(blocks);
        }

        // Interleave blocks from different devices (round-robin)
        var result = new List<T>(blocks.Count);
        var deviceQueues = byDevice.Values.ToList();
        var deviceIndex = 0;

        while (result.Count < blocks.Count)
        {
            var attempts = 0;
            while (attempts < deviceQueues.Count)
            {
                var queue = deviceQueues[deviceIndex % deviceQueues.Count];
                deviceIndex++;

                if (queue.Count > 0)
                {
                    result.Add(queue.Dequeue());
                    break;
                }
                attempts++;
            }
        }

        _logger.LogDebug("Standard reorder: interleaved blocks from {DeviceCount} devices", byDevice.Count);
        return result;
    }

    /// <summary>
    /// Shuffle blocks randomly for load balancing.
    /// </summary>
    private IList<T> ReorderRandom<T>(IList<T> blocks)
    {
        var result = blocks.ToList();

        // Fisher-Yates shuffle
        for (int i = result.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (result[i], result[j]) = (result[j], result[i]);
        }

        _logger.LogDebug("Random reorder: shuffled {Count} blocks", result.Count);
        return result;
    }

    /// <summary>
    /// Sequential ordering by offset for streaming use cases.
    /// </summary>
    private IList<T> ReorderSequential<T>(IList<T> blocks, Func<T, long> offsetSelector)
    {
        var result = blocks.OrderBy(offsetSelector).ToList();
        _logger.LogDebug("Sequential reorder: sorted {Count} blocks by offset", result.Count);
        return result;
    }

    /// <summary>
    /// Reorders block requests for a specific file download.
    /// </summary>
    public IList<BlockRequest> ReorderFileBlocks(
        IList<BlockRequest> blocks,
        SyncBlockPullOrder order)
    {
        return ReorderBlocks(
            blocks,
            order,
            b => b.Offset,
            b => b.DeviceId);
    }

    /// <summary>
    /// Creates an optimized download plan by grouping blocks by device
    /// and ordering within each group.
    /// </summary>
    public IList<DeviceBlockBatch> CreateOptimizedBatches(
        IList<BlockRequest> blocks,
        SyncBlockPullOrder order,
        int maxBatchSize = 16)
    {
        // First reorder all blocks
        var reorderedBlocks = ReorderFileBlocks(blocks, order);

        // Group by device for efficient network requests
        var batches = new List<DeviceBlockBatch>();
        var currentBatch = new List<BlockRequest>();
        string? currentDevice = null;

        foreach (var block in reorderedBlocks)
        {
            if (currentDevice != null &&
                (block.DeviceId != currentDevice || currentBatch.Count >= maxBatchSize))
            {
                // Finish current batch
                batches.Add(new DeviceBlockBatch(currentDevice, currentBatch.ToList()));
                currentBatch.Clear();
            }

            currentDevice = block.DeviceId;
            currentBatch.Add(block);
        }

        // Don't forget the last batch
        if (currentBatch.Count > 0 && currentDevice != null)
        {
            batches.Add(new DeviceBlockBatch(currentDevice, currentBatch.ToList()));
        }

        _logger.LogDebug("Created {BatchCount} optimized batches from {BlockCount} blocks",
            batches.Count, blocks.Count);

        return batches;
    }
}

/// <summary>
/// Represents a request for a single block.
/// </summary>
public class BlockRequest
{
    /// <summary>
    /// Device ID that has this block.
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Folder containing the file.
    /// </summary>
    public string FolderId { get; set; } = string.Empty;

    /// <summary>
    /// File name.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Block offset within the file.
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// Block size in bytes.
    /// </summary>
    public int Size { get; set; }

    /// <summary>
    /// Block hash for verification.
    /// </summary>
    public byte[] Hash { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Weak hash for quick comparison.
    /// </summary>
    public uint WeakHash { get; set; }
}

/// <summary>
/// A batch of block requests for a single device.
/// </summary>
public class DeviceBlockBatch
{
    /// <summary>
    /// Device ID for this batch.
    /// </summary>
    public string DeviceId { get; }

    /// <summary>
    /// Block requests in this batch.
    /// </summary>
    public IReadOnlyList<BlockRequest> Blocks { get; }

    /// <summary>
    /// Total bytes in this batch.
    /// </summary>
    public long TotalBytes => Blocks.Sum(b => b.Size);

    public DeviceBlockBatch(string deviceId, IReadOnlyList<BlockRequest> blocks)
    {
        DeviceId = deviceId;
        Blocks = blocks;
    }
}
