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

        var filesToUpdate = new List<FileMetadata>();
        int overrideCount = 0;

        // 1. Получить все файлы которые отличаются от локальных (needed globally)
        var neededFiles = await _database.FileMetadata.GetNeededFilesAsync(folder.Id);

        foreach (var neededFile in neededFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // Получаем локальную версию файла
            var localFile = await _database.FileMetadata.GetAsync(folder.Id, neededFile.FileName);

            // Пропускаем файлы в плохом состоянии (ignored, unsupported, etc.)
            if (localFile != null && localFile.IsInvalid)
            {
                _logger.LogDebug("Override: Skipping invalid file {FileName}", neededFile.FileName);
                continue;
            }

            if (localFile == null || localFile.FileName != neededFile.FileName)
            {
                // Файл отсутствует локально - помечаем как удаленный в глобальном индексе
                _logger.LogDebug("Override: Marking {FileName} as deleted (not present locally)",
                    neededFile.FileName);

                neededFile.IsDeleted = true;
                neededFile.ModifiedBy = GetLocalDeviceId();
                neededFile.ModifiedTime = DateTime.UtcNow;
                neededFile.Sequence = 0;
                filesToUpdate.Add(neededFile);
                overrideCount++;
            }
            else
            {
                // Файл существует локально - используем нашу версию
                _logger.LogDebug("Override: Replacing global version of {FileName} with local",
                    neededFile.FileName);

                // Объединяем версии (merge) и обновляем
                var mergedVersion = MergeVersionVectors(localFile.VersionVector, neededFile.VersionVector);
                localFile.VersionVector = IncrementVersion(mergedVersion, GetLocalDeviceId());
                localFile.Sequence = 0;
                localFile.UpdatedAt = DateTime.UtcNow;
                filesToUpdate.Add(localFile);
                overrideCount++;
            }
        }

        // 2. Batch обновление базы данных
        if (filesToUpdate.Count > 0)
        {
            await _database.FileMetadata.BatchUpsertAsync(filesToUpdate);
        }

        _logger.LogInformation("Override operation completed for folder {FolderId}, overrode {Count} files",
            folder.Id, overrideCount);

        return overrideCount > 0;
    }

    /// <summary>
    /// Получить локальный Device ID
    /// </summary>
    private string GetLocalDeviceId()
    {
        // TODO: Получить реальный локальный device ID из конфигурации
        return "local";
    }

    /// <summary>
    /// Объединить два вектора версий
    /// </summary>
    private string MergeVersionVectors(string v1, string v2)
    {
        // Простая реализация - берем максимальную версию
        // В реальности нужно объединять компоненты вектора
        if (string.IsNullOrEmpty(v1)) return v2;
        if (string.IsNullOrEmpty(v2)) return v1;

        // Парсим версии и объединяем
        // Формат: "device1:seq1,device2:seq2,..."
        var result = new Dictionary<string, long>();

        foreach (var part in (v1 + "," + v2).Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split(':');
            if (kv.Length == 2 && long.TryParse(kv[1], out var seq))
            {
                var device = kv[0];
                if (!result.ContainsKey(device) || result[device] < seq)
                {
                    result[device] = seq;
                }
            }
        }

        return string.Join(",", result.Select(kv => $"{kv.Key}:{kv.Value}"));
    }

    /// <summary>
    /// Инкрементировать версию для устройства
    /// </summary>
    private string IncrementVersion(string version, string deviceId)
    {
        var parts = new Dictionary<string, long>();

        if (!string.IsNullOrEmpty(version))
        {
            foreach (var part in version.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = part.Split(':');
                if (kv.Length == 2 && long.TryParse(kv[1], out var seq))
                {
                    parts[kv[0]] = seq;
                }
            }
        }

        // Инкрементируем версию для нашего устройства
        parts[deviceId] = parts.GetValueOrDefault(deviceId, 0) + 1;

        return string.Join(",", parts.Select(kv => $"{kv.Key}:{kv.Value}"));
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