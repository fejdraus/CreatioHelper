using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests;

public class DeltaSyncEngineTests
{
    private readonly Mock<ILogger<DeltaSyncEngine>> _mockLogger;
    private readonly AdaptiveBlockSizer _blockSizer;
    private readonly DeltaSyncEngine _deltaSyncEngine;

    public DeltaSyncEngineTests()
    {
        _mockLogger = new Mock<ILogger<DeltaSyncEngine>>();
        var mockBlockSizerLogger = new Mock<ILogger<AdaptiveBlockSizer>>();
        _blockSizer = new AdaptiveBlockSizer(mockBlockSizerLogger.Object);
        _deltaSyncEngine = new DeltaSyncEngine(_mockLogger.Object, _blockSizer);
    }

    [Fact]
    public void CreateSyncPlan_IdenticalFiles_ReturnsSynchronizedPlan()
    {
        // Arrange
        var localFile = CreateTestFile("test.txt", 1024, "abcd1234");
        var remoteFile = CreateTestFile("test.txt", 1024, "abcd1234");

        // Act
        var plan = _deltaSyncEngine.CreateSyncPlan(localFile, remoteFile);

        // Assert
        Assert.True(plan.IsSynchronized);
        Assert.Empty(plan.RequiredBlocks);
        Assert.Equal(0, plan.TransferredBytes);
    }

    [Fact]
    public void CreateSyncPlan_DifferentFiles_RequiresTransfer()
    {
        // Arrange
        var localFile = CreateTestFile("test.txt", 1024, "local1234");
        var remoteFile = CreateTestFile("test.txt", 1024, "remote567");

        // Act
        var plan = _deltaSyncEngine.CreateSyncPlan(localFile, remoteFile);

        // Assert
        Assert.False(plan.IsSynchronized);
        Assert.NotEmpty(plan.RequiredBlocks);
        Assert.True(plan.TransferredBytes > 0);
    }

    [Fact]
    public void CreateSyncPlan_PartiallyMatchingBlocks_OptimizesTransfer()
    {
        // Arrange - files with some matching blocks
        var localFile = CreateTestFileWithBlocks("test.txt", new[]
        {
            new BlockInfo(0, 256, "hash1", 12345),
            new BlockInfo(256, 256, "hash2", 67890),
            new BlockInfo(512, 256, "hash3", 11111)
        });

        var remoteFile = CreateTestFileWithBlocks("test.txt", new[]
        {
            new BlockInfo(0, 256, "hash1", 12345), // Same block
            new BlockInfo(256, 256, "hash_new", 99999), // Different block
            new BlockInfo(512, 256, "hash3", 11111)  // Same block
        });

        // Act
        var plan = _deltaSyncEngine.CreateSyncPlan(localFile, remoteFile);

        // Assert
        Assert.False(plan.IsSynchronized);
        Assert.Single(plan.RequiredBlocks); // Only one block needs transfer
        Assert.Equal(256, plan.TransferredBytes); // Only the changed block
        Assert.Equal(2, plan.AvailableBlocks.Count); // Two blocks available locally
    }

    [Fact]
    public void CreateSyncPlan_EmptyLocalFile_RequiresFullTransfer()
    {
        // Arrange
        var localFile = CreateTestFile("test.txt", 0, "");
        var remoteFile = CreateTestFile("test.txt", 1024, "remote567");

        // Act
        var plan = _deltaSyncEngine.CreateSyncPlan(localFile, remoteFile);

        // Assert
        Assert.False(plan.IsSynchronized);
        Assert.True(plan.RequiredBlocks.Count > 0);
        Assert.Equal(remoteFile.Size, plan.TransferredBytes);
    }

    [Fact]
    public void ValidateBlock_CorrectHashes_ReturnsTrue()
    {
        // Arrange
        var testData = System.Text.Encoding.UTF8.GetBytes("Hello, World!");
        var (weakHash, strongHash) = WeakHashCalculator.CalculateBlockHashes(testData);

        // Act
        var isValid = _deltaSyncEngine.ValidateBlock(testData, strongHash, (int)weakHash);

        // Assert
        Assert.True(isValid);
    }

