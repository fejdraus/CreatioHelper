using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Handlers;

/// <summary>
/// Базовая абстракция для обработчиков различных режимов синхронизации папок
/// </summary>
public abstract class SyncFolderHandlerBase : ISyncFolderHandler
{
    protected readonly ILogger _logger;
    protected readonly ConflictResolutionEngine _conflictEngine;
    protected readonly FileDownloader _fileDownloader;
    protected readonly FileUploader _fileUploader;
    protected readonly ISyncProtocol _protocol;
    protected readonly ISyncDatabase _database;

    protected SyncFolderHandlerBase(
        ILogger logger,
        ConflictResolutionEngine conflictEngine,
        FileDownloader fileDownloader,
        FileUploader fileUploader,
        ISyncProtocol protocol,
        ISyncDatabase database)
    {
        _logger = logger;
        _conflictEngine = conflictEngine;
        _fileDownloader = fileDownloader;
        _fileUploader = fileUploader;
        _protocol = protocol;
        _database = database;
    }

    /// <summary>
    /// Тип синхронизации, который обрабатывает данный handler
    /// </summary>
    public abstract SyncFolderType SupportedType { get; }

    /// <summary>
    /// Может ли этот режим отправлять изменения
    /// </summary>
    public abstract bool CanSendChanges { get; }

    /// <summary>
    /// Может ли этот режим получать изменения
    /// </summary>
    public abstract bool CanReceiveChanges { get; }

