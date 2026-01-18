#pragma warning disable CS1998 // Async method lacks await (for interface implementation stubs)
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Handlers;

/// <summary>
/// Обработчик режима SendOnly - только отправка изменений
/// Аналог sendonly в Syncthing
/// </summary>
public class SendOnlyFolderHandler : SyncFolderHandlerBase
{
    public SendOnlyFolderHandler(
        ILogger<SendOnlyFolderHandler> logger,
        ConflictResolutionEngine conflictEngine,
        FileDownloader fileDownloader,
        FileUploader fileUploader,
        ISyncProtocol protocol,
        ISyncDatabase database)
        : base(logger, conflictEngine, fileDownloader, fileUploader, protocol, database)
    {
    }

    public override SyncFolderType SupportedType => SyncFolderType.SendOnly;

    public override bool CanSendChanges => true;

    public override bool CanReceiveChanges => false;

    public override async Task<bool> PullAsync(SyncFolder folder, IEnumerable<FileMetadata> remoteFiles,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting SendOnly pull (metadata only) for folder {FolderId}", folder.Id);

        // SendOnly НЕ скачивает файлы, только обновляет метаданные для сравнения
        bool hasChanges = false;
        var localFiles = await GetLocalFilesAsync(folder.Id);
        var localDict = localFiles.ToDictionary(f => f.FileName);

        foreach (var remoteFile in remoteFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // Пометить удалённый файл как игнорируемый если он отличается
            if (localDict.TryGetValue(remoteFile.FileName, out var localFile))
            {
                if (!HashesEqual(localFile.Hash, remoteFile.Hash))
                {
                    // Удалённая версия отличается - игнорируем её
                    _logger.LogDebug("SendOnly: Ignoring remote version of {FileName}", remoteFile.FileName);
                }
            }
        }

        _logger.LogDebug("SendOnly pull completed for folder {FolderId}, hasChanges: {HasChanges}",
            folder.Id, hasChanges);

        return hasChanges;
    }

    public override async Task<bool> PushAsync(SyncFolder folder, IEnumerable<FileMetadata> localFiles,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting SendOnly push for folder {FolderId}", folder.Id);

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
                    _logger.LogInformation("SendOnly: Uploaded {FileName} to {DeviceId}",
                        localFile.FileName, deviceId);
                }
                else
                {
                    _logger.LogError("SendOnly: Failed to upload {FileName} to {DeviceId}: {Error}",
                        localFile.FileName, deviceId, result.Error);
                }
            }
        }

        _logger.LogDebug("SendOnly push completed for folder {FolderId}, hasChanges: {HasChanges}",
            folder.Id, hasChanges);

        return hasChanges;
    }

    /// <summary>
    /// Принудительная перезапись удаленного состояния (аналог Override в Syncthing)
    /// </summary>
    public async Task<bool> OverrideAsync(SyncFolder folder, CancellationToken cancellationToken = default)
    {
        if (!folder.AllowOverride)
        {
            _logger.LogWarning("Override not allowed for folder {FolderId}", folder.Id);
            return false;
        }

        _logger.LogInformation("Starting override operation for SendOnly folder {FolderId}", folder.Id);

        // TODO: Реализация принудительной перезаписи глобального состояния локальным
        // Аналог Override в Syncthing:
        // 1. Для каждого файла в need (нужного глобально, но отличающегося от локального)
        // 2. Если локально нет файла - помечаем как удаленный в глобальном индексе
        // 3. Если локально есть файл - объединяем версии и обновляем глобальный индекс

        await Task.CompletedTask;
        return true;
    }

    public override bool CanApplyFileChange(SyncFolder folder, FileMetadata file, bool isIncoming)
    {
        if (!base.CanApplyFileChange(folder, file, isIncoming))
            return false;

        // В режиме SendOnly не принимаем входящие изменения содержимого
        if (isIncoming)
        {
            // Принимаем только метаданные и флаги игнорирования
            return file.LocalFlags.HasFlag(FileLocalFlags.Ignored) || 
                   file.LocalFlags.HasFlag(FileLocalFlags.Unsupported);
        }

        return true;
    }
}