using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Handlers;

/// <summary>
/// Обработчик режима Master - расширенный SendOnly с принудительной перезаписью
/// Кастомный режим для иерархических конфигураций
/// </summary>
public class MasterFolderHandler : SendOnlyFolderHandler
{
    public MasterFolderHandler(
        ILogger<MasterFolderHandler> logger,
        ConflictResolutionEngine conflictEngine,
        FileDownloader fileDownloader,
        FileUploader fileUploader,
        ISyncProtocol protocol,
        ISyncDatabase database)
        : base(logger, conflictEngine, fileDownloader, fileUploader, protocol, database)
    {
    }

    public override SyncFolderType SupportedType => SyncFolderType.Master;

    public override async Task<bool> PullAsync(SyncFolder folder, IEnumerable<FileMetadata> remoteFiles, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting Master pull (metadata only + conflict detection) for folder {FolderId}", folder.Id);

        // Master режим наследует поведение SendOnly, но более агрессивно обрабатывает конфликты
        var result = await base.PullAsync(folder, remoteFiles, cancellationToken);

        // Дополнительная логика для Master режима
        await DetectAndResolveConflictsAggressively(folder, remoteFiles, cancellationToken);

        return result;
    }

    public override async Task ResolveConflictsAsync(SyncFolder folder, IEnumerable<FileConflict> conflicts, 
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Master folder resolving {ConflictCount} conflicts with override policy", conflicts.Count());

        foreach (var conflict in conflicts)
        {
            // В Master режиме всегда используем локальную версию
            var resolution = new ConflictResolution
            {
                Action = ConflictAction.Override,
                Winner = conflict.LocalFile.ToFileMetadata(),
                Reason = "Master folder always overrides remote versions"
            };

            await ApplyConflictResolutionAsync(folder, conflict, resolution, cancellationToken);
        }
    }

    public override bool CanApplyFileChange(SyncFolder folder, FileMetadata file, bool isIncoming)
    {
        if (!base.CanApplyFileChange(folder, file, isIncoming))
            return false;

        // Master режим более строгий в отношении входящих изменений
        if (isIncoming)
        {
            _logger.LogDebug("Master folder rejecting incoming change for {FileName} - masters don't accept remote changes", 
                file.FileName);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Агрессивное обнаружение и разрешение конфликтов в Master режиме
    /// </summary>
    private async Task DetectAndResolveConflictsAggressively(SyncFolder folder, IEnumerable<FileMetadata> remoteFiles,
        CancellationToken cancellationToken)
    {
        var conflicts = new List<FileConflict>();

        foreach (var remoteFile in remoteFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            // Получить локальную версию файла из базы данных
            var localFile = await GetLocalFileAsync(folder.Id, remoteFile.FileName);
            if (localFile != null && _conflictEngine.IsInConflict(localFile, remoteFile))
            {
                conflicts.Add(new FileConflict(
                    localFile.FileName,
                    _conflictEngine.DetermineConflictType(localFile, remoteFile),
                    SyncFileInfo.FromFileMetadata(localFile),
                    SyncFileInfo.FromFileMetadata(remoteFile)
                ));
            }
        }

        if (conflicts.Any())
        {
            _logger.LogInformation("Master folder detected {ConflictCount} conflicts, resolving with override", conflicts.Count);
            await ResolveConflictsAsync(folder, conflicts, cancellationToken);
        }
    }

    /// <summary>
    /// Автоматическая принудительная перезапись при обнаружении расхождений
    /// </summary>
    public async Task<bool> AutoOverrideAsync(SyncFolder folder, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting automatic override for Master folder {FolderId}", folder.Id);

        var localFiles = await GetLocalFilesAsync(folder.Id);
        var connectedDevices = await GetConnectedDevicesAsync(folder, cancellationToken);

        if (!connectedDevices.Any())
        {
            _logger.LogWarning("No connected devices for Master folder override");
            return false;
        }

        foreach (var localFile in localFiles)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            var localPath = Path.Combine(folder.Path, localFile.FileName);
            if (!File.Exists(localPath))
                continue;

            var syncFileInfo = SyncFileInfo.FromFileMetadata(localFile);

            foreach (var deviceId in connectedDevices)
            {
                await _fileUploader.UploadFileAsync(
                    deviceId, folder.Id, localPath, syncFileInfo, cancellationToken);
            }
        }

        return true;
    }
}