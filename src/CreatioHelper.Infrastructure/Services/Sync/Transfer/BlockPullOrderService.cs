using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Transfer;

/// <summary>
/// Enum for block pull order strategies.
/// Based on Syncthing's BlockPullOrder folder configuration.
/// </summary>
public enum BlockPullOrder
{
    /// <summary>
    /// Pull blocks in order (default).
    /// Best for sequential access patterns.
    /// </summary>
    InOrder,

    /// <summary>
    /// Pull blocks in random order.
    /// Better for multiple sources, prevents hotspots.
    /// </summary>
    Random,

    /// <summary>
    /// Pull blocks in standard order (same as InOrder).
    /// </summary>
    Standard,

    /// <summary>
    /// Pull blocks from largest to smallest.
    /// </summary>
    LargestFirst,

    /// <summary>
    /// Pull blocks from smallest to largest.
    /// </summary>
    SmallestFirst,

    /// <summary>
    /// Pull rarest blocks first (from fewest sources).
    /// </summary>
    RarestFirst
}

/// <summary>
/// Represents a block to be pulled.
/// </summary>
public class PullBlock
{
    /// <summary>
    /// Block index within the file.
    /// </summary>
    public int Index { get; init; }

    /// <summary>
    /// Block offset in bytes.
    /// </summary>
    public long Offset { get; init; }

    /// <summary>
    /// Block size in bytes.
    /// </summary>
    public int Size { get; init; }

    /// <summary>
    /// Block hash.
    /// </summary>
    public byte[] Hash { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// Number of sources that have this block.
    /// </summary>
    public int SourceCount { get; set; } = 1;

    /// <summary>
    /// Whether this block is weak-hash only.
    /// </summary>
    public bool IsWeakHashOnly { get; init; }

    /// <summary>
    /// Device IDs that have this block.
    /// </summary>
    public List<string> Sources { get; } = new();
}

/// <summary>
/// Service for ordering blocks during pull operations.
/// </summary>
public interface IBlockPullOrderService
{
    /// <summary>
    /// Order blocks according to the specified strategy.
    /// </summary>
    IReadOnlyList<PullBlock> OrderBlocks(IEnumerable<PullBlock> blocks, BlockPullOrder order);

    /// <summary>
    /// Order blocks for a specific folder.
    /// </summary>
    IReadOnlyList<PullBlock> OrderBlocksForFolder(IEnumerable<PullBlock> blocks, string folderId);

    /// <summary>
    /// Get the configured pull order for a folder.
    /// </summary>
    BlockPullOrder GetPullOrder(string folderId);

    /// <summary>
    /// Set the pull order for a folder.
    /// </summary>
    void SetPullOrder(string folderId, BlockPullOrder order);

    /// <summary>
    /// Get recommended pull order based on file characteristics.
    /// </summary>
    BlockPullOrder GetRecommendedOrder(FileCharacteristics characteristics);
}

/// <summary>
/// Characteristics of a file for pull order recommendation.
/// </summary>
public class FileCharacteristics
{
    /// <summary>
    /// Total file size in bytes.
    /// </summary>
    public long FileSize { get; init; }

    /// <summary>
    /// Number of blocks in the file.
    /// </summary>
    public int BlockCount { get; init; }

    /// <summary>
    /// Number of available sources (devices).
    /// </summary>
    public int SourceCount { get; init; } = 1;

    /// <summary>
    /// Whether the file will be accessed sequentially.
    /// </summary>
    public bool SequentialAccess { get; init; }

    /// <summary>
    /// Whether this is a partial/resume download.
    /// </summary>
    public bool IsPartialDownload { get; init; }

    /// <summary>
    /// Average block availability (how many sources per block).
    /// </summary>
    public double AverageBlockAvailability { get; init; } = 1.0;
}

/// <summary>
/// Configuration for block pull ordering.
/// </summary>
public class BlockPullOrderConfiguration
{
    /// <summary>
    /// Default pull order for all folders.
    /// </summary>
    public BlockPullOrder DefaultOrder { get; set; } = BlockPullOrder.Standard;

    /// <summary>
    /// Per-folder pull order overrides.
    /// </summary>
    public Dictionary<string, BlockPullOrder> FolderOrders { get; } = new();

    /// <summary>
    /// Whether to use adaptive ordering based on file characteristics.
    /// </summary>
    public bool UseAdaptiveOrdering { get; set; } = false;

    /// <summary>
    /// Minimum block count to use random ordering (to avoid hotspots).
    /// </summary>
    public int MinBlocksForRandomOrder { get; set; } = 10;

    /// <summary>
    /// Minimum sources to consider RarestFirst ordering.
    /// </summary>
    public int MinSourcesForRarestFirst { get; set; } = 3;

    /// <summary>
    /// Get effective pull order for a folder.
    /// </summary>
    public BlockPullOrder GetEffectiveOrder(string folderId)
    {
        if (FolderOrders.TryGetValue(folderId, out var order))
        {
            return order;
        }
        return DefaultOrder;
    }
}

/// <summary>
/// Implementation of block pull order service.
/// </summary>
public class BlockPullOrderService : IBlockPullOrderService
{
    private readonly ILogger<BlockPullOrderService> _logger;
    private readonly BlockPullOrderConfiguration _config;
    private readonly Random _random = new();

    public BlockPullOrderService(
        ILogger<BlockPullOrderService> logger,
        BlockPullOrderConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new BlockPullOrderConfiguration();
    }

