namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Типы режимов синхронизации папок, аналогично Syncthing FolderType
/// </summary>
public enum SyncFolderType
{
    /// <summary>
    /// Полная двусторонняя синхронизация (аналог sendreceive)
    /// Может отправлять и получать изменения
    /// </summary>
    SendReceive = 0,
    
    /// <summary>
    /// Только отправка изменений (аналог sendonly)
    /// Локальные изменения отправляются, удаленные игнорируются
    /// </summary>
    SendOnly = 1,
    
    /// <summary>
    /// Только получение изменений (аналог receiveonly)
    /// Удаленные изменения принимаются, локальные помечаются флагом
    /// </summary>
    ReceiveOnly = 2,
    
    /// <summary>
    /// Получение зашифрованных данных (аналог receiveencrypted)
    /// Принимает зашифрованные данные без расшифровки
    /// </summary>
    ReceiveEncrypted = 3,
    
    /// <summary>
    /// Режим мастера (расширенный SendOnly)
    /// Может принудительно перезаписывать удаленное состояние
    /// </summary>
    Master = 4,
    
    /// <summary>
    /// Режим ведомого (расширенный ReceiveOnly)
    /// Автоматически откатывает локальные изменения
    /// </summary>
    Slave = 5
}

/// <summary>
/// Флаги локального состояния файлов, аналогично Syncthing LocalFlags
/// </summary>
[Flags]
public enum FileLocalFlags
{
    /// <summary>
    /// Нет флагов - файл в нормальном состоянии
    /// </summary>
    None = 0,
    
    /// <summary>
    /// Неподдерживаемый тип файла
    /// </summary>
    Unsupported = 1 << 0,    // 1
    
    /// <summary>
    /// Файл игнорируется правилами .stignore
    /// </summary>
    Ignored = 1 << 1,        // 2
    
    /// <summary>
    /// Файл требует повторного сканирования
    /// </summary>
    MustRescan = 1 << 2,     // 4
    
    /// <summary>
    /// Файл изменен в receive-only папке
    /// </summary>
    ReceiveOnly = 1 << 3,    // 8
    
    /// <summary>
    /// Файл в состоянии конфликта
    /// </summary>
    Conflicted = 1 << 4,     // 16
    
    /// <summary>
    /// Файл поврежден или недоступен
    /// </summary>
    Invalid = 1 << 5,        // 32
    
    /// <summary>
    /// Удаленный файл недоступен или поврежден
    /// </summary>
    RemoteInvalid = 1 << 6,  // 64
    
    /// <summary>
    /// Флаги, которые делают файл невалидным для синхронизации
    /// </summary>
    LocalInvalidFlags = Unsupported | Ignored | MustRescan | ReceiveOnly | Invalid,
    
    /// <summary>
    /// Флаги, которые могут создавать конфликты
    /// </summary>
    LocalConflictFlags = Unsupported | Ignored | ReceiveOnly | Conflicted
}

/// <summary>
/// Политики разрешения конфликтов для различных режимов синхронизации
/// </summary>
public enum ConflictResolutionPolicy
{
    /// <summary>
    /// Создавать копии конфликтных файлов (.sync-conflict-...)
    /// </summary>
    CreateCopies,
    
    /// <summary>
    /// Всегда использовать самую новую версию по времени модификации
    /// </summary>
    UseNewest,
    
    /// <summary>
    /// Всегда использовать локальную версию
    /// </summary>
    UseLocal,
    
    /// <summary>
    /// Всегда использовать удаленную версию
    /// </summary>
    UseRemote,
    
    /// <summary>
    /// Принудительная перезапись удаленного состояния (для Master режима)
    /// </summary>
    Override,
    
    /// <summary>
    /// Откат к глобальному состоянию (для Slave режима)
    /// </summary>
    Revert
}

