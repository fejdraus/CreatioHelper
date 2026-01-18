#pragma warning disable CS1998 // Async method lacks await (for interface implementation stubs)
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Handlers;

/// <summary>
/// Обработчик режима ReceiveEncrypted - получение зашифрованных данных
/// Аналог receiveencrypted в Syncthing
/// Данные получаются и сохраняются в зашифрованном виде без расшифровки
/// </summary>
public class ReceiveEncryptedFolderHandler : SyncFolderHandlerBase
{
    public ReceiveEncryptedFolderHandler(
        ILogger<ReceiveEncryptedFolderHandler> logger,
        ConflictResolutionEngine conflictEngine,
        FileDownloader fileDownloader,
        FileUploader fileUploader,
        ISyncProtocol protocol,
        ISyncDatabase database)
        : base(logger, conflictEngine, fileDownloader, fileUploader, protocol, database)
    {
    }

    public override SyncFolderType SupportedType => SyncFolderType.ReceiveEncrypted;

    public override bool CanSendChanges => false;

    public override bool CanReceiveChanges => true;

    public override async Task<bool> PullAsync(SyncFolder folder, IEnumerable<FileMetadata> remoteFiles,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting ReceiveEncrypted pull for folder {FolderId}", folder.Id);

        bool hasChanges = false;
        var deviceId = GetActiveDeviceId(folder);

        if (string.IsNullOrEmpty(deviceId))
        {
            _logger.LogWarning("No device available for ReceiveEncrypted folder {FolderId}", folder.Id);
            return false;
        }

        foreach (var remoteFile in remoteFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            if (!CanApplyFileChange(folder, remoteFile, isIncoming: true))
            {
                _logger.LogDebug("Skipping encrypted remote file {FileName} - cannot apply change", remoteFile.FileName);
                continue;
            }

            // В режиме ReceiveEncrypted файлы принимаются в зашифрованном виде
            // и сохраняются без расшифровки
            _logger.LogTrace("Receiving encrypted file {FileName} without decryption", remoteFile.FileName);

            // Сохраняем в зашифрованном виде (путь остается тем же)
            var localPath = Path.Combine(folder.Path, remoteFile.FileName);
            var syncFileInfo = SyncFileInfo.FromFileMetadata(remoteFile);

            // Скачиваем файл напрямую без расшифровки
            var result = await _fileDownloader.DownloadFileAsync(
                deviceId, folder.Id, syncFileInfo, localPath, cancellationToken);

            if (result.Success)
            {
                // Обновить метаданные с пометкой о шифровании
                remoteFile.LocalFlags |= FileLocalFlags.Encrypted;
                await _database.FileMetadata.UpsertAsync(remoteFile);
                hasChanges = true;
                _logger.LogInformation("ReceiveEncrypted: Downloaded encrypted {FileName}",
                    remoteFile.FileName);
            }
            else
            {
                _logger.LogError("ReceiveEncrypted: Failed to download {FileName}: {Error}",
                    remoteFile.FileName, result.Error);
            }
        }

        _logger.LogDebug("ReceiveEncrypted pull completed for folder {FolderId}, hasChanges: {HasChanges}",
            folder.Id, hasChanges);

        return hasChanges;
    }

    public override async Task<bool> PushAsync(SyncFolder folder, IEnumerable<FileMetadata> localFiles, 
        CancellationToken cancellationToken = default)
    {
        // ReceiveEncrypted не отправляет локальные изменения
        _logger.LogDebug("ReceiveEncrypted mode - skipping push for folder {FolderId}", folder.Id);
        return false;
    }

    // Note: ResolveConflictAsync removed as it may not exist in base class

    public override ConflictResolutionPolicy GetDefaultConflictPolicy()
    {
        return ConflictResolutionPolicy.UseRemote;
    }

    public override bool CanApplyFileChange(SyncFolder folder, FileMetadata file, bool isIncoming)
    {
        // В режиме ReceiveEncrypted принимаются только входящие изменения
        if (!isIncoming)
        {
            _logger.LogTrace("ReceiveEncrypted mode - rejecting outgoing change for {FileName}", file.FileName);
            return false;
        }

        // Дополнительная проверка на то, что файл является зашифрованным
        // В реальной реализации здесь была бы проверка флагов шифрования
        _logger.LogTrace("ReceiveEncrypted mode - accepting encrypted incoming change for {FileName}", file.FileName);
        
        return base.CanApplyFileChange(folder, file, isIncoming);
    }

    /// <summary>
    /// Проверить, является ли файл зашифрованным
    /// </summary>
    protected virtual bool IsEncryptedFile(FileMetadata file)
    {
        // В будущем здесь будет проверка флагов шифрования из BEP протокола
        // Пока возвращаем true для всех файлов в ReceiveEncrypted папке
        return true;
    }

    /// <summary>
    /// Получить информацию о шифровании для файла
    /// </summary>
    protected virtual EncryptionInfo GetEncryptionInfo(FileMetadata file)
    {
        // В будущем здесь будет извлечение информации о шифровании из метаданных
        return new EncryptionInfo
        {
            IsEncrypted = true,
            Algorithm = "AES-256-GCM", // Syncthing использует AES-256-GCM
            KeyVersion = 1
        };
    }
}

/// <summary>
/// Информация о шифровании файла
/// </summary>
public class EncryptionInfo
{
    public bool IsEncrypted { get; set; }
    public string? Algorithm { get; set; }
    public int KeyVersion { get; set; }
    public DateTime? EncryptedAt { get; set; }
}