    /// <inheritdoc />
    public IReadOnlyList<PullBlock> OrderBlocks(IEnumerable<PullBlock> blocks, BlockPullOrder order)
    {
        ArgumentNullException.ThrowIfNull(blocks);

        var blockList = blocks.ToList();
        if (blockList.Count == 0)
        {
            return blockList;
        }

        return order switch
        {
            BlockPullOrder.InOrder or BlockPullOrder.Standard => OrderInOrder(blockList),
            BlockPullOrder.Random => OrderRandom(blockList),
            BlockPullOrder.LargestFirst => OrderLargestFirst(blockList),
            BlockPullOrder.SmallestFirst => OrderSmallestFirst(blockList),
            BlockPullOrder.RarestFirst => OrderRarestFirst(blockList),
            _ => OrderInOrder(blockList)
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<PullBlock> OrderBlocksForFolder(IEnumerable<PullBlock> blocks, string folderId)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(folderId);

        var order = GetPullOrder(folderId);
        return OrderBlocks(blocks, order);
    }

    /// <inheritdoc />
    public BlockPullOrder GetPullOrder(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        return _config.GetEffectiveOrder(folderId);
    }

    /// <inheritdoc />
    public void SetPullOrder(string folderId, BlockPullOrder order)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        _config.FolderOrders[folderId] = order;
        _logger.LogInformation("Set block pull order for folder {FolderId} to {Order}", folderId, order);
    }

    /// <inheritdoc />
    public BlockPullOrder GetRecommendedOrder(FileCharacteristics characteristics)
    {
        ArgumentNullException.ThrowIfNull(characteristics);

        // For sequential access (like video playback), use in-order
        if (characteristics.SequentialAccess)
        {
            return BlockPullOrder.InOrder;
        }

        // For many sources, rarest-first helps balance load
        if (characteristics.SourceCount >= _config.MinSourcesForRarestFirst)
        {
            return BlockPullOrder.RarestFirst;
        }

        // For large files with multiple sources, random helps avoid hotspots
        if (characteristics.SourceCount > 1 && characteristics.BlockCount >= _config.MinBlocksForRandomOrder)
        {
            return BlockPullOrder.Random;
        }

        // Default to in-order
        return BlockPullOrder.InOrder;
    }

    private IReadOnlyList<PullBlock> OrderInOrder(List<PullBlock> blocks)
    {
        return blocks.OrderBy(b => b.Index).ToList();
    }

    private IReadOnlyList<PullBlock> OrderRandom(List<PullBlock> blocks)
    {
        // Fisher-Yates shuffle
        var result = blocks.ToList();
        for (int i = result.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (result[i], result[j]) = (result[j], result[i]);
        }
        return result;
    }

    private IReadOnlyList<PullBlock> OrderLargestFirst(List<PullBlock> blocks)
    {
        return blocks.OrderByDescending(b => b.Size).ThenBy(b => b.Index).ToList();
    }

    private IReadOnlyList<PullBlock> OrderSmallestFirst(List<PullBlock> blocks)
    {
        return blocks.OrderBy(b => b.Size).ThenBy(b => b.Index).ToList();
    }

    private IReadOnlyList<PullBlock> OrderRarestFirst(List<PullBlock> blocks)
    {
        // Order by source count (ascending), then by index for stability
        return blocks.OrderBy(b => b.SourceCount).ThenBy(b => b.Index).ToList();
    }
}

/// <summary>
/// Builder for creating ordered block pull lists with multiple strategies.
/// </summary>
public class BlockPullOrderBuilder
{
    private readonly List<PullBlock> _blocks = new();
    private BlockPullOrder _primaryOrder = BlockPullOrder.InOrder;
    private BlockPullOrder? _tiebreaker;
    private readonly IBlockPullOrderService _orderService;

    public BlockPullOrderBuilder(IBlockPullOrderService orderService)
    {
        _orderService = orderService;
    }

    /// <summary>
    /// Add blocks to be ordered.
    /// </summary>
    public BlockPullOrderBuilder WithBlocks(IEnumerable<PullBlock> blocks)
    {
        _blocks.AddRange(blocks);
        return this;
    }

    /// <summary>
    /// Set the primary ordering strategy.
    /// </summary>
    public BlockPullOrderBuilder WithOrder(BlockPullOrder order)
    {
        _primaryOrder = order;
        return this;
    }

    /// <summary>
    /// Set a tiebreaker ordering strategy.
    /// </summary>
    public BlockPullOrderBuilder WithTiebreaker(BlockPullOrder tiebreaker)
    {
        _tiebreaker = tiebreaker;
        return this;
    }

    /// <summary>
    /// Build the ordered block list.
    /// </summary>
    public IReadOnlyList<PullBlock> Build()
    {
        if (_tiebreaker.HasValue && _primaryOrder == BlockPullOrder.RarestFirst)
        {
            // For rarest-first with tiebreaker, group by source count and apply tiebreaker within groups
            var groups = _blocks.GroupBy(b => b.SourceCount).OrderBy(g => g.Key);
            var result = new List<PullBlock>();

            foreach (var group in groups)
            {
                var ordered = _orderService.OrderBlocks(group, _tiebreaker.Value);
                result.AddRange(ordered);
            }

            return result;
        }

        return _orderService.OrderBlocks(_blocks, _primaryOrder);
    }
}
