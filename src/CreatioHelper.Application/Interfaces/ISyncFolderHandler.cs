using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Интерфейс для обработчиков различных режимов синхронизации папок
/// </summary>
public interface ISyncFolderHandler
{
    /// <summary>
    /// Тип синхронизации, который обрабатывает данный handler
    /// </summary>
    SyncFolderType SupportedType { get; }
    
    /// <summary>
    /// Может ли этот режим отправлять изменения
    /// </summary>
    bool CanSendChanges { get; }
    
    /// <summary>
    /// Может ли этот режим получать изменения
    /// </summary>
    bool CanReceiveChanges { get; }
    
    /// <summary>
    /// Выполнить получение изменений (pull)
    /// </summary>
    /// <param name="folder">Папка для синхронизации</param>
    /// <param name="remoteFiles">Удаленные файлы</param>
    /// <param name="cancellationToken">Токен отмены</param>
    /// <returns>True если были применены изменения</returns>
    Task<bool> PullAsync(SyncFolder folder, IEnumerable<FileMetadata> remoteFiles, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Выполнить отправку изменений (push)
    /// </summary>
    /// <param name="folder">Папка для синхронизации</param>
    /// <param name="localFiles">Локальные файлы</param>
    /// <param name="cancellationToken">Токен отмены</param>
    /// <returns>True если были отправлены изменения</returns>
    Task<bool> PushAsync(SyncFolder folder, IEnumerable<FileMetadata> localFiles, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Разрешить конфликты для данного режима синхронизации
    /// </summary>
    /// <param name="folder">Папка синхронизации</param>
    /// <param name="conflicts">Список конфликтов</param>
    /// <param name="cancellationToken">Токен отмены</param>
    Task ResolveConflictsAsync(SyncFolder folder, IEnumerable<FileConflict> conflicts, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Проверить, можно ли применить изменения файла в данном режиме
    /// </summary>
    /// <param name="folder">Папка синхронизации</param>
    /// <param name="file">Метаданные файла</param>
    /// <param name="isIncoming">True для входящих изменений, false для исходящих</param>
    bool CanApplyFileChange(SyncFolder folder, FileMetadata file, bool isIncoming);
    
    /// <summary>
    /// Получить политику разрешения конфликтов по умолчанию для данного режима
    /// </summary>
    ConflictResolutionPolicy GetDefaultConflictPolicy();
}

