#pragma warning disable CS1998 // Async method lacks await (for interface implementation stubs)
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Handlers;

/// <summary>
/// Обработчик режима SendReceive - полная двусторонняя синхронизация
/// Аналог sendreceive в Syncthing
/// </summary>
public class SendReceiveFolderHandler : SyncFolderHandlerBase
{
    public SendReceiveFolderHandler(
        ILogger<SendReceiveFolderHandler> logger,
        ConflictResolutionEngine conflictEngine,
        FileDownloader fileDownloader,
        FileUploader fileUploader,
        ISyncProtocol protocol,
        ISyncDatabase database)
        : base(logger, conflictEngine, fileDownloader, fileUploader, protocol, database)
    {
    }

    public override SyncFolderType SupportedType => SyncFolderType.SendReceive;

    public override bool CanSendChanges => true;

    public override bool CanReceiveChanges => true;

    public override async Task<bool> PullAsync(SyncFolder folder, IEnumerable<FileMetadata> remoteFiles,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting SendReceive pull for folder {FolderId}", folder.Id);

        bool hasChanges = false;
        var localFiles = await GetLocalFilesAsync(folder.Id);
        var localDict = localFiles.ToDictionary(f => f.FileName);

        foreach (var remoteFile in remoteFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            if (!CanApplyFileChange(folder, remoteFile, isIncoming: true))
            {
                _logger.LogDebug("Skipping remote file {FileName} - cannot apply change", remoteFile.FileName);
                continue;
            }

            var needsDownload = false;
            FileMetadata? localFile = null;

            if (!localDict.TryGetValue(remoteFile.FileName, out localFile))
            {
                // Новый файл
                needsDownload = true;
                _logger.LogDebug("New file detected: {FileName}", remoteFile.FileName);
            }
            else if (!HashesEqual(localFile.Hash, remoteFile.Hash))
            {
                // Файл изменился - проверить конфликт
                if (_conflictEngine.IsInConflict(localFile, remoteFile))
                {
                    // Конфликт будет обработан через ResolveConflictsAsync
                    _logger.LogDebug("Conflict detected for file {FileName}, skipping pull", remoteFile.FileName);
                    continue;
                }
                needsDownload = true;
                _logger.LogDebug("File changed: {FileName}", remoteFile.FileName);
            }

            if (needsDownload)
            {
                var localPath = Path.Combine(folder.Path, remoteFile.FileName);
                var syncFileInfo = SyncFileInfo.FromFileMetadata(remoteFile);
                var deviceId = GetActiveDeviceId(folder);

                if (string.IsNullOrEmpty(deviceId))
                {
                    _logger.LogWarning("No device available for downloading {FileName}", remoteFile.FileName);
                    continue;
                }

                var result = await _fileDownloader.DownloadFileAsync(
                    deviceId, folder.Id, syncFileInfo, localPath, cancellationToken);

                if (result.Success)
                {
                    await _database.FileMetadata.UpsertAsync(remoteFile);
                    hasChanges = true;
                    _logger.LogInformation("Downloaded {FileName} from device {DeviceId}", remoteFile.FileName, deviceId);
                }
                else
                {
                    _logger.LogError("Failed to download {FileName}: {Error}", remoteFile.FileName, result.Error);
                }
            }
        }

        _logger.LogDebug("SendReceive pull completed for folder {FolderId}, hasChanges: {HasChanges}",
            folder.Id, hasChanges);

        return hasChanges;
    }

    public override async Task<bool> PushAsync(SyncFolder folder, IEnumerable<FileMetadata> localFiles,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting SendReceive push for folder {FolderId}", folder.Id);

        bool hasChanges = false;
        var connectedDevices = await GetConnectedDevicesAsync(folder, cancellationToken);

        if (!connectedDevices.Any())
        {
            _logger.LogDebug("No connected devices for folder {FolderId}", folder.Id);
            return false;
        }

        foreach (var localFile in localFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            if (!CanApplyFileChange(folder, localFile, isIncoming: false))
            {
                _logger.LogDebug("Skipping local file {FileName} - cannot apply change", localFile.FileName);
                continue;
            }

            var localPath = Path.Combine(folder.Path, localFile.FileName);
            if (!File.Exists(localPath))
            {
                _logger.LogDebug("Skipping local file {FileName} - file does not exist", localFile.FileName);
                continue;
            }

            var syncFileInfo = SyncFileInfo.FromFileMetadata(localFile);

            foreach (var deviceId in connectedDevices)
            {
                var result = await _fileUploader.UploadFileAsync(
                    deviceId, folder.Id, localPath, syncFileInfo, cancellationToken);

                if (result.Success)
                {
                    hasChanges = true;
                    _logger.LogInformation("Uploaded {FileName} to device {DeviceId}", localFile.FileName, deviceId);
                }
                else
                {
                    _logger.LogError("Failed to upload {FileName} to device {DeviceId}: {Error}",
                        localFile.FileName, deviceId, result.Error);
                }
            }
        }

        _logger.LogDebug("SendReceive push completed for folder {FolderId}, hasChanges: {HasChanges}",
            folder.Id, hasChanges);

        return hasChanges;
    }

    public override bool CanApplyFileChange(SyncFolder folder, FileMetadata file, bool isIncoming)
    {
        if (!base.CanApplyFileChange(folder, file, isIncoming))
            return false;

        // В режиме SendReceive принимаем все валидные изменения
        return true;
    }
}