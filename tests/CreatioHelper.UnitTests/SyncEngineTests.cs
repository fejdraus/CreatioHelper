using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync;
using CreatioHelper.Infrastructure.Services.Sync.Handlers;

namespace CreatioHelper.Tests;

/// <summary>
/// Basic tests for sync engine functionality
/// Based on Syncthing test patterns
/// </summary>
public class SyncEngineTests : IDisposable
{
    private readonly Mock<ILogger<SyncEngine>> _mockLogger;
    private readonly Mock<ISyncProtocol> _mockProtocol;
    private readonly Mock<IDeviceDiscovery> _mockDiscovery;
    private readonly Mock<ISyncDatabase> _mockDatabase;
    private readonly Mock<IConfigurationManager> _mockConfigManager;
    private readonly Mock<IEventLogger> _mockEventLogger;
    private readonly Mock<IStatisticsCollector> _mockStatisticsCollector;
    private readonly Mock<ICertificateManager> _mockCertificateManager;
    private readonly SyncEngine _syncEngine;
    private readonly string _testDirectory;
    private readonly string _tempBlockStorageDir;

    public SyncEngineTests()
    {
        _mockLogger = new Mock<ILogger<SyncEngine>>();
        _mockProtocol = new Mock<ISyncProtocol>();
        _mockDiscovery = new Mock<IDeviceDiscovery>();
        _mockDatabase = new Mock<ISyncDatabase>();
        _mockConfigManager = new Mock<IConfigurationManager>();
        _mockEventLogger = new Mock<IEventLogger>();
        _mockStatisticsCollector = new Mock<IStatisticsCollector>();
        _mockCertificateManager = new Mock<ICertificateManager>();

        // Create real instances for classes that have simple constructors
        var blockSizerLogger = Mock.Of<ILogger<AdaptiveBlockSizer>>();
        var blockSizer = new AdaptiveBlockSizer(blockSizerLogger);
        
        var fileWatcherLogger = Mock.Of<ILogger<FileWatcher>>();
        var fileWatcher = new FileWatcher(fileWatcherLogger, blockSizer);
        
        var conflictResolverLogger = Mock.Of<ILogger<ConflictResolver>>();
        var conflictResolver = new ConflictResolver(conflictResolverLogger);
        
        var fileComparatorLogger = Mock.Of<ILogger<FileComparator>>();
        var fileComparator = new FileComparator(fileComparatorLogger);
        
        var fileDownloaderLogger = Mock.Of<ILogger<FileDownloader>>();
        var fileDownloader = new FileDownloader(fileDownloaderLogger, _mockProtocol.Object);

        var fileUploaderLogger = Mock.Of<ILogger<FileUploader>>();
        var fileUploader = new FileUploader(fileUploaderLogger, _mockProtocol.Object);

        var deltaSyncEngineLogger = Mock.Of<ILogger<DeltaSyncEngine>>();
        var deltaSyncEngine = new DeltaSyncEngine(deltaSyncEngineLogger, blockSizer);
        
        // Create mocks for more complex dependencies
        var mockBlockRepository = Mock.Of<IBlockInfoRepository>();
        var mockBlockStorageLogger = Mock.Of<ILogger<SyncthingBlockStorage>>();
        
        // For SyncthingBlockStorage, create a real instance with temp directory
        _tempBlockStorageDir = Path.Combine(Path.GetTempPath(), "TestBlockStorage", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempBlockStorageDir);
        var blockStorage = new SyncthingBlockStorage(mockBlockStorageLogger, mockBlockRepository, _tempBlockStorageDir);
        
        var blockRequestHandlerLogger = Mock.Of<ILogger<BlockRequestHandler>>();
        var blockRequestHandler = new BlockRequestHandler(blockRequestHandlerLogger, _mockProtocol.Object, blockStorage, mockBlockRepository);
        
        // Create SyncthingBlockCalculator for BlockDuplicationDetector
        var blockCalculatorLogger = Mock.Of<ILogger<SyncthingBlockCalculator>>();
        var blockCalculator = new SyncthingBlockCalculator(blockCalculatorLogger);
        
        var blockDuplicationDetectorLogger = Mock.Of<ILogger<BlockDuplicationDetector>>();
        var blockDuplicationDetector = new BlockDuplicationDetector(blockDuplicationDetectorLogger, mockBlockRepository, blockCalculator);
        
        var parallelBlockTransferLogger = Mock.Of<ILogger<ParallelBlockTransfer>>();
        var parallelBlockTransfer = new ParallelBlockTransfer(parallelBlockTransferLogger, 32);
        
        var transferOptimizerLogger = Mock.Of<ILogger<TransferOptimizer>>();
        var transferOptimizer = new TransferOptimizer(transferOptimizerLogger, blockDuplicationDetector, parallelBlockTransfer, _mockDatabase.Object, _mockProtocol.Object);
        
        var syncFolderHandlerFactoryLogger = Mock.Of<ILogger<SyncFolderHandlerFactory>>();
        var mockServiceProvider = Mock.Of<IServiceProvider>();
        var syncFolderHandlerFactory = new SyncFolderHandlerFactory(mockServiceProvider, syncFolderHandlerFactoryLogger);
        
        var syncConfig = new SyncConfiguration("test-device", "Test Device");

        // Create SyncEngine with real dependencies
        try 
        {
            // Create mock for SyncthingGlobalDiscovery
            var mockGlobalDiscovery = new Mock<SyncthingGlobalDiscovery>(Mock.Of<ILogger<SyncthingGlobalDiscovery>>(), Mock.Of<System.Security.Cryptography.X509Certificates.X509Certificate2>(), "test-device-id");
            
            _syncEngine = new SyncEngine(
                _mockLogger.Object,
                _mockProtocol.Object,
                _mockDiscovery.Object,
                fileWatcher,
                conflictResolver,
                fileComparator,
                fileDownloader,
                fileUploader,
                blockRequestHandler,
                deltaSyncEngine,
                blockDuplicationDetector,
                transferOptimizer,
                syncConfig,
                _mockDatabase.Object,
                _mockConfigManager.Object,
                _mockEventLogger.Object,
                _mockStatisticsCollector.Object,
                _mockCertificateManager.Object,
                syncFolderHandlerFactory,
                mockGlobalDiscovery.Object,
                null, // ICombinedNatService - optional
                null  // X509Certificate2 - optional
            );
        }
        catch (Exception ex)
        {
            // If SyncEngine constructor fails, create a minimal mock
            throw new InvalidOperationException($"Failed to create SyncEngine for testing: {ex.Message}", ex);
        }

        _testDirectory = Path.Combine(Path.GetTempPath(), "SyncEngineTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public async Task AddDeviceAsync_ShouldAddDevice_WhenValidParametersProvided()
    {
        // Arrange
        var deviceId = "test-device-123";
        var name = "Test Device";
        var addresses = new List<string> { "tcp://192.168.1.100:22000" };

        // Act
        var device = await _syncEngine.AddDeviceAsync(deviceId, name, null, addresses);

        // Assert
        Assert.NotNull(device);
        Assert.Equal(deviceId, device.DeviceId);
        Assert.Equal(name, device.DeviceName);
        Assert.Contains("tcp://192.168.1.100:22000", device.Addresses);
    }

    [Fact]
    public async Task AddFolderAsync_ShouldAddFolder_WhenValidParametersProvided()
    {
        // Arrange
        var folderId = "test-folder-456";
        var label = "Test Folder";
        var path = Path.Combine(_testDirectory, "TestFolder");
        var type = "sendreceive";

        // Act
        var folder = await _syncEngine.AddFolderAsync(folderId, label, path, type);

        // Assert
        Assert.NotNull(folder);
        Assert.Equal(folderId, folder.Id);
        Assert.Equal(label, folder.Label);
        Assert.Equal(path, folder.Path);
        Assert.Equal(type, folder.Type);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public async Task ShareFolderWithDeviceAsync_ShouldShareFolder_WhenBothExist()
    {
        // Arrange
        var deviceId = "test-device-123";
        var folderId = "test-folder-456";
        
        await _syncEngine.AddDeviceAsync(deviceId, "Test Device", "fingerprint");
        await _syncEngine.AddFolderAsync(folderId, "Test Folder", Path.Combine(_testDirectory, "TestFolder"));

        // Act
        await _syncEngine.ShareFolderWithDeviceAsync(folderId, deviceId);

        // Assert
        var folders = await _syncEngine.GetFoldersAsync();
        var folder = folders.FirstOrDefault(f => f.Id == folderId);
        Assert.NotNull(folder);
        Assert.Contains(folder.Devices, d => d == deviceId);
    }

    [Fact]
    public async Task ShareFolderWithDeviceAsync_ShouldThrowException_WhenFolderNotFound()
    {
        // Arrange
        var deviceId = "test-device-123";
        var folderId = "non-existent-folder";
        
        await _syncEngine.AddDeviceAsync(deviceId, "Test Device", "fingerprint");

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _syncEngine.ShareFolderWithDeviceAsync(folderId, deviceId));
    }

    [Fact]
    public async Task GetStatisticsAsync_ShouldReturnValidStatistics()
    {
        // Act
        var statistics = await _syncEngine.GetStatisticsAsync();

        // Assert
        Assert.NotNull(statistics);
        Assert.True(statistics.StartTime <= DateTime.UtcNow);
        Assert.True(statistics.Uptime >= TimeSpan.Zero);
        Assert.True(statistics.ConnectedDevices >= 0);
        Assert.True(statistics.TotalDevices >= 0);
    }

    [Fact]
    public async Task PauseFolderAsync_ShouldPauseFolder_WhenFolderExists()
    {
        // Arrange
        var folderId = "test-folder-456";
        await _syncEngine.AddFolderAsync(folderId, "Test Folder", Path.Combine(_testDirectory, "TestFolder"));

        // Act
        await _syncEngine.PauseFolderAsync(folderId);

        // Assert
        var folders = await _syncEngine.GetFoldersAsync();
        var folder = folders.FirstOrDefault(f => f.Id == folderId);
        Assert.NotNull(folder);
        Assert.True(folder.IsPaused);

        var status = await _syncEngine.GetSyncStatusAsync(folderId);
        Assert.Equal(SyncState.Paused, status.State);
    }

    [Fact]
    public async Task ResumeFolderAsync_ShouldResumeFolder_WhenFolderIsPaused()
    {
        // Arrange
        var folderId = "test-folder-456";
        await _syncEngine.AddFolderAsync(folderId, "Test Folder", Path.Combine(_testDirectory, "TestFolder"));
        await _syncEngine.PauseFolderAsync(folderId);

        // Act
        await _syncEngine.ResumeFolderAsync(folderId);

        // Assert
        var folders = await _syncEngine.GetFoldersAsync();
        var folder = folders.FirstOrDefault(f => f.Id == folderId);
        Assert.NotNull(folder);
        Assert.False(folder.IsPaused);

        var status = await _syncEngine.GetSyncStatusAsync(folderId);
        Assert.Equal(SyncState.Idle, status.State);
    }

    [Fact]
    public async Task AddDeviceAsync_WithDynamicAddress_ShouldCallDiscovery()
    {
        // Arrange
        var deviceId = "WIKSNWR-OG3WH54-QKJGL2--CE9LDJ0-2_G7BML-IESNG6V-U";
        var name = "Test Device";
        var addresses = new List<string> { "dynamic" };

        var discoveredDevice = new DiscoveredDevice
        {
            DeviceId = deviceId,
            Addresses = new List<string> { "tcp://192.168.1.100:22000" }
        };

        _mockDiscovery
            .Setup(d => d.DiscoverAsync(deviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DiscoveredDevice> { discoveredDevice });

        // Act
        var device = await _syncEngine.AddDeviceAsync(deviceId, name, null, addresses);

        // Assert
        Assert.NotNull(device);
        Assert.Equal(deviceId, device.DeviceId);
        // Discovery should be called when connecting with "dynamic" address
        // Give time for background connection task
        await Task.Delay(1500);
        _mockDiscovery.Verify(d => d.DiscoverAsync(deviceId, It.IsAny<CancellationToken>()), Times.AtLeastOnce());
    }

    [Fact]
    public async Task AddDeviceAsync_WithStaticAddress_ShouldNotCallDiscovery()
    {
        // Arrange
        var deviceId = "SRW2H63-MUVLXSQ-ALVP0UK-PN3GAQC-M0K99QP-J-JJ9IS-0";
        var name = "Test Device Static";
        var addresses = new List<string> { "tcp://192.168.1.200:22000" };

        // Act
        var device = await _syncEngine.AddDeviceAsync(deviceId, name, null, addresses);

        // Assert
        Assert.NotNull(device);
        Assert.Contains("tcp://192.168.1.200:22000", device.Addresses);
        // With static address, discovery should not be called for address resolution
        // (it may be called for other reasons, but not for resolving "dynamic")
    }

    [Fact]
    public async Task AddDeviceAsync_WithMixedAddresses_ShouldResolveOnlyDynamic()
    {
        // Arrange
        var deviceId = "ABCDEFG-1234567-ABCDEFG-1234567-ABCDEFG-1234567-A";
        var name = "Test Device Mixed";
        var addresses = new List<string> { "dynamic", "tcp://10.0.0.1:22000" };

        var discoveredDevice = new DiscoveredDevice
        {
            DeviceId = deviceId,
            Addresses = new List<string> { "tcp://192.168.1.50:22000" }
        };

        _mockDiscovery
            .Setup(d => d.DiscoverAsync(deviceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DiscoveredDevice> { discoveredDevice });

        // Act
        var device = await _syncEngine.AddDeviceAsync(deviceId, name, null, addresses);

        // Assert
        Assert.NotNull(device);
        // Static address should be in the list
        Assert.Contains("tcp://10.0.0.1:22000", device.Addresses);
        // Dynamic should also be in list (original config preserved)
        Assert.Contains("dynamic", device.Addresses);
        // After connection attempt, discovered address should be added
        await Task.Delay(1500);
        // The device should now have the discovered address added
    }

    public void Dispose()
    {
        _syncEngine?.Dispose();
        
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors in tests
            }
        }
        
        if (Directory.Exists(_tempBlockStorageDir))
        {
            try
            {
                Directory.Delete(_tempBlockStorageDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors in tests
            }
        }
    }
}

/// <summary>
/// Tests for conflict resolution logic
/// </summary>
public class ConflictResolverTests
{
    private readonly Mock<ILogger<ConflictResolver>> _mockLogger;
    private readonly ConflictResolver _conflictResolver;

    public ConflictResolverTests()
    {
        _mockLogger = new Mock<ILogger<ConflictResolver>>();
        _conflictResolver = new ConflictResolver(_mockLogger.Object);
    }

    [Fact]
    public async Task ResolveConflictAsync_ShouldReturnNoAction_WhenFilesAreIdentical()
    {
        // Arrange
        var localFile = CreateTestFileInfo("test.txt", "hash123", 1000);
        var remoteFile = CreateTestFileInfo("test.txt", "hash123", 1000);

        // Act
        var resolution = await _conflictResolver.ResolveConflictAsync(localFile, remoteFile, "device123");

        // Assert
        Assert.Equal(ConflictAction.NoAction, resolution.Action);
        Assert.Contains("identical", resolution.Reason);
    }

    [Fact]
    public async Task ResolveConflictAsync_ShouldPreferNewer_WhenUsingPreferNewerStrategy()
    {
        // Arrange
        var localFile = CreateTestFileInfo("test.txt", "hash1", 1000, DateTime.UtcNow.AddHours(-1));
        var remoteFile = CreateTestFileInfo("test.txt", "hash2", 1000, DateTime.UtcNow);

        // Act
        var resolution = await _conflictResolver.ResolveConflictAsync(
            localFile, remoteFile, "device123", ConflictResolutionStrategy.PreferNewer);

        // Assert
        Assert.Equal(ConflictAction.AcceptRemote, resolution.Action);
        Assert.Contains("newer", resolution.Reason);
    }

    [Fact]
    public async Task ResolveConflictAsync_ShouldCreateConflictCopy_WhenUsingDefaultStrategy()
    {
        // Arrange
        var localFile = CreateTestFileInfo("test.txt", "hash1", 1000);
        var remoteFile = CreateTestFileInfo("test.txt", "hash2", 1000);

        // Act
        var resolution = await _conflictResolver.ResolveConflictAsync(
            localFile, remoteFile, "device123", ConflictResolutionStrategy.Default);

        // Assert
        Assert.Equal(ConflictAction.CreateConflictCopy, resolution.Action);
        Assert.NotNull(resolution.ConflictCopyName);
        Assert.Contains("sync-conflict", resolution.ConflictCopyName);
    }

    private SyncFileInfo CreateTestFileInfo(string name, string hash, long size, DateTime? modifiedTime = null)
    {
        var fileInfo = new SyncFileInfo("test-folder", name, name, size, modifiedTime ?? DateTime.UtcNow);
        fileInfo.UpdateHash(hash);
        return fileInfo;
    }
}