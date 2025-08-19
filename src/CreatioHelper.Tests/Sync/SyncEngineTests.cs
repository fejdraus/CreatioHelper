using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync;

namespace CreatioHelper.Tests.Sync;

/// <summary>
/// Basic tests for sync engine functionality
/// Based on Syncthing test patterns
/// </summary>
public class SyncEngineTests : IDisposable
{
    private readonly Mock<ILogger<SyncEngine>> _mockLogger;
    private readonly Mock<ISyncProtocol> _mockProtocol;
    private readonly Mock<IDeviceDiscovery> _mockDiscovery;
    private readonly Mock<ILogger<FileWatcher>> _mockFileWatcherLogger;
    private readonly Mock<ILogger<ConflictResolver>> _mockConflictResolverLogger;
    private readonly SyncEngine _syncEngine;
    private readonly string _testDirectory;

    public SyncEngineTests()
    {
        _mockLogger = new Mock<ILogger<SyncEngine>>();
        _mockProtocol = new Mock<ISyncProtocol>();
        _mockDiscovery = new Mock<IDeviceDiscovery>();
        _mockFileWatcherLogger = new Mock<ILogger<FileWatcher>>();
        _mockConflictResolverLogger = new Mock<ILogger<ConflictResolver>>();

        var fileWatcher = new FileWatcher(_mockFileWatcherLogger.Object);
        var conflictResolver = new ConflictResolver(_mockConflictResolverLogger.Object);

        _syncEngine = new SyncEngine(
            _mockLogger.Object,
            _mockProtocol.Object,
            _mockDiscovery.Object,
            fileWatcher,
            conflictResolver);

        _testDirectory = Path.Combine(Path.GetTempPath(), "SyncEngineTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
    }

    [Fact]
    public async Task AddDeviceAsync_ShouldAddDevice_WhenValidParametersProvided()
    {
        // Arrange
        var deviceId = "test-device-123";
        var name = "Test Device";
        var fingerprint = "test-fingerprint";
        var addresses = new List<string> { "tcp://192.168.1.100:22000" };

        // Act
        var device = await _syncEngine.AddDeviceAsync(deviceId, name, fingerprint, addresses);

        // Assert
        Assert.NotNull(device);
        Assert.Equal(deviceId, device.DeviceId);
        Assert.Equal(name, device.Name);
        Assert.Equal(fingerprint, device.CertificateFingerprint);
        Assert.Contains("tcp://192.168.1.100:22000", device.Addresses);
    }

    [Fact]
    public async Task AddFolderAsync_ShouldAddFolder_WhenValidParametersProvided()
    {
        // Arrange
        var folderId = "test-folder-456";
        var label = "Test Folder";
        var path = Path.Combine(_testDirectory, "TestFolder");
        var type = FolderType.SendReceive;

        // Act
        var folder = await _syncEngine.AddFolderAsync(folderId, label, path, type);

        // Assert
        Assert.NotNull(folder);
        Assert.Equal(folderId, folder.FolderId);
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
        var folder = folders.FirstOrDefault(f => f.FolderId == folderId);
        Assert.NotNull(folder);
        Assert.Contains(folder.Devices, d => d.DeviceId == deviceId);
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
        var folder = folders.FirstOrDefault(f => f.FolderId == folderId);
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
        var folder = folders.FirstOrDefault(f => f.FolderId == folderId);
        Assert.NotNull(folder);
        Assert.False(folder.IsPaused);

        var status = await _syncEngine.GetSyncStatusAsync(folderId);
        Assert.Equal(SyncState.Idle, status.State);
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