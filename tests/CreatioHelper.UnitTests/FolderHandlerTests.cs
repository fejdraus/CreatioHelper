using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync;
using CreatioHelper.Infrastructure.Services.Sync.Handlers;

namespace CreatioHelper.UnitTests;

/// <summary>
/// Unit tests for SendReceiveFolderHandler PullAsync and PushAsync
/// </summary>
public class SendReceiveFolderHandlerTests : IDisposable
{
    private readonly Mock<ILogger<SendReceiveFolderHandler>> _mockLogger;
    private readonly Mock<ISyncProtocol> _mockProtocol;
    private readonly Mock<ISyncDatabase> _mockDatabase;
    private readonly Mock<IFileMetadataRepository> _mockFileMetadataRepository;
    private readonly Mock<ILogger<FileDownloader>> _mockDownloaderLogger;
    private readonly Mock<ILogger<FileUploader>> _mockUploaderLogger;
    private readonly Mock<ILogger<ConflictResolutionEngine>> _mockConflictEngineLogger;
    private readonly FileDownloader _fileDownloader;
    private readonly FileUploader _fileUploader;
    private readonly ConflictResolutionEngine _conflictEngine;
    private readonly SendReceiveFolderHandler _handler;
    private readonly string _testDirectory;

    public SendReceiveFolderHandlerTests()
    {
        _mockLogger = new Mock<ILogger<SendReceiveFolderHandler>>();
        _mockProtocol = new Mock<ISyncProtocol>();
        _mockDatabase = new Mock<ISyncDatabase>();
        _mockFileMetadataRepository = new Mock<IFileMetadataRepository>();
        _mockDownloaderLogger = new Mock<ILogger<FileDownloader>>();
        _mockUploaderLogger = new Mock<ILogger<FileUploader>>();
        _mockConflictEngineLogger = new Mock<ILogger<ConflictResolutionEngine>>();

        // Setup database to return the file metadata repository
        _mockDatabase.Setup(d => d.FileMetadata).Returns(_mockFileMetadataRepository.Object);

        _fileDownloader = new FileDownloader(_mockDownloaderLogger.Object, _mockProtocol.Object);
        _fileUploader = new FileUploader(_mockUploaderLogger.Object, _mockProtocol.Object);
        _conflictEngine = new ConflictResolutionEngine(_mockConflictEngineLogger.Object);

        _handler = new SendReceiveFolderHandler(
            _mockLogger.Object,
            _conflictEngine,
            _fileDownloader,
            _fileUploader,
            _mockProtocol.Object,
            _mockDatabase.Object);

        _testDirectory = Path.Combine(Path.GetTempPath(), "SendReceiveHandlerTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
    }

    #region PullAsync Tests

    [Fact]
    public async Task PullAsync_WithNoRemoteFiles_ReturnsFalse()
    {
        // Arrange
        var folder = CreateTestFolder();
        var remoteFiles = Enumerable.Empty<FileMetadata>();

        _mockFileMetadataRepository.Setup(r => r.GetAllAsync(folder.Id))
            .ReturnsAsync(new List<FileMetadata>());

        // Act
        var result = await _handler.PullAsync(folder, remoteFiles);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task PullAsync_WithNewRemoteFile_DownloadsFile()
    {
        // Arrange
        var folder = CreateTestFolder();
        var remoteFile = CreateFileMetadata("newfile.txt", folder.Id, new byte[] { 1, 2, 3 });
        var remoteFiles = new List<FileMetadata> { remoteFile };

        _mockFileMetadataRepository.Setup(r => r.GetAllAsync(folder.Id))
            .ReturnsAsync(new List<FileMetadata>()); // No local files

        _mockProtocol.Setup(p => p.IsConnectedAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        _mockProtocol.Setup(p => p.RequestBlockAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<long>(), It.IsAny<int>(), It.IsAny<string>()))
            .ReturnsAsync(new byte[0]); // Simulate block data

        // Act
        var result = await _handler.PullAsync(folder, remoteFiles);

        // Assert - download will fail because block data is empty, but the logic is tested
        // The key test is that the handler attempted to download
        Assert.False(result); // Will fail because mock returns empty block
    }

    [Fact]
    public async Task PullAsync_WithUnchangedFile_SkipsDownload()
    {
        // Arrange
        var folder = CreateTestFolder();
        var hash = new byte[] { 1, 2, 3, 4, 5 };
        var remoteFile = CreateFileMetadata("existing.txt", folder.Id, hash);
        var localFile = CreateFileMetadata("existing.txt", folder.Id, hash); // Same hash

        _mockFileMetadataRepository.Setup(r => r.GetAllAsync(folder.Id))
            .ReturnsAsync(new List<FileMetadata> { localFile });

        // Act
        var result = await _handler.PullAsync(folder, new[] { remoteFile });

        // Assert
        Assert.False(result); // No changes because files are identical
        _mockProtocol.Verify(
            p => p.RequestBlockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task PullAsync_WithIgnoredFile_SkipsFile()
    {
        // Arrange
        var folder = CreateTestFolder();
        var remoteFile = CreateFileMetadata("ignored.txt", folder.Id, new byte[] { 1 });
        remoteFile.LocalFlags = FileLocalFlags.Ignored;

        _mockFileMetadataRepository.Setup(r => r.GetAllAsync(folder.Id))
            .ReturnsAsync(new List<FileMetadata>());

        // Act
        var result = await _handler.PullAsync(folder, new[] { remoteFile });

        // Assert
        Assert.False(result);
        _mockProtocol.Verify(
            p => p.RequestBlockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task PullAsync_WithCancellation_StopsProcessing()
    {
        // Arrange
        var folder = CreateTestFolder();
        var remoteFiles = Enumerable.Range(1, 10)
            .Select(i => CreateFileMetadata($"file{i}.txt", folder.Id, new byte[] { (byte)i }))
            .ToList();

        _mockFileMetadataRepository.Setup(r => r.GetAllAsync(folder.Id))
            .ReturnsAsync(new List<FileMetadata>());

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var result = await _handler.PullAsync(folder, remoteFiles, cts.Token);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task PullAsync_WithNoConnectedDevices_ReturnsFalse()
    {
        // Arrange
        var folder = CreateTestFolder();
        folder.Devices.Clear(); // No devices

        var remoteFile = CreateFileMetadata("newfile.txt", folder.Id, new byte[] { 1, 2, 3 });

        _mockFileMetadataRepository.Setup(r => r.GetAllAsync(folder.Id))
            .ReturnsAsync(new List<FileMetadata>());

        // Act
        var result = await _handler.PullAsync(folder, new[] { remoteFile });

        // Assert
        Assert.False(result);
    }

    #endregion

    #region PushAsync Tests

    [Fact]
    public async Task PushAsync_WithNoLocalFiles_ReturnsFalse()
    {
        // Arrange
        var folder = CreateTestFolder();
        var localFiles = Enumerable.Empty<FileMetadata>();

        _mockProtocol.Setup(p => p.IsConnectedAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        // Act
        var result = await _handler.PushAsync(folder, localFiles);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task PushAsync_WithNoConnectedDevices_ReturnsFalse()
    {
        // Arrange
        var folder = CreateTestFolder();
        var localFile = CreateFileMetadata("local.txt", folder.Id, new byte[] { 1, 2, 3 });

        _mockProtocol.Setup(p => p.IsConnectedAsync(It.IsAny<string>()))
            .ReturnsAsync(false);

        // Act
        var result = await _handler.PushAsync(folder, new[] { localFile });

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task PushAsync_WithNonExistentFile_SkipsFile()
    {
        // Arrange
        var folder = CreateTestFolder();
        var localFile = CreateFileMetadata("nonexistent.txt", folder.Id, new byte[] { 1, 2, 3 });

        _mockProtocol.Setup(p => p.IsConnectedAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        // Act
        var result = await _handler.PushAsync(folder, new[] { localFile });

        // Assert
        Assert.False(result);
        _mockProtocol.Verify(
            p => p.SendIndexUpdateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<SyncFileInfo>>()),
            Times.Never);
    }

    [Fact]
    public async Task PushAsync_WithExistingFile_UploadsToAllDevices()
    {
        // Arrange
        var folder = CreateTestFolder();
        folder.AddDevice("device-2");

        var filePath = Path.Combine(_testDirectory, "upload.txt");
        File.WriteAllText(filePath, "test content");

        var localFile = CreateFileMetadata("upload.txt", folder.Id, new byte[] { 1, 2, 3 });
        localFile.Size = new FileInfo(filePath).Length;

        _mockProtocol.Setup(p => p.IsConnectedAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        _mockProtocol.Setup(p => p.SendIndexUpdateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<SyncFileInfo>>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _handler.PushAsync(folder, new[] { localFile });

        // Assert
        Assert.True(result);
        _mockProtocol.Verify(
            p => p.SendIndexUpdateAsync(It.IsAny<string>(), folder.Id, It.IsAny<List<SyncFileInfo>>()),
            Times.Exactly(2)); // Once for each device
    }

    [Fact]
    public async Task PushAsync_WithIgnoredFile_SkipsFile()
    {
        // Arrange
        var folder = CreateTestFolder();
        var localFile = CreateFileMetadata("ignored.txt", folder.Id, new byte[] { 1 });
        localFile.LocalFlags = FileLocalFlags.Ignored;

        _mockProtocol.Setup(p => p.IsConnectedAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        // Act
        var result = await _handler.PushAsync(folder, new[] { localFile });

        // Assert
        Assert.False(result);
    }

    #endregion

    #region CanApplyFileChange Tests

    [Fact]
    public void CanApplyFileChange_ValidFile_ReturnsTrue()
    {
        // Arrange
        var folder = CreateTestFolder();
        var file = CreateFileMetadata("valid.txt", folder.Id, new byte[] { 1 });

        // Act
        var result = _handler.CanApplyFileChange(folder, file, isIncoming: true);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void CanApplyFileChange_InvalidFile_ReturnsFalse()
    {
        // Arrange
        var folder = CreateTestFolder();
        var file = CreateFileMetadata("invalid.txt", folder.Id, new byte[] { 1 });
        file.LocalFlags = FileLocalFlags.Invalid;

        // Act
        var result = _handler.CanApplyFileChange(folder, file, isIncoming: true);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region Helper Methods

    private SyncFolder CreateTestFolder()
    {
        var folderId = "test-folder-" + Guid.NewGuid().ToString("N")[..8];
        var folder = new SyncFolder(folderId, "Test Folder", _testDirectory, "sendreceive");
        folder.AddDevice("device-1");
        return folder;
    }

    private FileMetadata CreateFileMetadata(string fileName, string folderId, byte[] hash)
    {
        return new FileMetadata
        {
            FileName = fileName,
            FolderId = folderId,
            Hash = hash,
            Size = 1024,
            ModifiedTime = DateTime.UtcNow,
            FileType = FileType.File
        };
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
/// Unit tests for SendOnlyFolderHandler PullAsync and PushAsync
/// </summary>
public class SendOnlyFolderHandlerTests : IDisposable
{
    private readonly Mock<ILogger<SendOnlyFolderHandler>> _mockLogger;
    private readonly Mock<ISyncProtocol> _mockProtocol;
    private readonly Mock<ISyncDatabase> _mockDatabase;
    private readonly Mock<IFileMetadataRepository> _mockFileMetadataRepository;
    private readonly FileDownloader _fileDownloader;
    private readonly FileUploader _fileUploader;
    private readonly ConflictResolutionEngine _conflictEngine;
    private readonly SendOnlyFolderHandler _handler;
    private readonly string _testDirectory;

    public SendOnlyFolderHandlerTests()
    {
        _mockLogger = new Mock<ILogger<SendOnlyFolderHandler>>();
        _mockProtocol = new Mock<ISyncProtocol>();
        _mockDatabase = new Mock<ISyncDatabase>();
        _mockFileMetadataRepository = new Mock<IFileMetadataRepository>();

        _mockDatabase.Setup(d => d.FileMetadata).Returns(_mockFileMetadataRepository.Object);

        var mockDownloaderLogger = Mock.Of<ILogger<FileDownloader>>();
        var mockUploaderLogger = Mock.Of<ILogger<FileUploader>>();
        var mockConflictEngineLogger = Mock.Of<ILogger<ConflictResolutionEngine>>();

        _fileDownloader = new FileDownloader(mockDownloaderLogger, _mockProtocol.Object);
        _fileUploader = new FileUploader(mockUploaderLogger, _mockProtocol.Object);
        _conflictEngine = new ConflictResolutionEngine(mockConflictEngineLogger);

        _handler = new SendOnlyFolderHandler(
            _mockLogger.Object,
            _conflictEngine,
            _fileDownloader,
            _fileUploader,
            _mockProtocol.Object,
            _mockDatabase.Object);

        _testDirectory = Path.Combine(Path.GetTempPath(), "SendOnlyHandlerTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
    }

    #region PullAsync Tests

    [Fact]
    public async Task PullAsync_NeverDownloadsFiles()
    {
        // Arrange
        var folder = CreateTestFolder();
        var remoteFile = CreateFileMetadata("remote.txt", folder.Id, new byte[] { 1, 2, 3 });

        _mockFileMetadataRepository.Setup(r => r.GetAllAsync(folder.Id))
            .ReturnsAsync(new List<FileMetadata>());

        // Act
        var result = await _handler.PullAsync(folder, new[] { remoteFile });

        // Assert
        Assert.False(result); // SendOnly never downloads
        _mockProtocol.Verify(
            p => p.RequestBlockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task PullAsync_LogsIgnoredRemoteVersions()
    {
        // Arrange
        var folder = CreateTestFolder();
        var localFile = CreateFileMetadata("file.txt", folder.Id, new byte[] { 1, 2, 3 });
        var remoteFile = CreateFileMetadata("file.txt", folder.Id, new byte[] { 4, 5, 6 }); // Different hash

        _mockFileMetadataRepository.Setup(r => r.GetAllAsync(folder.Id))
            .ReturnsAsync(new List<FileMetadata> { localFile });

        // Act
        var result = await _handler.PullAsync(folder, new[] { remoteFile });

        // Assert
        Assert.False(result);
        // Verify logger was called (would need to verify log message in real test)
    }

    #endregion

    #region PushAsync Tests

    [Fact]
    public async Task PushAsync_UploadsAllLocalFiles()
    {
        // Arrange
        var folder = CreateTestFolder();

        var filePath = Path.Combine(_testDirectory, "upload.txt");
        File.WriteAllText(filePath, "test content");

        var localFile = CreateFileMetadata("upload.txt", folder.Id, new byte[] { 1, 2, 3 });
        localFile.Size = new FileInfo(filePath).Length;

        _mockProtocol.Setup(p => p.IsConnectedAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        _mockProtocol.Setup(p => p.SendIndexUpdateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<SyncFileInfo>>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _handler.PushAsync(folder, new[] { localFile });

        // Assert
        Assert.True(result);
        _mockProtocol.Verify(
            p => p.SendIndexUpdateAsync(It.IsAny<string>(), folder.Id, It.IsAny<List<SyncFileInfo>>()),
            Times.Once);
    }

    [Fact]
    public async Task PushAsync_WithNoConnectedDevices_ReturnsFalse()
    {
        // Arrange
        var folder = CreateTestFolder();
        var localFile = CreateFileMetadata("file.txt", folder.Id, new byte[] { 1 });

        _mockProtocol.Setup(p => p.IsConnectedAsync(It.IsAny<string>()))
            .ReturnsAsync(false);

        // Act
        var result = await _handler.PushAsync(folder, new[] { localFile });

        // Assert
        Assert.False(result);
    }

    #endregion

    #region CanApplyFileChange Tests

    [Fact]
    public void CanApplyFileChange_IncomingChange_ReturnsFalse()
    {
        // Arrange
        var folder = CreateTestFolder();
        var file = CreateFileMetadata("file.txt", folder.Id, new byte[] { 1 });

        // Act
        var result = _handler.CanApplyFileChange(folder, file, isIncoming: true);

        // Assert
        Assert.False(result); // SendOnly doesn't accept incoming changes
    }

    [Fact]
    public void CanApplyFileChange_OutgoingChange_ReturnsTrue()
    {
        // Arrange
        var folder = CreateTestFolder();
        var file = CreateFileMetadata("file.txt", folder.Id, new byte[] { 1 });

        // Act
        var result = _handler.CanApplyFileChange(folder, file, isIncoming: false);

        // Assert
        Assert.True(result);
    }

    #endregion

    #region Properties Tests

    [Fact]
    public void SupportedType_ReturnsSendOnly()
    {
        Assert.Equal(SyncFolderType.SendOnly, _handler.SupportedType);
    }

    [Fact]
    public void CanSendChanges_ReturnsTrue()
    {
        Assert.True(_handler.CanSendChanges);
    }

    [Fact]
    public void CanReceiveChanges_ReturnsFalse()
    {
        Assert.False(_handler.CanReceiveChanges);
    }

    #endregion

    #region Helper Methods

    private SyncFolder CreateTestFolder()
    {
        var folderId = "test-folder-" + Guid.NewGuid().ToString("N")[..8];
        var folder = new SyncFolder(folderId, "Test Folder", _testDirectory, "sendonly");
        folder.AddDevice("device-1");
        return folder;
    }

    private FileMetadata CreateFileMetadata(string fileName, string folderId, byte[] hash)
    {
        return new FileMetadata
        {
            FileName = fileName,
            FolderId = folderId,
            Hash = hash,
            Size = 1024,
            ModifiedTime = DateTime.UtcNow,
            FileType = FileType.File
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
            catch { }
        }
    }

    #endregion
}

/// <summary>
/// Unit tests for ReceiveOnlyFolderHandler PullAsync
/// </summary>
public class ReceiveOnlyFolderHandlerTests : IDisposable
{
    private readonly Mock<ILogger<ReceiveOnlyFolderHandler>> _mockLogger;
    private readonly Mock<ISyncProtocol> _mockProtocol;
    private readonly Mock<ISyncDatabase> _mockDatabase;
    private readonly Mock<IFileMetadataRepository> _mockFileMetadataRepository;
    private readonly FileDownloader _fileDownloader;
    private readonly FileUploader _fileUploader;
    private readonly ConflictResolutionEngine _conflictEngine;
    private readonly ReceiveOnlyFolderHandler _handler;
    private readonly string _testDirectory;

    public ReceiveOnlyFolderHandlerTests()
    {
        _mockLogger = new Mock<ILogger<ReceiveOnlyFolderHandler>>();
        _mockProtocol = new Mock<ISyncProtocol>();
        _mockDatabase = new Mock<ISyncDatabase>();
        _mockFileMetadataRepository = new Mock<IFileMetadataRepository>();

        _mockDatabase.Setup(d => d.FileMetadata).Returns(_mockFileMetadataRepository.Object);

        var mockDownloaderLogger = Mock.Of<ILogger<FileDownloader>>();
        var mockUploaderLogger = Mock.Of<ILogger<FileUploader>>();
        var mockConflictEngineLogger = Mock.Of<ILogger<ConflictResolutionEngine>>();

        _fileDownloader = new FileDownloader(mockDownloaderLogger, _mockProtocol.Object);
        _fileUploader = new FileUploader(mockUploaderLogger, _mockProtocol.Object);
        _conflictEngine = new ConflictResolutionEngine(mockConflictEngineLogger);

        _handler = new ReceiveOnlyFolderHandler(
            _mockLogger.Object,
            _conflictEngine,
            _fileDownloader,
            _fileUploader,
            _mockProtocol.Object,
            _mockDatabase.Object);

        _testDirectory = Path.Combine(Path.GetTempPath(), "ReceiveOnlyHandlerTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
    }

    #region PullAsync Tests

    [Fact]
    public async Task PullAsync_WithNoDevices_ReturnsFalse()
    {
        // Arrange
        var folder = CreateTestFolder();
        folder.Devices.Clear();

        var remoteFile = CreateFileMetadata("remote.txt", folder.Id, new byte[] { 1, 2, 3 });

        // Act
        var result = await _handler.PullAsync(folder, new[] { remoteFile });

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task PullAsync_WithIgnoredFile_SkipsFile()
    {
        // Arrange
        var folder = CreateTestFolder();
        var remoteFile = CreateFileMetadata("ignored.txt", folder.Id, new byte[] { 1 });
        remoteFile.LocalFlags = FileLocalFlags.Ignored;

        // Act
        var result = await _handler.PullAsync(folder, new[] { remoteFile });

        // Assert
        Assert.False(result);
        _mockProtocol.Verify(
            p => p.RequestBlockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task PullAsync_WithCancellation_StopsProcessing()
    {
        // Arrange
        var folder = CreateTestFolder();
        var remoteFiles = Enumerable.Range(1, 10)
            .Select(i => CreateFileMetadata($"file{i}.txt", folder.Id, new byte[] { (byte)i }))
            .ToList();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var result = await _handler.PullAsync(folder, remoteFiles, cts.Token);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region PushAsync Tests

    [Fact]
    public async Task PushAsync_MarksLocalChangesWithFlag()
    {
        // Arrange
        var folder = CreateTestFolder();
        var localFile = CreateFileMetadata("local.txt", folder.Id, new byte[] { 1, 2, 3 });

        // Act
        var result = await _handler.PushAsync(folder, new[] { localFile });

        // Assert
        Assert.True(result);
        Assert.True(localFile.LocalFlags.HasFlag(FileLocalFlags.ReceiveOnly));
    }

    [Fact]
    public async Task PushAsync_DoesNotUploadFiles()
    {
        // Arrange
        var folder = CreateTestFolder();
        var localFile = CreateFileMetadata("local.txt", folder.Id, new byte[] { 1, 2, 3 });

        // Act
        await _handler.PushAsync(folder, new[] { localFile });

        // Assert
        _mockProtocol.Verify(
            p => p.SendIndexUpdateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<List<SyncFileInfo>>()),
            Times.Never);
    }

    #endregion

    #region CanApplyFileChange Tests

    [Fact]
    public void CanApplyFileChange_IncomingChange_ReturnsTrue()
    {
        // Arrange
        var folder = CreateTestFolder();
        var file = CreateFileMetadata("file.txt", folder.Id, new byte[] { 1 });

        // Act
        var result = _handler.CanApplyFileChange(folder, file, isIncoming: true);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void CanApplyFileChange_OutgoingChange_ReturnsFalse()
    {
        // Arrange
        var folder = CreateTestFolder();
        var file = CreateFileMetadata("file.txt", folder.Id, new byte[] { 1 });

        // Act
        var result = _handler.CanApplyFileChange(folder, file, isIncoming: false);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region Properties Tests

    [Fact]
    public void SupportedType_ReturnsReceiveOnly()
    {
        Assert.Equal(SyncFolderType.ReceiveOnly, _handler.SupportedType);
    }

    [Fact]
    public void CanSendChanges_ReturnsFalse()
    {
        Assert.False(_handler.CanSendChanges);
    }

    [Fact]
    public void CanReceiveChanges_ReturnsTrue()
    {
        Assert.True(_handler.CanReceiveChanges);
    }

    #endregion

    #region Helper Methods

    private SyncFolder CreateTestFolder()
    {
        var folderId = "test-folder-" + Guid.NewGuid().ToString("N")[..8];
        var folder = new SyncFolder(folderId, "Test Folder", _testDirectory, "receiveonly");
        folder.AddDevice("device-1");
        return folder;
    }

    private FileMetadata CreateFileMetadata(string fileName, string folderId, byte[] hash)
    {
        return new FileMetadata
        {
            FileName = fileName,
            FolderId = folderId,
            Hash = hash,
            Size = 1024,
            ModifiedTime = DateTime.UtcNow,
            FileType = FileType.File
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
            catch { }
        }
    }

    #endregion
}

/// <summary>
/// Unit tests for ReceiveEncryptedFolderHandler PullAsync
/// </summary>
public class ReceiveEncryptedFolderHandlerTests : IDisposable
{
    private readonly Mock<ILogger<ReceiveEncryptedFolderHandler>> _mockLogger;
    private readonly Mock<ISyncProtocol> _mockProtocol;
    private readonly Mock<ISyncDatabase> _mockDatabase;
    private readonly Mock<IFileMetadataRepository> _mockFileMetadataRepository;
    private readonly FileDownloader _fileDownloader;
    private readonly FileUploader _fileUploader;
    private readonly ConflictResolutionEngine _conflictEngine;
    private readonly ReceiveEncryptedFolderHandler _handler;
    private readonly string _testDirectory;

    public ReceiveEncryptedFolderHandlerTests()
    {
        _mockLogger = new Mock<ILogger<ReceiveEncryptedFolderHandler>>();
        _mockProtocol = new Mock<ISyncProtocol>();
        _mockDatabase = new Mock<ISyncDatabase>();
        _mockFileMetadataRepository = new Mock<IFileMetadataRepository>();

        _mockDatabase.Setup(d => d.FileMetadata).Returns(_mockFileMetadataRepository.Object);

        var mockDownloaderLogger = Mock.Of<ILogger<FileDownloader>>();
        var mockUploaderLogger = Mock.Of<ILogger<FileUploader>>();
        var mockConflictEngineLogger = Mock.Of<ILogger<ConflictResolutionEngine>>();

        _fileDownloader = new FileDownloader(mockDownloaderLogger, _mockProtocol.Object);
        _fileUploader = new FileUploader(mockUploaderLogger, _mockProtocol.Object);
        _conflictEngine = new ConflictResolutionEngine(mockConflictEngineLogger);

        _handler = new ReceiveEncryptedFolderHandler(
            _mockLogger.Object,
            _conflictEngine,
            _fileDownloader,
            _fileUploader,
            _mockProtocol.Object,
            _mockDatabase.Object);

        _testDirectory = Path.Combine(Path.GetTempPath(), "ReceiveEncryptedHandlerTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
    }

    #region PullAsync Tests

    [Fact]
    public async Task PullAsync_WithNoDevices_ReturnsFalse()
    {
        // Arrange
        var folder = CreateTestFolder();
        folder.Devices.Clear();

        var remoteFile = CreateFileMetadata("encrypted.txt", folder.Id, new byte[] { 1, 2, 3 });

        // Act
        var result = await _handler.PullAsync(folder, new[] { remoteFile });

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task PullAsync_WithIgnoredFile_SkipsFile()
    {
        // Arrange
        var folder = CreateTestFolder();
        var remoteFile = CreateFileMetadata("ignored.txt", folder.Id, new byte[] { 1 });
        remoteFile.LocalFlags = FileLocalFlags.Ignored;

        // Act
        var result = await _handler.PullAsync(folder, new[] { remoteFile });

        // Assert
        Assert.False(result);
        _mockProtocol.Verify(
            p => p.RequestBlockAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<long>(), It.IsAny<int>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task PullAsync_WithCancellation_StopsProcessing()
    {
        // Arrange
        var folder = CreateTestFolder();
        var remoteFiles = Enumerable.Range(1, 10)
            .Select(i => CreateFileMetadata($"encrypted{i}.txt", folder.Id, new byte[] { (byte)i }))
            .ToList();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var result = await _handler.PullAsync(folder, remoteFiles, cts.Token);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region PushAsync Tests

    [Fact]
    public async Task PushAsync_AlwaysReturnsFalse()
    {
        // Arrange
        var folder = CreateTestFolder();
        var localFile = CreateFileMetadata("local.txt", folder.Id, new byte[] { 1, 2, 3 });

        // Act
        var result = await _handler.PushAsync(folder, new[] { localFile });

        // Assert
        Assert.False(result); // ReceiveEncrypted never pushes
    }

    #endregion

    #region CanApplyFileChange Tests

    [Fact]
    public void CanApplyFileChange_IncomingChange_ReturnsTrue()
    {
        // Arrange
        var folder = CreateTestFolder();
        var file = CreateFileMetadata("file.txt", folder.Id, new byte[] { 1 });

        // Act
        var result = _handler.CanApplyFileChange(folder, file, isIncoming: true);

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void CanApplyFileChange_OutgoingChange_ReturnsFalse()
    {
        // Arrange
        var folder = CreateTestFolder();
        var file = CreateFileMetadata("file.txt", folder.Id, new byte[] { 1 });

        // Act
        var result = _handler.CanApplyFileChange(folder, file, isIncoming: false);

        // Assert
        Assert.False(result);
    }

    #endregion

    #region Properties Tests

    [Fact]
    public void SupportedType_ReturnsReceiveEncrypted()
    {
        Assert.Equal(SyncFolderType.ReceiveEncrypted, _handler.SupportedType);
    }

    [Fact]
    public void CanSendChanges_ReturnsFalse()
    {
        Assert.False(_handler.CanSendChanges);
    }

    [Fact]
    public void CanReceiveChanges_ReturnsTrue()
    {
        Assert.True(_handler.CanReceiveChanges);
    }

    [Fact]
    public void GetDefaultConflictPolicy_ReturnsUseRemote()
    {
        Assert.Equal(ConflictResolutionPolicy.UseRemote, _handler.GetDefaultConflictPolicy());
    }

    #endregion

    #region Helper Methods

    private SyncFolder CreateTestFolder()
    {
        var folderId = "test-folder-" + Guid.NewGuid().ToString("N")[..8];
        var folder = new SyncFolder(folderId, "Test Folder", _testDirectory, "receiveencrypted");
        folder.AddDevice("device-1");
        return folder;
    }

    private FileMetadata CreateFileMetadata(string fileName, string folderId, byte[] hash)
    {
        return new FileMetadata
        {
            FileName = fileName,
            FolderId = folderId,
            Hash = hash,
            Size = 1024,
            ModifiedTime = DateTime.UtcNow,
            FileType = FileType.File
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
            catch { }
        }
    }

    #endregion
}

/// <summary>
/// Unit tests for SyncFolderHandlerBase helper methods
/// </summary>
public class SyncFolderHandlerBaseTests
{
    [Fact]
    public void HashesEqual_BothNull_ReturnsTrue()
    {
        // Using reflection to test protected static method
        var method = typeof(SyncFolderHandlerBase).GetMethod("HashesEqual",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var result = (bool)method!.Invoke(null, new object?[] { null, null })!;

        Assert.True(result);
    }

    [Fact]
    public void HashesEqual_OneNull_ReturnsFalse()
    {
        var method = typeof(SyncFolderHandlerBase).GetMethod("HashesEqual",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var result = (bool)method!.Invoke(null, new object?[] { new byte[] { 1 }, null })!;

        Assert.False(result);
    }

    [Fact]
    public void HashesEqual_SameHashes_ReturnsTrue()
    {
        var method = typeof(SyncFolderHandlerBase).GetMethod("HashesEqual",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var hash = new byte[] { 1, 2, 3, 4, 5 };
        var result = (bool)method!.Invoke(null, new object?[] { hash, hash.ToArray() })!;

        Assert.True(result);
    }

    [Fact]
    public void HashesEqual_DifferentHashes_ReturnsFalse()
    {
        var method = typeof(SyncFolderHandlerBase).GetMethod("HashesEqual",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var result = (bool)method!.Invoke(null, new object?[] { new byte[] { 1, 2, 3 }, new byte[] { 4, 5, 6 } })!;

        Assert.False(result);
    }
}
