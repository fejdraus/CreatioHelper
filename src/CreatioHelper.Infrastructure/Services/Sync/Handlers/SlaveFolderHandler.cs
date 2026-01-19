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

        // Получаем файлы с локальными изменениями (флаг ReceiveOnly или LocallyChanged)
        var receiveOnlyFiles = await _database.FileMetadata.GetByLocalFlagsAsync(
            folder.Id, FileLocalFlags.ReceiveOnly);

        if (receiveOnlyFiles.Any())
        {
            _logger.LogInformation("Slave folder auto-reverting {Count} files with local changes",
                receiveOnlyFiles.Count());

            // Используем базовый метод Revert из ReceiveOnlyFolderHandler
            await RevertAsync(folder, cancellationToken);
        }
    }

    /// <summary>
    /// Проверить, есть ли неавторизованные локальные изменения
    /// </summary>
    private async Task<bool> HasUnauthorizedLocalChangesAsync(FileMetadata localFile)
    {
        // В Slave режиме любые локальные изменения считаются неавторизованными
        if (localFile.LocalFlags.HasFlag(FileLocalFlags.ReceiveOnly))
            return true;

        // Сравниваем с глобальной версией файла
        var globalFile = await _database.FileMetadata.GetGlobalFileAsync(localFile.FolderId, localFile.FileName);

        if (globalFile == null)
        {
            // Файл существует только локально - это неавторизованное изменение
            return !localFile.IsDeleted;
        }

        // Сравниваем хэши
        if (localFile.Hash != null && globalFile.Hash != null)
        {
            if (!localFile.Hash.SequenceEqual(globalFile.Hash))
                return true;
        }

        // Сравниваем размер
        if (localFile.Size != globalFile.Size)
            return true;

        return false;
    }

    /// <summary>
    /// Синхронная проверка неавторизованных изменений (для использования в цикле)
    /// </summary>
    private bool HasUnauthorizedLocalChanges(FileMetadata localFile)
    {
        // В Slave режиме любые локальные изменения считаются неавторизованными
        if (localFile.LocalFlags.HasFlag(FileLocalFlags.ReceiveOnly))
            return true;

        // Базовая проверка - если файл помечен как локально измененный
        if (localFile.LocallyChanged)
            return true;

        return false;
    }

    /// <summary>
    /// Принудительная синхронизация со всеми мастерами
    /// </summary>
    public async Task<bool> ForceSyncWithMastersAsync(SyncFolder folder, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Slave folder forcing sync with masters for {FolderId}", folder.Id);

        int syncedDevices = 0;

        // 1. Найти все устройства с доступом к этой папке (потенциальные мастера)
        foreach (var deviceId in folder.Devices)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                _logger.LogDebug("Requesting full index from device {DeviceId} for folder {FolderId}",
                    deviceId, folder.Id);

                // 2. Запросить полный индекс у устройства через протокол
                var remoteIndex = await _protocol.RequestIndexAsync(deviceId, folder.Id);

                if (remoteIndex != null && remoteIndex.Any())
                {
                    _logger.LogInformation("Received {Count} files from master device {DeviceId}",
                        remoteIndex.Count(), deviceId);

                    // 3. Конвертируем SyncFileInfo в FileMetadata и принудительно принимаем все изменения
                    var remoteFiles = remoteIndex.Select(f => f.ToFileMetadata());
                    await base.PullAsync(folder, remoteFiles, cancellationToken);
                    syncedDevices++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync with device {DeviceId}", deviceId);
            }
        }

        // 4. Откатить все оставшиеся локальные изменения
        await RevertAsync(folder, cancellationToken);

        _logger.LogInformation("Force sync completed for folder {FolderId}, synced with {Count} devices",
            folder.Id, syncedDevices);

        return syncedDevices > 0;
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

        var issues = new List<string>();

        // 1. Проверить, что нет файлов с локальными изменениями
        var receiveOnlyFiles = await _database.FileMetadata.GetByLocalFlagsAsync(
            folder.Id, FileLocalFlags.ReceiveOnly);

        if (receiveOnlyFiles.Any())
        {
            issues.Add($"Found {receiveOnlyFiles.Count()} files with unauthorized local changes");
        }

        // 2. Проверить все локальные файлы на соответствие глобальному состоянию
        var allLocalFiles = await _database.FileMetadata.GetAllAsync(folder.Id);
        int mismatchCount = 0;

        foreach (var localFile in allLocalFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            if (await HasUnauthorizedLocalChangesAsync(localFile))
            {
                mismatchCount++;
            }
        }

        if (mismatchCount > 0)
        {
            issues.Add($"Found {mismatchCount} files not matching global state");
        }

        // 3. Автоматически исправить расхождения если есть
        if (issues.Count > 0)
        {
            _logger.LogWarning("Slave folder {FolderId} has integrity issues: {Issues}",
                folder.Id, string.Join("; ", issues));

            // Автоматический откат
            await RevertAsync(folder, cancellationToken);

            _logger.LogInformation("Slave folder {FolderId} integrity restored via automatic revert", folder.Id);
            return false; // Были проблемы, но исправлены
        }

        _logger.LogDebug("Slave folder {FolderId} integrity validated successfully", folder.Id);
        return true; // Целостность в порядке
    }
}