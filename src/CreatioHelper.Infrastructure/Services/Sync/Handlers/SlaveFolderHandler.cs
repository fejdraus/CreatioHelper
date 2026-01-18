using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Handlers;

/// <summary>
/// Обработчик режима Slave - расширенный ReceiveOnly с автоматическим откатом
/// Кастомный режим для иерархических конфигураций
/// </summary>
public class SlaveFolderHandler : ReceiveOnlyFolderHandler
{
    public SlaveFolderHandler(
        ILogger<SlaveFolderHandler> logger,
        ConflictResolutionEngine conflictEngine,
        FileDownloader fileDownloader,
        FileUploader fileUploader,
        ISyncProtocol protocol,
        ISyncDatabase database)
        : base(logger, conflictEngine, fileDownloader, fileUploader, protocol, database)
    {
    }

    public override SyncFolderType SupportedType => SyncFolderType.Slave;

    public override async Task<bool> PullAsync(SyncFolder folder, IEnumerable<FileMetadata> remoteFiles, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting Slave pull (forced acceptance) for folder {FolderId}", folder.Id);

        // Slave режим принимает все входящие изменения без вопросов
        var result = await base.PullAsync(folder, remoteFiles, cancellationToken);

        // Автоматически откатываем любые локальные изменения
        await AutoRevertLocalChanges(folder, cancellationToken);

        return result;
    }

    public override async Task<bool> PushAsync(SyncFolder folder, IEnumerable<FileMetadata> localFiles, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting Slave push (automatic revert) for folder {FolderId}", folder.Id);

        // В Slave режиме не отправляем изменения, а автоматически откатываем их
        var localFilesList = localFiles.ToList();
        bool hasLocalChanges = false;

        foreach (var localFile in localFilesList)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // Проверяем, есть ли неавторизованные локальные изменения
            if (HasUnauthorizedLocalChanges(localFile))
            {
                // Помечаем для автоматического отката
                localFile.LocalFlags |= FileLocalFlags.ReceiveOnly;
                
                _logger.LogDebug("Slave folder detected unauthorized change in {FileName}, marking for auto-revert", 
                    localFile.FileName);
                
                hasLocalChanges = true;
            }
        }

        // Автоматически откатываем помеченные файлы
        if (hasLocalChanges)
        {
            await RevertAsync(folder, cancellationToken);
        }

        return hasLocalChanges;
    }

    public override async Task ResolveConflictsAsync(SyncFolder folder, IEnumerable<FileConflict> conflicts, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Slave folder resolving {ConflictCount} conflicts with revert policy", conflicts.Count());

        foreach (var conflict in conflicts)
        {
            // В Slave режиме всегда используем удаленную версию
            var resolution = new ConflictResolution
            {
                Action = ConflictAction.Revert,
                Winner = conflict.RemoteFile.ToFileMetadata(),
                Reason = "Slave folder always reverts to remote versions"
            };

            await ApplyConflictResolutionAsync(folder, conflict, resolution, cancellationToken);
        }
    }

    public override bool CanApplyFileChange(SyncFolder folder, FileMetadata file, bool isIncoming)
    {
        if (!base.CanApplyFileChange(folder, file, isIncoming))
            return false;

        // Slave режим принимает только входящие изменения
        if (!isIncoming)
        {
            _logger.LogDebug("Slave folder rejecting outgoing change for {FileName} - slaves don't send changes", 
                file.FileName);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Автоматический откат локальных изменений
    /// </summary>
    private async Task AutoRevertLocalChanges(SyncFolder folder, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Slave folder performing automatic revert of local changes for {FolderId}", folder.Id);

        // TODO: Найти все файлы с флагом ReceiveOnly или локальными изменениями
        // TODO: Автоматически откатить их к глобальному состоянию

        await Task.CompletedTask;
    }

    /// <summary>
    /// Проверить, есть ли неавторизованные локальные изменения
    /// </summary>
    private bool HasUnauthorizedLocalChanges(FileMetadata localFile)
    {
        // В Slave режиме любые локальные изменения считаются неавторизованными
        if (localFile.LocalFlags.HasFlag(FileLocalFlags.ReceiveOnly))
            return true;

        // TODO: Сравнить с глобальной версией файла
        // Если файл изменен локально, но не должен был изменяться в slave папке
        
        return !localFile.IsDeleted && localFile.Hash != null && localFile.Hash.Length > 0;
    }

    /// <summary>
    /// Принудительная синхронизация со всеми мастерами
    /// </summary>
    public async Task<bool> ForceSyncWithMastersAsync(SyncFolder folder, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Slave folder forcing sync with masters for {FolderId}", folder.Id);

        // TODO: Найти все устройства с Master режимом для этой папки
        // TODO: Запросить полный индекс у всех мастеров
        // TODO: Принудительно принять все изменения от мастеров
        // TODO: Откатить все локальные изменения

        await Task.CompletedTask;
        return true;
    }

    /// <summary>
    /// Блокировка локальных изменений в Slave режиме
    /// </summary>
    public bool IsLocalChangeAllowed(SyncFolder folder, FileMetadata file)
    {
        // В Slave режиме локальные изменения не разрешены
        _logger.LogWarning("Local change attempted on Slave folder {FolderId} for file {FileName} - blocked", 
            folder.Id, file.FileName);
        return false;
    }

    /// <summary>
    /// Проверка целостности Slave папки
    /// </summary>
    public async Task<bool> ValidateIntegrityAsync(SyncFolder folder, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Validating Slave folder integrity for {FolderId}", folder.Id);

        // TODO: Проверить, что нет файлов с локальными изменениями
        // TODO: Проверить, что все файлы соответствуют глобальному состоянию
        // TODO: Автоматически исправить любые расхождения

        await Task.CompletedTask;
        return true;
    }
}