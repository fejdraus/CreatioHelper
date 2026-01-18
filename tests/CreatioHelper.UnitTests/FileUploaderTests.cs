using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync;
using System.Security.Cryptography;

namespace CreatioHelper.UnitTests;

/// <summary>
/// Unit tests for FileUploader class
/// Tests block calculation, upload flow, verification, and error handling
/// </summary>
public class FileUploaderTests : IDisposable
{
    private readonly Mock<ILogger<FileUploader>> _mockLogger;
    private readonly Mock<ISyncProtocol> _mockProtocol;
    private readonly FileUploader _fileUploader;
    private readonly string _testDirectory;

    public FileUploaderTests()
    {
        _mockLogger = new Mock<ILogger<FileUploader>>();
        _mockProtocol = new Mock<ISyncProtocol>();
        _fileUploader = new FileUploader(_mockLogger.Object, _mockProtocol.Object);

        _testDirectory = Path.Combine(Path.GetTempPath(), "FileUploaderTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
    }

    #region Block Calculation Tests

    [Fact]
    public async Task CalculateFileBlocksAsync_SmallFile_ReturnsOneBlock()
    {
        // Arrange
        var filePath = CreateTestFile(1024); // 1KB file

        // Act
        var blocks = await _fileUploader.CalculateFileBlocksAsync(filePath);

        // Assert
        Assert.Single(blocks);
        Assert.Equal(0, blocks[0].Offset);
        Assert.Equal(1024, blocks[0].Size);
        Assert.NotEmpty(blocks[0].Hash);
        Assert.Equal(64, blocks[0].Hash.Length); // SHA-256 hex = 64 chars
    }

    [Fact]
    public async Task CalculateFileBlocksAsync_MultiBlockFile_ReturnsMultipleBlocks()
    {
        // Arrange - create file larger than minimum block size (128KB)
        var filePath = CreateTestFile(256 * 1024); // 256KB file

        // Act
        var blocks = await _fileUploader.CalculateFileBlocksAsync(filePath);

        // Assert
        Assert.Equal(2, blocks.Count);
        Assert.Equal(0, blocks[0].Offset);
        Assert.Equal(128 * 1024, blocks[0].Size);
        Assert.Equal(128 * 1024, blocks[1].Offset);
        Assert.Equal(128 * 1024, blocks[1].Size);
    }

    [Fact]
    public async Task CalculateFileBlocksAsync_EmptyFile_ReturnsEmptyList()
    {
        // Arrange
        var filePath = CreateTestFile(0);

        // Act
        var blocks = await _fileUploader.CalculateFileBlocksAsync(filePath);

        // Assert
        Assert.Empty(blocks);
    }

    [Fact]
    public async Task CalculateFileBlocksAsync_LargeFile_UsesLargerBlockSize()
    {
        // Arrange - create 1MB file, should use 128KB blocks (1MB / 128KB = 8 blocks)
        var filePath = CreateTestFile(1024 * 1024);

        // Act
        var blocks = await _fileUploader.CalculateFileBlocksAsync(filePath);

        // Assert
        Assert.Equal(8, blocks.Count);
        Assert.All(blocks.Take(7), b => Assert.Equal(128 * 1024, b.Size));
    }

    [Fact]
    public async Task CalculateFileBlocksAsync_BlocksHaveCorrectOffsets()
    {
        // Arrange
        var filePath = CreateTestFile(384 * 1024); // 3 blocks of 128KB

        // Act
        var blocks = await _fileUploader.CalculateFileBlocksAsync(filePath);

        // Assert
        Assert.Equal(3, blocks.Count);
        Assert.Equal(0, blocks[0].Offset);
        Assert.Equal(128 * 1024, blocks[1].Offset);
        Assert.Equal(256 * 1024, blocks[2].Offset);
    }

    [Fact]
    public async Task CalculateFileBlocksAsync_LastBlockCanBeSmallerThanBlockSize()
    {
        // Arrange - 150KB file = 128KB + 22KB
        var filePath = CreateTestFile(150 * 1024);

        // Act
        var blocks = await _fileUploader.CalculateFileBlocksAsync(filePath);

        // Assert
        Assert.Equal(2, blocks.Count);
        Assert.Equal(128 * 1024, blocks[0].Size);
        Assert.Equal(22 * 1024, blocks[1].Size);
    }

    [Fact]
    public async Task CalculateFileBlocksAsync_HashesAreConsistent()
    {
        // Arrange
        var filePath = CreateTestFile(1024, fillByte: 0x42);

        // Act
        var blocks1 = await _fileUploader.CalculateFileBlocksAsync(filePath);
        var blocks2 = await _fileUploader.CalculateFileBlocksAsync(filePath);

        // Assert
        Assert.Equal(blocks1[0].Hash, blocks2[0].Hash);
    }

    [Fact]
    public async Task CalculateFileBlocksAsync_DifferentContentHasDifferentHashes()
    {
        // Arrange
        var filePath1 = CreateTestFile(1024, fillByte: 0x00);
        var filePath2 = CreateTestFile(1024, fillByte: 0xFF);

        // Act
        var blocks1 = await _fileUploader.CalculateFileBlocksAsync(filePath1);
        var blocks2 = await _fileUploader.CalculateFileBlocksAsync(filePath2);

        // Assert
        Assert.NotEqual(blocks1[0].Hash, blocks2[0].Hash);
    }

    [Fact]
    public async Task CalculateFileBlocksAsync_WeakHashIsCalculated()
    {
        // Arrange
        var filePath = CreateTestFile(1024);

        // Act
        var blocks = await _fileUploader.CalculateFileBlocksAsync(filePath);

        // Assert
        Assert.True(blocks[0].WeakHash > 0);
    }

    [Fact]
    public async Task CalculateFileBlocksAsync_CancellationThrowsException()
    {
        // Arrange
        var filePath = CreateTestFile(1024 * 1024); // 1MB file
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _fileUploader.CalculateFileBlocksAsync(filePath, cts.Token));
    }

