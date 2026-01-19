using CreatioHelper.Infrastructure.Services.Sync.Transfer;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.Transfer;

public class BlockPullOrderServiceTests
{
    private readonly Mock<ILogger<BlockPullOrderService>> _loggerMock;
    private readonly BlockPullOrderConfiguration _config;
    private readonly BlockPullOrderService _service;

    public BlockPullOrderServiceTests()
    {
        _loggerMock = new Mock<ILogger<BlockPullOrderService>>();
        _config = new BlockPullOrderConfiguration();
        _service = new BlockPullOrderService(_loggerMock.Object, _config);
    }

    #region OrderBlocks Tests

    [Fact]
    public void OrderBlocks_Empty_ReturnsEmpty()
    {
        var result = _service.OrderBlocks(Array.Empty<PullBlock>(), BlockPullOrder.InOrder);

        Assert.Empty(result);
    }

    [Fact]
    public void OrderBlocks_InOrder_SortsByIndex()
    {
        var blocks = new[]
        {
            new PullBlock { Index = 3 },
            new PullBlock { Index = 1 },
            new PullBlock { Index = 2 }
        };

        var result = _service.OrderBlocks(blocks, BlockPullOrder.InOrder);

        Assert.Equal(1, result[0].Index);
        Assert.Equal(2, result[1].Index);
        Assert.Equal(3, result[2].Index);
    }

    [Fact]
    public void OrderBlocks_Standard_SameAsInOrder()
    {
        var blocks = new[]
        {
            new PullBlock { Index = 3 },
            new PullBlock { Index = 1 },
            new PullBlock { Index = 2 }
        };

        var inOrderResult = _service.OrderBlocks(blocks, BlockPullOrder.InOrder);
        var standardResult = _service.OrderBlocks(blocks, BlockPullOrder.Standard);

        Assert.Equal(inOrderResult.Select(b => b.Index), standardResult.Select(b => b.Index));
    }

    [Fact]
    public void OrderBlocks_LargestFirst_SortsBySizeDescending()
    {
        var blocks = new[]
        {
            new PullBlock { Index = 1, Size = 100 },
            new PullBlock { Index = 2, Size = 300 },
            new PullBlock { Index = 3, Size = 200 }
        };

        var result = _service.OrderBlocks(blocks, BlockPullOrder.LargestFirst);

        Assert.Equal(300, result[0].Size);
        Assert.Equal(200, result[1].Size);
        Assert.Equal(100, result[2].Size);
    }

    [Fact]
    public void OrderBlocks_SmallestFirst_SortsBySizeAscending()
    {
        var blocks = new[]
        {
            new PullBlock { Index = 1, Size = 300 },
            new PullBlock { Index = 2, Size = 100 },
            new PullBlock { Index = 3, Size = 200 }
        };

        var result = _service.OrderBlocks(blocks, BlockPullOrder.SmallestFirst);

        Assert.Equal(100, result[0].Size);
        Assert.Equal(200, result[1].Size);
        Assert.Equal(300, result[2].Size);
    }

    [Fact]
    public void OrderBlocks_RarestFirst_SortsBySourceCountAscending()
    {
        var blocks = new[]
        {
            new PullBlock { Index = 1, SourceCount = 5 },
            new PullBlock { Index = 2, SourceCount = 1 },
            new PullBlock { Index = 3, SourceCount = 3 }
        };

        var result = _service.OrderBlocks(blocks, BlockPullOrder.RarestFirst);

        Assert.Equal(1, result[0].SourceCount);
        Assert.Equal(3, result[1].SourceCount);
        Assert.Equal(5, result[2].SourceCount);
    }

    [Fact]
    public void OrderBlocks_Random_ReturnsAllBlocks()
    {
        var blocks = Enumerable.Range(0, 100).Select(i => new PullBlock { Index = i }).ToArray();

        var result = _service.OrderBlocks(blocks, BlockPullOrder.Random);

        Assert.Equal(100, result.Count);
        Assert.Equal(100, result.Select(b => b.Index).Distinct().Count());
    }

    [Fact]
    public void OrderBlocks_Random_ShufflesBlocks()
    {
        var blocks = Enumerable.Range(0, 100).Select(i => new PullBlock { Index = i }).ToArray();

        var result = _service.OrderBlocks(blocks, BlockPullOrder.Random);

        // With 100 blocks, the probability of random order being identical to original is negligible
        var isShuffled = !result.Select((b, i) => b.Index == i).All(x => x);
        Assert.True(isShuffled);
    }

