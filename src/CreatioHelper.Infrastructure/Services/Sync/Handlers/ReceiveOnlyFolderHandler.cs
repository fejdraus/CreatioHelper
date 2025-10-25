#pragma warning disable CS1998 // Async method lacks await (for interface implementation stubs)
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Handlers;

/// <summary>
/// Обработчик режима ReceiveOnly - только получение изменений
/// Аналог receiveonly в Syncthing
/// </summary>
public class ReceiveOnlyFolderHandler : SyncFolderHandlerBase
{
    public ReceiveOnlyFolderHandler(ILogger<ReceiveOnlyFolderHandler> logger, ConflictResolutionEngine conflictEngine) 
        : base(logger, conflictEngine)
    {
    }

    public override SyncFolderType SupportedType => SyncFolderType.ReceiveOnly;

    public override bool CanSendChanges => false;

    public override bool CanReceiveChanges => true;

    public override async Task<bool> PullAsync(SyncFolder folder, IEnumerable<FileMetadata> remoteFiles, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting ReceiveOnly pull for folder {FolderId}", folder.Id);

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

            // TODO: Скачать удаленный файл
            // TODO: Обновить локальные метаданные
            
            hasChanges = true;
        }

        _logger.LogDebug("ReceiveOnly pull completed for folder {FolderId}, hasChanges: {HasChanges}", 
            folder.Id, hasChanges);

        return hasChanges;
    }

    public override async Task<bool> PushAsync(SyncFolder folder, IEnumerable<FileMetadata> localFiles, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting ReceiveOnly push (marking local changes) for folder {FolderId}", folder.Id);

        // В режиме ReceiveOnly мы не отправляем изменения, а только помечаем их флагом
        bool hasLocalChanges = false;
        var localFilesList = localFiles.ToList();

        foreach (var localFile in localFilesList)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // Проверяем, есть ли неожиданные локальные изменения
            if (HasUnexpectedLocalChanges(localFile))
            {
                // Помечаем флагом FlagLocalReceiveOnly
                localFile.LocalFlags |= FileLocalFlags.ReceiveOnly;
                
                _logger.LogDebug("Marked file {FileName} with ReceiveOnly flag due to unexpected local changes", 
                    localFile.FileName);
                
                hasLocalChanges = true;
            }
        }

        _logger.LogDebug("ReceiveOnly push completed for folder {FolderId}, hasLocalChanges: {HasLocalChanges}", 
            folder.Id, hasLocalChanges);

        return hasLocalChanges;
    }

    /// <summary>
    /// Откат локальных изменений к глобальному состоянию (аналог Revert в Syncthing)
    /// </summary>
    public async Task<bool> RevertAsync(SyncFolder folder, CancellationToken cancellationToken = default)
    {
        if (!folder.AllowRevert)
        {
            _logger.LogWarning("Revert not allowed for folder {FolderId}", folder.Id);
            return false;
        }

        _logger.LogInformation("Starting revert operation for ReceiveOnly folder {FolderId}", folder.Id);

        bool hasReverts = false;

        // TODO: Получить все локальные файлы с флагом ReceiveOnly
        // TODO: Для каждого такого файла:
        // 1. Убрать флаг FlagLocalReceiveOnly
        // 2. Если файл существует только локально - удалить его
        // 3. Если файл отличается от глобального - откатить к глобальной версии
        // 4. Обновить локальный индекс

        await Task.CompletedTask;
        
        _logger.LogInformation("Revert operation completed for folder {FolderId}, hasReverts: {HasReverts}", 
            folder.Id, hasReverts);

        return hasReverts;
    }

    public override bool CanApplyFileChange(SyncFolder folder, FileMetadata file, bool isIncoming)
    {
        if (!base.CanApplyFileChange(folder, file, isIncoming))
            return false;

        // В режиме ReceiveOnly принимаем только входящие изменения
        if (!isIncoming)
        {
            // Локальные изменения только помечаем флагом, не отправляем
            return false;
        }

        return true;
    }

    /// <summary>
    /// Проверить, есть ли неожиданные локальные изменения в файле
    /// </summary>
    private bool HasUnexpectedLocalChanges(FileMetadata localFile)
    {
        // TODO: Сравнить с глобальной версией файла
        // Если файл изменен локально, но не должен был изменяться в receive-only папке
        
        // Пока что простая проверка - если файл не помечен флагом, но изменен
        return !localFile.LocalFlags.HasFlag(FileLocalFlags.ReceiveOnly) && 
               !localFile.IsDeleted && 
               localFile.Hash != null && localFile.Hash.Length > 0;
    }

    /// <summary>
    /// Проверить, помечен ли файл как изменный в receive-only папке
    /// </summary>
    public bool IsReceiveOnlyChanged(FileMetadata file)
    {
        return file.LocalFlags.HasFlag(FileLocalFlags.ReceiveOnly);
    }
}