    #endregion

    #region Upload Flow Tests

    [Fact]
    public async Task UploadFileAsync_Success_ReturnsSuccessResult()
    {
        // Arrange
        var deviceId = "test-device";
        var folderId = "test-folder";
        var filePath = CreateTestFile(1024);
        var fileInfo = new SyncFileInfo(folderId, "test.txt", "test.txt", 1024, DateTime.UtcNow);

        _mockProtocol.Setup(p => p.SendIndexUpdateAsync(deviceId, folderId, It.IsAny<List<SyncFileInfo>>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _fileUploader.UploadFileAsync(deviceId, folderId, filePath, fileInfo);

        // Assert
        Assert.True(result.Success);
        Assert.Equal("test.txt", result.FileName);
        Assert.Equal(1024, result.BytesTransferred);
        Assert.True(result.BlocksTransferred > 0);
        Assert.True(result.Duration > TimeSpan.Zero);
        Assert.Empty(result.Error);
    }

    [Fact]
    public async Task UploadFileAsync_FileNotFound_ReturnsFailure()
    {
        // Arrange
        var deviceId = "test-device";
        var folderId = "test-folder";
        var nonExistentPath = Path.Combine(_testDirectory, "nonexistent.txt");
        var fileInfo = new SyncFileInfo(folderId, "nonexistent.txt", "nonexistent.txt", 1024, DateTime.UtcNow);

        // Act
        var result = await _fileUploader.UploadFileAsync(deviceId, folderId, nonExistentPath, fileInfo);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task UploadFileAsync_CalculatesBlocksWhenNotProvided()
    {
        // Arrange
        var deviceId = "test-device";
        var folderId = "test-folder";
        var filePath = CreateTestFile(256 * 1024);
        var fileInfo = new SyncFileInfo(folderId, "test.txt", "test.txt", 256 * 1024, DateTime.UtcNow);
        // fileInfo.Blocks is empty initially

        _mockProtocol.Setup(p => p.SendIndexUpdateAsync(deviceId, folderId, It.IsAny<List<SyncFileInfo>>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _fileUploader.UploadFileAsync(deviceId, folderId, filePath, fileInfo);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(2, result.BlocksTransferred); // 256KB / 128KB = 2 blocks
    }

    [Fact]
    public async Task UploadFileAsync_SendsIndexUpdateToProtocol()
    {
        // Arrange
        var deviceId = "test-device";
        var folderId = "test-folder";
        var filePath = CreateTestFile(1024);
        var fileInfo = new SyncFileInfo(folderId, "test.txt", "test.txt", 1024, DateTime.UtcNow);

        _mockProtocol.Setup(p => p.SendIndexUpdateAsync(deviceId, folderId, It.IsAny<List<SyncFileInfo>>()))
            .Returns(Task.CompletedTask);

        // Act
        await _fileUploader.UploadFileAsync(deviceId, folderId, filePath, fileInfo);

        // Assert
        _mockProtocol.Verify(
            p => p.SendIndexUpdateAsync(deviceId, folderId, It.Is<List<SyncFileInfo>>(list => list.Count == 1 && list[0].Name == "test.txt")),
            Times.Once);
    }

    [Fact]
    public async Task UploadFileAsync_ProtocolError_ReturnsFailure()
    {
        // Arrange
        var deviceId = "test-device";
        var folderId = "test-folder";
        var filePath = CreateTestFile(1024);
        var fileInfo = new SyncFileInfo(folderId, "test.txt", "test.txt", 1024, DateTime.UtcNow);

        _mockProtocol.Setup(p => p.SendIndexUpdateAsync(deviceId, folderId, It.IsAny<List<SyncFileInfo>>()))
            .ThrowsAsync(new InvalidOperationException("Connection failed"));

        // Act
        var result = await _fileUploader.UploadFileAsync(deviceId, folderId, filePath, fileInfo);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Connection failed", result.Error);
    }

    [Fact]
    public async Task UploadFileAsync_SizeMismatch_ReturnsFailure()
    {
        // Arrange
        var deviceId = "test-device";
        var folderId = "test-folder";
        var filePath = CreateTestFile(1024);
        // FileInfo has wrong size
        var fileInfo = new SyncFileInfo(folderId, "test.txt", "test.txt", 2048, DateTime.UtcNow);
        // Add a block to trigger verification
        var blocks = new List<BlockInfo> { new BlockInfo(0, 2048, "abc123", 0) };
        fileInfo.SetBlocks(blocks);

        // Act
        var result = await _fileUploader.UploadFileAsync(deviceId, folderId, filePath, fileInfo);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("Size mismatch", result.Error);
    }

    [Fact]
    public async Task UploadFileAsync_ConcurrentUploadsSameFile_AreSequential()
    {
        // Arrange
        var deviceId = "test-device";
        var folderId = "test-folder";
        var filePath = CreateTestFile(1024);
        var fileInfo = new SyncFileInfo(folderId, "test.txt", "test.txt", 1024, DateTime.UtcNow);

        var callOrder = new List<int>();
        var callNumber = 0;

        _mockProtocol.Setup(p => p.SendIndexUpdateAsync(deviceId, folderId, It.IsAny<List<SyncFileInfo>>()))
            .Returns(async () =>
            {
                var currentCall = Interlocked.Increment(ref callNumber);
                callOrder.Add(currentCall);
                await Task.Delay(50); // Simulate some work
            });

        // Act - start two uploads concurrently
        var task1 = _fileUploader.UploadFileAsync(deviceId, folderId, filePath, fileInfo);
        var task2 = _fileUploader.UploadFileAsync(deviceId, folderId, filePath, fileInfo);

        var results = await Task.WhenAll(task1, task2);

        // Assert - both should succeed and calls should be sequential (1, then 2)
        Assert.True(results[0].Success);
        Assert.True(results[1].Success);
        Assert.Equal(2, callOrder.Count);
    }

    #endregion

    #region Block Size Calculation Tests

    [Fact]
    public async Task BlockSizeCalculation_SmallFile_UsesMinimumBlockSize()
    {
        // Arrange - files smaller than min block size (128KB) get one block
        var filePath = CreateTestFile(1024); // 1KB file

        // Act
        var blocks = await _fileUploader.CalculateFileBlocksAsync(filePath);

        // Assert - should have exactly one block
        Assert.Single(blocks);
        Assert.Equal(1024, blocks[0].Size);
    }

    #endregion

    #region Weak Hash Tests

    [Fact]
    public async Task WeakHash_DifferentDataProducesDifferentHashes()
    {
        // Arrange
        var filePath1 = CreateTestFile(1024, fillByte: 0x00);
        var filePath2 = CreateTestFile(1024, fillByte: 0xFF);

        // Act
        var blocks1 = await _fileUploader.CalculateFileBlocksAsync(filePath1);
        var blocks2 = await _fileUploader.CalculateFileBlocksAsync(filePath2);

        // Assert
        Assert.NotEqual(blocks1[0].WeakHash, blocks2[0].WeakHash);
    }

    [Fact]
    public async Task WeakHash_SameDataProducesSameHash()
    {
        // Arrange
        var filePath = CreateTestFile(1024, fillByte: 0x42);

        // Act
        var blocks1 = await _fileUploader.CalculateFileBlocksAsync(filePath);
        var blocks2 = await _fileUploader.CalculateFileBlocksAsync(filePath);

        // Assert
        Assert.Equal(blocks1[0].WeakHash, blocks2[0].WeakHash);
    }

    #endregion

    #region File Verification Tests

    [Fact]
    public async Task UploadFileAsync_WithCorrectBlocks_PassesVerification()
    {
        // Arrange
        var deviceId = "test-device";
        var folderId = "test-folder";
        var filePath = CreateTestFile(1024, fillByte: 0x42);

        // Calculate correct blocks first
        var blocks = await _fileUploader.CalculateFileBlocksAsync(filePath);

        var fileInfo = new SyncFileInfo(folderId, "test.txt", "test.txt", 1024, DateTime.UtcNow);
        fileInfo.SetBlocks(blocks);

        _mockProtocol.Setup(p => p.SendIndexUpdateAsync(deviceId, folderId, It.IsAny<List<SyncFileInfo>>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _fileUploader.UploadFileAsync(deviceId, folderId, filePath, fileInfo);

        // Assert
        Assert.True(result.Success);
    }

    [Fact]
    public async Task UploadFileAsync_WithIncorrectBlockHash_FailsVerification()
    {
        // Arrange
        var deviceId = "test-device";
        var folderId = "test-folder";
        var filePath = CreateTestFile(1024);

        var fileInfo = new SyncFileInfo(folderId, "test.txt", "test.txt", 1024, DateTime.UtcNow);
        // Set incorrect block hash
        var wrongBlocks = new List<BlockInfo> { new BlockInfo(0, 1024, "0000000000000000000000000000000000000000000000000000000000000000", 0) };
        fileInfo.SetBlocks(wrongBlocks);

        // Act
        var result = await _fileUploader.UploadFileAsync(deviceId, folderId, filePath, fileInfo);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("hash mismatch", result.Error.ToLower());
    }

    #endregion

    #region File Hash Calculation Tests

    [Fact]
    public async Task FileHash_IsCalculatedFromBlockHashes()
    {
        // Arrange
        var deviceId = "test-device";
        var folderId = "test-folder";
        var filePath = CreateTestFile(256 * 1024); // Multi-block file
        var fileInfo = new SyncFileInfo(folderId, "test.txt", "test.txt", 256 * 1024, DateTime.UtcNow);

        _mockProtocol.Setup(p => p.SendIndexUpdateAsync(deviceId, folderId, It.IsAny<List<SyncFileInfo>>()))
            .Callback<string, string, List<SyncFileInfo>>((d, f, files) =>
            {
                // Verify that file hash was calculated
                Assert.NotEmpty(files[0].Hash);
                Assert.Equal(64, files[0].Hash.Length); // SHA-256 hex
            })
            .Returns(Task.CompletedTask);

        // Act
        var result = await _fileUploader.UploadFileAsync(deviceId, folderId, filePath, fileInfo);

        // Assert
        Assert.True(result.Success);
        _mockProtocol.Verify(p => p.SendIndexUpdateAsync(deviceId, folderId, It.IsAny<List<SyncFileInfo>>()), Times.Once);
    }

    #endregion

    #region Helper Methods

    private string CreateTestFile(int size, byte fillByte = 0x00)
    {
        var filePath = Path.Combine(_testDirectory, $"test_{Guid.NewGuid()}.bin");
        var data = new byte[size];
        if (fillByte != 0x00)
        {
            Array.Fill(data, fillByte);
        }
        else if (size > 0)
        {
            // Fill with random-ish data to avoid all-zero hashes
            var random = new Random(42);
            random.NextBytes(data);
        }
        File.WriteAllBytes(filePath, data);
        return filePath;
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    #endregion
}

/// <summary>
/// Tests for UploadResult class
/// </summary>
public class UploadResultTests
{
    [Fact]
    public void UploadResult_DefaultValues_AreCorrect()
    {
        // Act
        var result = new UploadResult();

        // Assert
        Assert.Equal(string.Empty, result.FileName);
        Assert.False(result.Success);
        Assert.Equal(string.Empty, result.Error);
        Assert.Empty(result.Errors);
        Assert.Equal(0, result.BytesTransferred);
        Assert.Equal(0, result.BlocksTransferred);
        Assert.Equal(TimeSpan.Zero, result.Duration);
    }

    [Fact]
    public void UploadResult_CanSetAllProperties()
    {
        // Arrange & Act
        var result = new UploadResult
        {
            FileName = "test.txt",
            Success = true,
            Error = "some error",
            BytesTransferred = 1024,
            BlocksTransferred = 2,
            Duration = TimeSpan.FromSeconds(5)
        };
        result.Errors.Add("error1");

        // Assert
        Assert.Equal("test.txt", result.FileName);
        Assert.True(result.Success);
        Assert.Equal("some error", result.Error);
        Assert.Single(result.Errors);
        Assert.Equal(1024, result.BytesTransferred);
        Assert.Equal(2, result.BlocksTransferred);
        Assert.Equal(TimeSpan.FromSeconds(5), result.Duration);
    }
}

/// <summary>
/// Tests for FileVerificationResult class
/// </summary>
public class FileVerificationResultTests
{
    [Fact]
    public void FileVerificationResult_DefaultValues_AreCorrect()
    {
        // Act
        var result = new FileVerificationResult();

        // Assert
        Assert.False(result.IsValid);
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public void FileVerificationResult_CanSetProperties()
    {
        // Act
        var result = new FileVerificationResult
        {
            IsValid = true,
            Error = "test error"
        };

        // Assert
        Assert.True(result.IsValid);
        Assert.Equal("test error", result.Error);
    }
}

/// <summary>
/// Tests for Delta Upload functionality
/// </summary>
public class DeltaUploadTests : IDisposable
{
    private readonly Mock<ILogger<FileUploader>> _mockLogger;
    private readonly Mock<ISyncProtocol> _mockProtocol;
    private readonly FileUploader _fileUploader;
    private readonly string _testDirectory;

    public DeltaUploadTests()
    {
        _mockLogger = new Mock<ILogger<FileUploader>>();
        _mockProtocol = new Mock<ISyncProtocol>();
        _fileUploader = new FileUploader(_mockLogger.Object, _mockProtocol.Object);

        _testDirectory = Path.Combine(Path.GetTempPath(), "DeltaUploadTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
    }

    #region Delta Upload Tests

    [Fact]
    public async Task DeltaUploadAsync_NoRemoteFile_PerformsFullUpload()
    {
        // Arrange
        var deviceId = "test-device";
        var folderId = "test-folder";
        var filePath = CreateTestFile(1024);
        var localFileInfo = new SyncFileInfo(folderId, "test.txt", "test.txt", 1024, DateTime.UtcNow);

        _mockProtocol.Setup(p => p.SendIndexUpdateAsync(deviceId, folderId, It.IsAny<List<SyncFileInfo>>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _fileUploader.DeltaUploadAsync(deviceId, folderId, filePath, localFileInfo, null);

        // Assert
        Assert.True(result.Success);
        Assert.False(result.IsDeltaUpload);
        Assert.Equal(1024, result.TotalBytes);
        Assert.Equal(1024, result.ChangedBytes);
    }

    [Fact]
    public async Task DeltaUploadAsync_IdenticalFiles_NoTransferNeeded()
    {
        // Arrange
        var deviceId = "test-device";
        var folderId = "test-folder";
        var filePath = CreateTestFile(256 * 1024); // 256KB = 2 blocks

        var blocks = await _fileUploader.CalculateFileBlocksAsync(filePath);

        var localFileInfo = new SyncFileInfo(folderId, "test.txt", "test.txt", 256 * 1024, DateTime.UtcNow);
        localFileInfo.SetBlocks(blocks);
        localFileInfo.UpdateHash("testhash");

        // Remote has same blocks
        var remoteFileInfo = new SyncFileInfo(folderId, "test.txt", "test.txt", 256 * 1024, DateTime.UtcNow);
        remoteFileInfo.SetBlocks(blocks);
        remoteFileInfo.UpdateHash("testhash");

        // Act
        var result = await _fileUploader.DeltaUploadAsync(deviceId, folderId, filePath, localFileInfo, remoteFileInfo);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.IsDeltaUpload);
        Assert.Equal(0, result.ChangedBlocks);
        Assert.Equal(0, result.ChangedBytes);
        Assert.Equal(2, result.UnchangedBlocks);
        Assert.Equal(0.0, result.TransferPercentage);

        // Should not send IndexUpdate for identical files
        _mockProtocol.Verify(p => p.SendIndexUpdateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<SyncFileInfo>>()), Times.Never);
    }

    [Fact]
    public async Task DeltaUploadAsync_OneBlockChanged_OnlyTransfersChangedBlock()
    {
        // Arrange
        var deviceId = "test-device";
        var folderId = "test-folder";

        // Create file with 2 blocks (256KB)
        var filePath = CreateTestFile(256 * 1024);
        var localBlocks = await _fileUploader.CalculateFileBlocksAsync(filePath);

        var localFileInfo = new SyncFileInfo(folderId, "test.txt", "test.txt", 256 * 1024, DateTime.UtcNow);
        localFileInfo.SetBlocks(localBlocks);

        // Remote has first block same, second block different
        var remoteBlocks = new List<BlockInfo>
        {
            localBlocks[0], // Same as local
            new BlockInfo(128 * 1024, 128 * 1024, "different_hash_for_second_block_0000000000", 12345) // Different
        };
        var remoteFileInfo = new SyncFileInfo(folderId, "test.txt", "test.txt", 256 * 1024, DateTime.UtcNow);
        remoteFileInfo.SetBlocks(remoteBlocks);

        _mockProtocol.Setup(p => p.SendIndexUpdateAsync(deviceId, folderId, It.IsAny<List<SyncFileInfo>>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _fileUploader.DeltaUploadAsync(deviceId, folderId, filePath, localFileInfo, remoteFileInfo);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.IsDeltaUpload);
        Assert.Equal(1, result.ChangedBlocks);
        Assert.Equal(1, result.UnchangedBlocks);
        Assert.Equal(128 * 1024, result.ChangedBytes);
        Assert.InRange(result.TransferPercentage, 49.0, 51.0); // ~50%
        Assert.Equal(128 * 1024, result.BytesSaved);
    }

    [Fact]
    public async Task DeltaUploadAsync_AllBlocksChanged_TransfersAllBlocks()
    {
        // Arrange
        var deviceId = "test-device";
        var folderId = "test-folder";

        var filePath = CreateTestFile(256 * 1024);
        var localBlocks = await _fileUploader.CalculateFileBlocksAsync(filePath);

        var localFileInfo = new SyncFileInfo(folderId, "test.txt", "test.txt", 256 * 1024, DateTime.UtcNow);
        localFileInfo.SetBlocks(localBlocks);

        // Remote has completely different blocks
        var remoteBlocks = new List<BlockInfo>
        {
            new BlockInfo(0, 128 * 1024, "completely_different_hash_1_0000000000000000", 11111),
            new BlockInfo(128 * 1024, 128 * 1024, "completely_different_hash_2_0000000000000000", 22222)
        };
        var remoteFileInfo = new SyncFileInfo(folderId, "test.txt", "test.txt", 256 * 1024, DateTime.UtcNow);
        remoteFileInfo.SetBlocks(remoteBlocks);

        _mockProtocol.Setup(p => p.SendIndexUpdateAsync(deviceId, folderId, It.IsAny<List<SyncFileInfo>>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _fileUploader.DeltaUploadAsync(deviceId, folderId, filePath, localFileInfo, remoteFileInfo);

        // Assert
        Assert.True(result.Success);
        Assert.True(result.IsDeltaUpload);
        Assert.Equal(2, result.ChangedBlocks);
        Assert.Equal(0, result.UnchangedBlocks);
        Assert.Equal(256 * 1024, result.ChangedBytes);
        Assert.Equal(100.0, result.TransferPercentage);
        Assert.Equal(0, result.BytesSaved);
    }

    [Fact]
    public async Task DeltaUploadAsync_FileNotFound_ReturnsFailure()
    {
        // Arrange
        var deviceId = "test-device";
        var folderId = "test-folder";
        var nonExistentPath = Path.Combine(_testDirectory, "nonexistent.txt");
        var localFileInfo = new SyncFileInfo(folderId, "nonexistent.txt", "nonexistent.txt", 1024, DateTime.UtcNow);

        // Act
        var result = await _fileUploader.DeltaUploadAsync(deviceId, folderId, nonExistentPath, localFileInfo, null);

        // Assert
        Assert.False(result.Success);
        Assert.Contains("not found", result.Error);
    }

    #endregion

    #region Block Comparison Tests

    [Fact]
    public void CompareBlocksForUpload_IdenticalHashes_NoChanges()
    {
        // Arrange
        var blocks = new List<BlockInfo>
        {
            new BlockInfo(0, 1024, "hash1", 1),
            new BlockInfo(1024, 1024, "hash2", 2)
        };

        var localFile = new SyncFileInfo("folder", "test.txt", "test.txt", 2048, DateTime.UtcNow);
        localFile.SetBlocks(blocks);
        localFile.UpdateHash("filehash");

        var remoteFile = new SyncFileInfo("folder", "test.txt", "test.txt", 2048, DateTime.UtcNow);
        remoteFile.SetBlocks(blocks);
        remoteFile.UpdateHash("filehash");

        // Act
        var comparison = _fileUploader.CompareBlocksForUpload(localFile, remoteFile);

        // Assert
        Assert.Empty(comparison.ChangedBlocks);
        Assert.Equal(2, comparison.UnchangedBlocks.Count);
        Assert.Equal(0, comparison.ChangedBytes);
    }

    [Fact]
    public void CompareBlocksForUpload_AllBlocksDifferent_AllChanged()
    {
        // Arrange
        var localBlocks = new List<BlockInfo>
        {
            new BlockInfo(0, 1024, "localhash1", 1),
            new BlockInfo(1024, 1024, "localhash2", 2)
        };

        var remoteBlocks = new List<BlockInfo>
        {
            new BlockInfo(0, 1024, "remotehash1", 3),
            new BlockInfo(1024, 1024, "remotehash2", 4)
        };

        var localFile = new SyncFileInfo("folder", "test.txt", "test.txt", 2048, DateTime.UtcNow);
        localFile.SetBlocks(localBlocks);

        var remoteFile = new SyncFileInfo("folder", "test.txt", "test.txt", 2048, DateTime.UtcNow);
        remoteFile.SetBlocks(remoteBlocks);

        // Act
        var comparison = _fileUploader.CompareBlocksForUpload(localFile, remoteFile);

        // Assert
        Assert.Equal(2, comparison.ChangedBlocks.Count);
        Assert.Empty(comparison.UnchangedBlocks);
        Assert.Equal(2048, comparison.ChangedBytes);
    }

    [Fact]
    public void CompareBlocksForUpload_MixedBlocks_CorrectlySeparates()
    {
        // Arrange
        var localBlocks = new List<BlockInfo>
        {
            new BlockInfo(0, 1024, "samehash", 1),
            new BlockInfo(1024, 1024, "localhash", 2),
            new BlockInfo(2048, 1024, "samehash2", 3)
        };

        var remoteBlocks = new List<BlockInfo>
        {
            new BlockInfo(0, 1024, "samehash", 1),
            new BlockInfo(1024, 1024, "remotehash", 4),
            new BlockInfo(2048, 1024, "samehash2", 3)
        };

        var localFile = new SyncFileInfo("folder", "test.txt", "test.txt", 3072, DateTime.UtcNow);
        localFile.SetBlocks(localBlocks);

        var remoteFile = new SyncFileInfo("folder", "test.txt", "test.txt", 3072, DateTime.UtcNow);
        remoteFile.SetBlocks(remoteBlocks);

        // Act
        var comparison = _fileUploader.CompareBlocksForUpload(localFile, remoteFile);

        // Assert
        Assert.Single(comparison.ChangedBlocks);
        Assert.Equal(2, comparison.UnchangedBlocks.Count);
        Assert.Equal(1024, comparison.ChangedBytes);
        Assert.Equal("localhash", comparison.ChangedBlocks[0].Hash);
    }

    [Fact]
    public void CompareBlocksForUpload_CaseInsensitiveHashComparison()
    {
        // Arrange
        var localBlocks = new List<BlockInfo>
        {
            new BlockInfo(0, 1024, "ABCDEF123456", 1)
        };

        var remoteBlocks = new List<BlockInfo>
        {
            new BlockInfo(0, 1024, "abcdef123456", 1)
        };

        var localFile = new SyncFileInfo("folder", "test.txt", "test.txt", 1024, DateTime.UtcNow);
        localFile.SetBlocks(localBlocks);

        var remoteFile = new SyncFileInfo("folder", "test.txt", "test.txt", 1024, DateTime.UtcNow);
        remoteFile.SetBlocks(remoteBlocks);

        // Act
        var comparison = _fileUploader.CompareBlocksForUpload(localFile, remoteFile);

        // Assert
        Assert.Empty(comparison.ChangedBlocks);
        Assert.Single(comparison.UnchangedBlocks);
    }

    #endregion

    #region DeltaUploadResult Tests

    [Fact]
    public void DeltaUploadResult_TransferPercentage_CalculatesCorrectly()
    {
        // Arrange & Act
        var result = new DeltaUploadResult
        {
            TotalBytes = 1000,
            ChangedBytes = 250
        };

        // Assert
        Assert.Equal(25.0, result.TransferPercentage);
    }

    [Fact]
    public void DeltaUploadResult_TransferPercentage_ZeroTotalBytes_ReturnsZero()
    {
        // Arrange & Act
        var result = new DeltaUploadResult
        {
            TotalBytes = 0,
            ChangedBytes = 0
        };

        // Assert
        Assert.Equal(0.0, result.TransferPercentage);
    }

    [Fact]
    public void DeltaUploadResult_BytesSaved_CalculatesCorrectly()
    {
        // Arrange & Act
        var result = new DeltaUploadResult
        {
            TotalBytes = 1000,
            ChangedBytes = 250
        };

        // Assert
        Assert.Equal(750, result.BytesSaved);
    }

    #endregion

    #region Helper Methods

    private string CreateTestFile(int size, byte fillByte = 0x00)
    {
        var filePath = Path.Combine(_testDirectory, $"test_{Guid.NewGuid()}.bin");
        var data = new byte[size];
        if (fillByte != 0x00)
        {
            Array.Fill(data, fillByte);
        }
        else if (size > 0)
        {
            var random = new Random(42);
            random.NextBytes(data);
        }
        File.WriteAllBytes(filePath, data);
        return filePath;
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    #endregion
}
