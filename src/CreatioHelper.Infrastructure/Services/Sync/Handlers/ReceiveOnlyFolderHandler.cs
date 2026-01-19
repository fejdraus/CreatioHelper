using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Handlers;

/// <summary>
/// Обработчик режима ReceiveOnly - только получение изменений
/// Аналог receiveonly в Syncthing
/// </summary>
public class ReceiveOnlyFolderHandler : SyncFolderHandlerBase
{
    public ReceiveOnlyFolderHandler(
        ILogger<ReceiveOnlyFolderHandler> logger,
        ConflictResolutionEngine conflictEngine,
        FileDownloader fileDownloader,
        FileUploader fileUploader,
        ISyncProtocol protocol,
        ISyncDatabase database)
        : base(logger, conflictEngine, fileDownloader, fileUploader, protocol, database)
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
        var deviceId = GetActiveDeviceId(folder);

        if (string.IsNullOrEmpty(deviceId))
        {
            _logger.LogWarning("No device available for ReceiveOnly folder {FolderId}", folder.Id);
            return false;
        }

        foreach (var remoteFile in remoteFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            if (!CanApplyFileChange(folder, remoteFile, isIncoming: true))
            {
                _logger.LogDebug("Skipping remote file {FileName} - cannot apply change", remoteFile.FileName);
                continue;
            }

            var localPath = Path.Combine(folder.Path, remoteFile.FileName);
            var syncFileInfo = SyncFileInfo.FromFileMetadata(remoteFile);

            var result = await _fileDownloader.DownloadFileAsync(
                deviceId, folder.Id, syncFileInfo, localPath, cancellationToken);

            if (result.Success)
            {
                // Обновить метаданные
                await _database.FileMetadata.UpsertAsync(remoteFile);
                hasChanges = true;
                _logger.LogInformation("ReceiveOnly: Downloaded {FileName}", remoteFile.FileName);
            }
            else
            {
                _logger.LogError("ReceiveOnly: Failed to download {FileName}: {Error}",
                    remoteFile.FileName, result.Error);
            }
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

        var filesToUpdate = new List<FileMetadata>();
        var directoriesToDelete = new List<string>();
        int revertedCount = 0;

        // 1. Получить все локальные файлы с флагом ReceiveOnly
        var receiveOnlyFiles = await _database.FileMetadata.GetByLocalFlagsAsync(
            folder.Id, FileLocalFlags.ReceiveOnly);

        foreach (var localFile in receiveOnlyFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // Убираем флаг ReceiveOnly
            localFile.LocalFlags &= ~FileLocalFlags.ReceiveOnly;

            // 2. Получить глобальную версию файла
            var globalFile = await _database.FileMetadata.GetGlobalFileAsync(folder.Id, localFile.FileName);

            if (globalFile == null)
            {
                // Файл существует только локально - нужно удалить
                _logger.LogDebug("Revert: File {FileName} exists only locally, marking for deletion",
                    localFile.FileName);

                if (localFile.FileType == FileType.Directory)
                {
                    // Директории откладываем на потом (удаляем от листьев к корню)
                    directoriesToDelete.Add(localFile.FileName);
                }
                else
                {
                    // Удаляем файл с диска
                    var localPath = Path.Combine(folder.Path, localFile.FileName);
                    if (File.Exists(localPath))
                    {
                        try
                        {
                            File.Delete(localPath);
                            _logger.LogDebug("Revert: Deleted local file {FileName}", localFile.FileName);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Revert: Failed to delete file {FileName}", localFile.FileName);
                            continue;
                        }
                    }

                    // Помечаем файл как удаленный с пустой версией
                    localFile.IsDeleted = true;
                    localFile.VersionVector = string.Empty;
                    filesToUpdate.Add(localFile);
                    revertedCount++;
                }
            }
            else if (IsEquivalent(localFile, globalFile, folder))
            {
                // Локальный файл эквивалентен глобальному - просто обновляем метаданные
                _logger.LogDebug("Revert: File {FileName} is equivalent to global, updating metadata",
                    localFile.FileName);

                // Используем глобальную версию
                localFile.VersionVector = globalFile.VersionVector;
                localFile.Hash = globalFile.Hash;
                localFile.Size = globalFile.Size;
                localFile.ModifiedTime = globalFile.ModifiedTime;
                filesToUpdate.Add(localFile);
                revertedCount++;
            }
            else
            {
                // Файл отличается от глобального - сбрасываем версию, чтобы следующий pull заменил его
                _logger.LogDebug("Revert: File {FileName} differs from global, resetting version for re-pull",
                    localFile.FileName);

                localFile.VersionVector = string.Empty; // Empty version = needs to be pulled
                filesToUpdate.Add(localFile);
                revertedCount++;
            }
        }

        // 3. Удаляем директории от листьев к корню
        directoriesToDelete.Sort((a, b) => b.Length.CompareTo(a.Length)); // Сортируем по длине пути (длинные первые)

        foreach (var dirName in directoriesToDelete)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var dirPath = Path.Combine(folder.Path, dirName);
            if (Directory.Exists(dirPath))
            {
                try
                {
                    // Пытаемся удалить только если директория пуста
                    if (!Directory.EnumerateFileSystemEntries(dirPath).Any())
                    {
                        Directory.Delete(dirPath);
                        _logger.LogDebug("Revert: Deleted empty directory {DirName}", dirName);

                        // Добавляем запись об удалении директории
                        filesToUpdate.Add(new FileMetadata
                        {
                            FolderId = folder.Id,
                            FileName = dirName,
                            FileType = FileType.Directory,
                            IsDeleted = true,
                            VersionVector = string.Empty,
                            ModifiedTime = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        });
                        revertedCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Revert: Failed to delete directory {DirName}", dirName);
                }
            }
        }

        // 4. Batch обновление базы данных
        if (filesToUpdate.Count > 0)
        {
            await _database.FileMetadata.BatchUpsertAsync(filesToUpdate);
        }

        _logger.LogInformation("Revert operation completed for folder {FolderId}, reverted {Count} files",
            folder.Id, revertedCount);

        // 5. Инициируем pull чтобы загрузить правильные версии файлов
        if (revertedCount > 0)
        {
            _logger.LogInformation("Revert: Scheduling pull to download correct file versions");
            // Pull будет инициирован SyncEngine при следующем цикле синхронизации
        }

        return revertedCount > 0;
    }

    /// <summary>
    /// Проверить эквивалентность файлов (с учетом настроек папки)
    /// </summary>
    private bool IsEquivalent(FileMetadata local, FileMetadata global, SyncFolder folder)
    {
        // Сравниваем размер
        if (local.Size != global.Size)
            return false;

        // Сравниваем хэш если есть
        if (local.Hash != null && global.Hash != null)
        {
            if (!local.Hash.SequenceEqual(global.Hash))
                return false;
        }

        // Сравниваем время модификации с допуском (ModTimeWindow)
        var timeDiff = Math.Abs((local.ModifiedTime - global.ModifiedTime).TotalSeconds);
        if (timeDiff > folder.ModTimeWindowS)
            return false;

        return true;
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