    [Fact]
    public void ValidateBlock_IncorrectWeakHash_ReturnsFalse()
    {
        // Arrange
        var testData = System.Text.Encoding.UTF8.GetBytes("Hello, World!");
        var (_, strongHash) = WeakHashCalculator.CalculateBlockHashes(testData);
        var incorrectWeakHash = 99999;

        // Act
        var isValid = _deltaSyncEngine.ValidateBlock(testData, strongHash, incorrectWeakHash);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void ValidateBlock_IncorrectStrongHash_ReturnsFalse()
    {
        // Arrange
        var testData = System.Text.Encoding.UTF8.GetBytes("Hello, World!");
        var (weakHash, _) = WeakHashCalculator.CalculateBlockHashes(testData);
        var incorrectStrongHash = "incorrect_hash";

        // Act
        var isValid = _deltaSyncEngine.ValidateBlock(testData, incorrectStrongHash, (int)weakHash);

        // Assert
        Assert.False(isValid);
    }

    [Fact]
    public void CreateAdvancedSyncPlan_CallsBasicPlanForNow()
    {
        // Arrange
        var localFile = CreateTestFile("test.txt", 1024, "local1234");
        var remoteFile = CreateTestFile("test.txt", 1024, "remote567");
        
        // Use real block sizer for this test

        // Act
        var plan = _deltaSyncEngine.CreateAdvancedSyncPlan(localFile, remoteFile, 256);

        // Assert
        Assert.False(plan.IsSynchronized);
        Assert.NotEmpty(plan.RequiredBlocks);
    }

    [Fact]
    public void CreateSyncPlan_WithWeakHashCollisions_UsesStrongHashForVerification()
    {
        // Arrange - create blocks with same weak hash but different strong hash
        var localFile = CreateTestFileWithBlocks("test.txt", new[]
        {
            new BlockInfo(0, 256, "strong_hash_1", 12345)
        });

        var remoteFile = CreateTestFileWithBlocks("test.txt", new[]
        {
            new BlockInfo(0, 256, "strong_hash_2", 12345) // Same weak hash, different strong hash
        });

        // Act
        var plan = _deltaSyncEngine.CreateSyncPlan(localFile, remoteFile);

        // Assert
        Assert.False(plan.IsSynchronized);
        Assert.Single(plan.RequiredBlocks); // Should require transfer due to strong hash mismatch
        Assert.Equal(256, plan.TransferredBytes);
    }

    [Theory]
    [InlineData(100, 1)]
    [InlineData(1000, 2)]
    [InlineData(10000, 3)]
    public void CreateSyncPlan_VariousFileSizes_HandlesCorrectly(long fileSize, int expectedMinBlocks)
    {
        // Arrange
        var localFile = CreateTestFile("test.txt", fileSize, "local_content");
        var remoteFile = CreateTestFile("test.txt", fileSize, "remote_content");

        // Act
        var plan = _deltaSyncEngine.CreateSyncPlan(localFile, remoteFile);

        // Assert
        Assert.False(plan.IsSynchronized);
        Assert.True(plan.RequiredBlocks.Count >= expectedMinBlocks);
        Assert.Equal(fileSize, plan.TransferredBytes); // Full transfer needed for different content
    }

    private SyncFileInfo CreateTestFile(string fileName, long size, string contentHash)
    {
        var file = new SyncFileInfo("test-folder", fileName, fileName, size, DateTime.UtcNow);
        file.UpdateHash(contentHash);
        
        // Create some test blocks if size > 0
        if (size > 0)
        {
            var blockSize = Math.Min(256L, size);
            var blocks = new List<BlockInfo>();
            
            for (long offset = 0; offset < size; offset += blockSize)
            {
                var currentBlockSize = (int)Math.Min(blockSize, size - offset);
                var blockHash = $"block_{offset}_{contentHash}";
                var weakHash = (uint)(offset % 65536); // Simple weak hash
                
                blocks.Add(new BlockInfo(offset, currentBlockSize, blockHash, weakHash));
            }
            
            file.SetBlocks(blocks);
        }
        
        return file;
    }

    private SyncFileInfo CreateTestFileWithBlocks(string fileName, BlockInfo[] blocks)
    {
        var totalSize = blocks.Sum(b => b.Size);
        var file = new SyncFileInfo("test-folder", fileName, fileName, totalSize, DateTime.UtcNow);
        file.SetBlocks(blocks.ToList());
        
        // Create a hash based on block hashes
        var combinedHash = string.Join("", blocks.Select(b => b.Hash));
        file.UpdateHash(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(combinedHash)
        ).Take(8).Select(b => b.ToString("x2")).Aggregate((a, b) => a + b));
        
        return file;
    }
}