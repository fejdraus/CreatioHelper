#pragma warning disable CS1998 // Async method lacks await (for interface implementation stubs)
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Handlers;

/// <summary>
/// Обработчик режима SendReceive - полная двусторонняя синхронизация
/// Аналог sendreceive в Syncthing
/// </summary>
public class SendReceiveFolderHandler : SyncFolderHandlerBase
{
    public SendReceiveFolderHandler(ILogger<SendReceiveFolderHandler> logger, ConflictResolutionEngine conflictEngine) 
        : base(logger, conflictEngine)
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
        var remoteFilesList = remoteFiles.ToList();

        foreach (var remoteFile in remoteFilesList)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            if (!CanApplyFileChange(folder, remoteFile, isIncoming: true))
            {
                _logger.LogDebug("Skipping remote file {FileName} - cannot apply change", remoteFile.FileName);
                continue;
            }

            // TODO: Сравнить с локальной версией файла
            // TODO: Обработать различия (скачивание, обновление метаданных)
            
            hasChanges = true;
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
        var localFilesList = localFiles.ToList();

        foreach (var localFile in localFilesList)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            if (!CanApplyFileChange(folder, localFile, isIncoming: false))
            {
                _logger.LogDebug("Skipping local file {FileName} - cannot apply change", localFile.FileName);
                continue;
            }

            // TODO: Отправить файл на удаленные устройства
            // TODO: Обновить глобальный индекс
            
            hasChanges = true;
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