    [Fact]
    public void OrderBlocks_NullBlocks_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.OrderBlocks(null!, BlockPullOrder.InOrder));
    }

    #endregion

    #region OrderBlocksForFolder Tests

    [Fact]
    public void OrderBlocksForFolder_UsesConfiguredOrder()
    {
        _service.SetPullOrder("folder1", BlockPullOrder.LargestFirst);

        var blocks = new[]
        {
            new PullBlock { Index = 1, Size = 100 },
            new PullBlock { Index = 2, Size = 300 },
            new PullBlock { Index = 3, Size = 200 }
        };

        var result = _service.OrderBlocksForFolder(blocks, "folder1");

        Assert.Equal(300, result[0].Size);
    }

    [Fact]
    public void OrderBlocksForFolder_NullBlocks_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.OrderBlocksForFolder(null!, "folder1"));
    }

    [Fact]
    public void OrderBlocksForFolder_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.OrderBlocksForFolder(Array.Empty<PullBlock>(), null!));
    }

    #endregion

    #region GetPullOrder Tests

    [Fact]
    public void GetPullOrder_NoOverride_ReturnsDefault()
    {
        var order = _service.GetPullOrder("folder1");

        Assert.Equal(_config.DefaultOrder, order);
    }

    [Fact]
    public void GetPullOrder_WithOverride_ReturnsOverride()
    {
        _service.SetPullOrder("folder1", BlockPullOrder.RarestFirst);

        Assert.Equal(BlockPullOrder.RarestFirst, _service.GetPullOrder("folder1"));
    }

    [Fact]
    public void GetPullOrder_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetPullOrder(null!));
    }

    #endregion

    #region SetPullOrder Tests

    [Fact]
    public void SetPullOrder_ValidOrder_Sets()
    {
        _service.SetPullOrder("folder1", BlockPullOrder.Random);

        Assert.Equal(BlockPullOrder.Random, _service.GetPullOrder("folder1"));
    }

    [Fact]
    public void SetPullOrder_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.SetPullOrder(null!, BlockPullOrder.Random));
    }

    #endregion

    #region GetRecommendedOrder Tests

    [Fact]
    public void GetRecommendedOrder_SequentialAccess_ReturnsInOrder()
    {
        var characteristics = new FileCharacteristics
        {
            SequentialAccess = true
        };

        var order = _service.GetRecommendedOrder(characteristics);

        Assert.Equal(BlockPullOrder.InOrder, order);
    }

    [Fact]
    public void GetRecommendedOrder_ManySources_ReturnsRarestFirst()
    {
        var config = new BlockPullOrderConfiguration { MinSourcesForRarestFirst = 3 };
        var service = new BlockPullOrderService(_loggerMock.Object, config);

        var characteristics = new FileCharacteristics
        {
            SourceCount = 5
        };

        var order = service.GetRecommendedOrder(characteristics);

        Assert.Equal(BlockPullOrder.RarestFirst, order);
    }

    [Fact]
    public void GetRecommendedOrder_MultipleSourcesManyBlocks_ReturnsRandom()
    {
        var config = new BlockPullOrderConfiguration
        {
            MinSourcesForRarestFirst = 5,
            MinBlocksForRandomOrder = 10
        };
        var service = new BlockPullOrderService(_loggerMock.Object, config);

        var characteristics = new FileCharacteristics
        {
            SourceCount = 2,
            BlockCount = 20
        };

        var order = service.GetRecommendedOrder(characteristics);

        Assert.Equal(BlockPullOrder.Random, order);
    }

    [Fact]
    public void GetRecommendedOrder_Default_ReturnsInOrder()
    {
        var characteristics = new FileCharacteristics
        {
            SourceCount = 1,
            BlockCount = 5
        };

        var order = _service.GetRecommendedOrder(characteristics);

        Assert.Equal(BlockPullOrder.InOrder, order);
    }

    [Fact]
    public void GetRecommendedOrder_NullCharacteristics_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetRecommendedOrder(null!));
    }

    #endregion

    #region Ordering Stability Tests

    [Fact]
    public void OrderBlocks_LargestFirst_TiesResolvedByIndex()
    {
        var blocks = new[]
        {
            new PullBlock { Index = 3, Size = 100 },
            new PullBlock { Index = 1, Size = 100 },
            new PullBlock { Index = 2, Size = 100 }
        };

        var result = _service.OrderBlocks(blocks, BlockPullOrder.LargestFirst);

        // Same size, so should be ordered by index
        Assert.Equal(1, result[0].Index);
        Assert.Equal(2, result[1].Index);
        Assert.Equal(3, result[2].Index);
    }

    [Fact]
    public void OrderBlocks_RarestFirst_TiesResolvedByIndex()
    {
        var blocks = new[]
        {
            new PullBlock { Index = 3, SourceCount = 2 },
            new PullBlock { Index = 1, SourceCount = 2 },
            new PullBlock { Index = 2, SourceCount = 2 }
        };

        var result = _service.OrderBlocks(blocks, BlockPullOrder.RarestFirst);

        // Same source count, so should be ordered by index
        Assert.Equal(1, result[0].Index);
        Assert.Equal(2, result[1].Index);
        Assert.Equal(3, result[2].Index);
    }

    #endregion
}

