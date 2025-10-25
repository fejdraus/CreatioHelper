#pragma warning disable CS1998 // Async method lacks await (for interface implementation stubs)
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Handlers;

/// <summary>
/// Обработчик режима SendOnly - только отправка изменений
/// Аналог sendonly в Syncthing
/// </summary>
public class SendOnlyFolderHandler : SyncFolderHandlerBase
{
    public SendOnlyFolderHandler(ILogger<SendOnlyFolderHandler> logger, ConflictResolutionEngine conflictEngine) 
        : base(logger, conflictEngine)
    {
    }

    public override SyncFolderType SupportedType => SyncFolderType.SendOnly;

    public override bool CanSendChanges => true;

    public override bool CanReceiveChanges => false;

    public override async Task<bool> PullAsync(SyncFolder folder, IEnumerable<FileMetadata> remoteFiles, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting SendOnly pull (metadata only) for folder {FolderId}", folder.Id);

        // В режиме SendOnly мы не получаем содержимое файлов, только метаданные
        bool hasMetadataUpdates = false;
        var remoteFilesList = remoteFiles.ToList();

        foreach (var remoteFile in remoteFilesList)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // Игнорируем файлы, которые нельзя обработать
            if (remoteFile.LocalFlags.HasFlag(FileLocalFlags.Ignored))
            {
                remoteFile.LocalFlags |= FileLocalFlags.Ignored;
                hasMetadataUpdates = true;
                continue;
            }

            // Проверяем эквивалентность без содержимого (только метаданные)
            // TODO: Сравнить с локальной версией на основе времени модификации, размера, прав доступа
            // TODO: Обновить метаданные если необходимо
            
            hasMetadataUpdates = true;
        }

        _logger.LogDebug("SendOnly pull completed for folder {FolderId}, hasMetadataUpdates: {HasMetadataUpdates}", 
            folder.Id, hasMetadataUpdates);

        return hasMetadataUpdates;
    }

    public override async Task<bool> PushAsync(SyncFolder folder, IEnumerable<FileMetadata> localFiles, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting SendOnly push for folder {FolderId}", folder.Id);

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

            // TODO: Отправить локальные изменения на все устройства
            // TODO: Обновить глобальный индекс с локальными изменениями
            
            hasChanges = true;
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