    /// <summary>
    /// Выполнить получение изменений (pull)
    /// </summary>
    public abstract Task<bool> PullAsync(SyncFolder folder, IEnumerable<FileMetadata> remoteFiles, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Выполнить отправку изменений (push)
    /// </summary>
    public abstract Task<bool> PushAsync(SyncFolder folder, IEnumerable<FileMetadata> localFiles, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Разрешить конфликты для данного режима синхронизации
    /// </summary>
    public virtual async Task ResolveConflictsAsync(SyncFolder folder, IEnumerable<FileConflict> conflicts, 
        CancellationToken cancellationToken = default)
    {
        foreach (var conflict in conflicts)
        {
            var resolution = _conflictEngine.ResolveConflictByFolderType(
                conflict.LocalFile.ToFileMetadata(), conflict.RemoteFile.ToFileMetadata(), folder.SyncType);
            
            await ApplyConflictResolutionAsync(folder, conflict, resolution, cancellationToken);
        }
    }

    /// <summary>
    /// Проверить, можно ли применить изменения файла в данном режиме
    /// </summary>
    public virtual bool CanApplyFileChange(SyncFolder folder, FileMetadata file, bool isIncoming)
    {
        // Общие проверки для всех режимов
        if (file.LocalFlags.HasFlag(FileLocalFlags.Invalid))
            return false;

        if (file.LocalFlags.HasFlag(FileLocalFlags.Ignored))
            return false;

        return true;
    }

    /// <summary>
    /// Получить политику разрешения конфликтов по умолчанию для данного режима
    /// </summary>
    public virtual ConflictResolutionPolicy GetDefaultConflictPolicy()
    {
        return ConflictResolutionEngine.GetDefaultPolicyForFolderType(SupportedType);
    }

    /// <summary>
    /// Применить разрешение конфликта
    /// </summary>
    protected virtual async Task ApplyConflictResolutionAsync(SyncFolder folder, FileConflict conflict, 
        ConflictResolution resolution, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Applying conflict resolution for {FileName}: {Action} - {Reason}",
            conflict.LocalFile.FileName, resolution.Action, resolution.Reason);

        switch (resolution.Action)
        {
            case ConflictAction.UseLocal:
                await UseLocalVersionAsync(folder, conflict, cancellationToken);
                break;
                
            case ConflictAction.UseRemote:
                await UseRemoteVersionAsync(folder, conflict, cancellationToken);
                break;
                
            case ConflictAction.CreateConflictCopy:
                await CreateConflictCopyAsync(folder, conflict, resolution, cancellationToken);
                break;
                
            case ConflictAction.Override:
                await OverrideRemoteAsync(folder, conflict, cancellationToken);
                break;
                
            case ConflictAction.Revert:
                await RevertToRemoteAsync(folder, conflict, cancellationToken);
                break;
                
            case ConflictAction.Skip:
                _logger.LogDebug("Skipping conflict resolution for {FileName}", conflict.LocalFile.FileName);
                break;
                
            default:
                _logger.LogWarning("Unknown conflict action: {Action}", resolution.Action);
                break;
        }
    }

    /// <summary>
    /// Использовать локальную версию файла
    /// </summary>
    protected virtual async Task UseLocalVersionAsync(SyncFolder folder, FileConflict conflict, 
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Using local version of {FileName}", conflict.LocalFile.FileName);
        // Базовая реализация - ничего не делаем, локальная версия уже есть
        await Task.CompletedTask;
    }

    /// <summary>
    /// Использовать удаленную версию файла
    /// </summary>
    protected virtual async Task UseRemoteVersionAsync(SyncFolder folder, FileConflict conflict,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Using remote version of {FileName}", conflict.LocalFile.FileName);

        var localPath = Path.Combine(folder.Path, conflict.FileName);
        var deviceId = GetActiveDeviceId(folder);

        if (string.IsNullOrEmpty(deviceId))
        {
            _logger.LogWarning("Cannot download remote version of {FileName} - no device ID available", conflict.FileName);
            return;
        }

        var downloadResult = await _fileDownloader.DownloadFileAsync(deviceId, folder.Id, conflict.RemoteFile, localPath, cancellationToken);

        if (!downloadResult.Success)
        {
            _logger.LogError("Failed to download remote version of {FileName}: {Error}", conflict.FileName, downloadResult.Error);
        }
        else
        {
            _logger.LogInformation("Successfully downloaded remote version of {FileName}", conflict.FileName);
        }
    }

    /// <summary>
    /// Создать копию конфликтного файла
    /// </summary>
    protected virtual async Task CreateConflictCopyAsync(SyncFolder folder, FileConflict conflict,
        ConflictResolution resolution, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(resolution.ConflictFileName))
            return;

        _logger.LogDebug("Creating conflict copy: {ConflictFileName}", resolution.ConflictFileName);

        var sourcePath = Path.Combine(folder.Path, conflict.LocalFile.Name);
        var conflictFilePath = Path.Combine(folder.Path, resolution.ConflictFileName);

        try
        {
            // Ensure the directory for the conflict file exists
            var conflictDir = Path.GetDirectoryName(conflictFilePath);
            if (!string.IsNullOrEmpty(conflictDir) && !Directory.Exists(conflictDir))
            {
                Directory.CreateDirectory(conflictDir);
            }

            // Create conflict copy of the loser
            if (File.Exists(sourcePath))
            {
                File.Copy(sourcePath, conflictFilePath, overwrite: false);
                _logger.LogInformation("Created conflict copy at {ConflictFilePath}", conflictFilePath);
            }

            // Download the winning version to the original path if the remote version won
            if (resolution.Winner?.FileName == conflict.RemoteFile.Name)
            {
                await UseRemoteVersionAsync(folder, conflict, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating conflict copy for {FileName}", conflict.FileName);

            // Clean up partial conflict file if it was created
            if (File.Exists(conflictFilePath))
            {
                try
                {
                    File.Delete(conflictFilePath);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to clean up partial conflict file {ConflictFilePath}", conflictFilePath);
                }
            }

            throw;
        }
    }

    /// <summary>
    /// Принудительно перезаписать удаленную версию
    /// </summary>
    protected virtual async Task OverrideRemoteAsync(SyncFolder folder, FileConflict conflict,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Overriding remote version of {FileName}", conflict.LocalFile.FileName);

        var localPath = Path.Combine(folder.Path, conflict.LocalFile.Name);

        if (!File.Exists(localPath))
        {
            _logger.LogWarning("Cannot override remote - local file does not exist: {LocalPath}", localPath);
            return;
        }

        var connectedDevices = await GetConnectedDevicesAsync(folder, cancellationToken);

        if (!connectedDevices.Any())
        {
            _logger.LogWarning("Cannot override remote - no connected devices for folder {FolderId}", folder.Id);
            return;
        }

        foreach (var deviceId in connectedDevices)
        {
            try
            {
                var uploadResult = await _fileUploader.UploadFileAsync(deviceId, folder.Id, localPath, conflict.LocalFile, cancellationToken);

                if (!uploadResult.Success)
                {
                    _logger.LogError("Failed to override remote version on device {DeviceId}: {Error}", deviceId, uploadResult.Error);
                }
                else
                {
                    _logger.LogInformation("Successfully overrode remote version on device {DeviceId} for {FileName}", deviceId, conflict.FileName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error overriding remote version on device {DeviceId} for {FileName}", deviceId, conflict.FileName);
            }
        }
    }

    /// <summary>
    /// Откатить к удаленной версии
    /// </summary>
    protected virtual async Task RevertToRemoteAsync(SyncFolder folder, FileConflict conflict,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Reverting to remote version of {FileName}", conflict.LocalFile.FileName);

        // Clear the ReceiveOnly flag since we're reverting to remote
        conflict.LocalFile.LocalFlags &= ~FileLocalFlags.ReceiveOnly;

        // Download the remote version to replace local
        await UseRemoteVersionAsync(folder, conflict, cancellationToken);

        _logger.LogInformation("Reverted {FileName} to remote version", conflict.FileName);
    }

    /// <summary>
    /// Get the first active (connected) device ID for the folder
    /// </summary>
    protected virtual string? GetActiveDeviceId(SyncFolder folder)
    {
        // Return the first device from folder's device list
        // In a full implementation, we would check connection status
        return folder.Devices.FirstOrDefault();
    }

    /// <summary>
    /// Get all connected devices for the folder
    /// </summary>
    protected virtual async Task<IEnumerable<string>> GetConnectedDevicesAsync(SyncFolder folder, CancellationToken cancellationToken)
    {
        var connectedDevices = new List<string>();

        foreach (var deviceId in folder.Devices)
        {
            try
            {
                if (await _protocol.IsConnectedAsync(deviceId))
                {
                    connectedDevices.Add(deviceId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error checking connection status for device {DeviceId}", deviceId);
            }
        }

        return connectedDevices;
    }

    /// <summary>
    /// Проверить, поддерживается ли указанный тип папки этим обработчиком
    /// </summary>
    public bool SupportsFolder(SyncFolder folder)
    {
        return folder.SyncType == SupportedType;
    }

    /// <summary>
    /// Сравнить хеши файлов
    /// </summary>
    protected static bool HashesEqual(byte[]? hash1, byte[]? hash2)
    {
        if (hash1 == null && hash2 == null) return true;
        if (hash1 == null || hash2 == null) return false;
        return hash1.SequenceEqual(hash2);
    }

    /// <summary>
    /// Получить локальный файл по имени из БД
    /// </summary>
    protected async Task<FileMetadata?> GetLocalFileAsync(string folderId, string fileName)
    {
        return await _database.FileMetadata.GetAsync(folderId, fileName);
    }

    /// <summary>
    /// Получить все локальные файлы папки
    /// </summary>
    protected async Task<IEnumerable<FileMetadata>> GetLocalFilesAsync(string folderId)
    {
        return await _database.FileMetadata.GetAllAsync(folderId);
    }
}