public class BlockPullOrderConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new BlockPullOrderConfiguration();

        Assert.Equal(BlockPullOrder.Standard, config.DefaultOrder);
        Assert.False(config.UseAdaptiveOrdering);
        Assert.Equal(10, config.MinBlocksForRandomOrder);
        Assert.Equal(3, config.MinSourcesForRarestFirst);
    }

    [Fact]
    public void GetEffectiveOrder_NoOverride_ReturnsDefault()
    {
        var config = new BlockPullOrderConfiguration { DefaultOrder = BlockPullOrder.Random };

        Assert.Equal(BlockPullOrder.Random, config.GetEffectiveOrder("folder1"));
    }

    [Fact]
    public void GetEffectiveOrder_WithOverride_ReturnsOverride()
    {
        var config = new BlockPullOrderConfiguration { DefaultOrder = BlockPullOrder.Standard };
        config.FolderOrders["folder1"] = BlockPullOrder.RarestFirst;

        Assert.Equal(BlockPullOrder.RarestFirst, config.GetEffectiveOrder("folder1"));
        Assert.Equal(BlockPullOrder.Standard, config.GetEffectiveOrder("folder2"));
    }
}

public class PullBlockTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var block = new PullBlock();

        Assert.Equal(0, block.Index);
        Assert.Equal(0, block.Offset);
        Assert.Equal(0, block.Size);
        Assert.Equal(1, block.SourceCount);
        Assert.False(block.IsWeakHashOnly);
        Assert.Empty(block.Sources);
        Assert.Empty(block.Hash);
    }

    [Fact]
    public void Sources_CanBeAddedTo()
    {
        var block = new PullBlock();
        block.Sources.Add("device1");
        block.Sources.Add("device2");

        Assert.Equal(2, block.Sources.Count);
    }
}

public class FileCharacteristicsTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var characteristics = new FileCharacteristics();

        Assert.Equal(0, characteristics.FileSize);
        Assert.Equal(0, characteristics.BlockCount);
        Assert.Equal(1, characteristics.SourceCount);
        Assert.False(characteristics.SequentialAccess);
        Assert.False(characteristics.IsPartialDownload);
        Assert.Equal(1.0, characteristics.AverageBlockAvailability);
    }
}

public class BlockPullOrderBuilderTests
{
    private readonly BlockPullOrderService _service;

    public BlockPullOrderBuilderTests()
    {
        var loggerMock = new Mock<ILogger<BlockPullOrderService>>();
        _service = new BlockPullOrderService(loggerMock.Object);
    }

    [Fact]
    public void Build_WithOrder_AppliesOrder()
    {
        var blocks = new[]
        {
            new PullBlock { Index = 3 },
            new PullBlock { Index = 1 },
            new PullBlock { Index = 2 }
        };

        var result = new BlockPullOrderBuilder(_service)
            .WithBlocks(blocks)
            .WithOrder(BlockPullOrder.InOrder)
            .Build();

        Assert.Equal(1, result[0].Index);
        Assert.Equal(2, result[1].Index);
        Assert.Equal(3, result[2].Index);
    }

    [Fact]
    public void Build_WithTiebreaker_AppliesWithinGroups()
    {
        var blocks = new[]
        {
            new PullBlock { Index = 3, SourceCount = 1, Size = 100 },
            new PullBlock { Index = 1, SourceCount = 1, Size = 300 },
            new PullBlock { Index = 2, SourceCount = 2, Size = 200 }
        };

        var result = new BlockPullOrderBuilder(_service)
            .WithBlocks(blocks)
            .WithOrder(BlockPullOrder.RarestFirst)
            .WithTiebreaker(BlockPullOrder.LargestFirst)
            .Build();

        // SourceCount=1 group first (ordered by size descending)
        Assert.Equal(1, result[0].Index); // Size 300
        Assert.Equal(3, result[1].Index); // Size 100
        // SourceCount=2 group
        Assert.Equal(2, result[2].Index);
    }

    [Fact]
    public void Build_EmptyBlocks_ReturnsEmpty()
    {
        var result = new BlockPullOrderBuilder(_service)
            .WithBlocks(Array.Empty<PullBlock>())
            .WithOrder(BlockPullOrder.InOrder)
            .Build();

        Assert.Empty(result);
    }
}
