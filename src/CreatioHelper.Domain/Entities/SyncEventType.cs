using System.ComponentModel;

namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Типы событий синхронизации (на основе Syncthing EventType)
/// Используются битовые флаги для эффективного фильтрования
/// </summary>
[Flags]
public enum SyncEventType : long
{
    None = 0,
    
    // Системные события
    Starting = 1 << 0,
    StartupComplete = 1 << 1,
    Shutdown = 1 << 2,
    ConfigChanged = 1 << 3,
    
    // События устройств
    DeviceDiscovered = 1 << 4,
    DeviceConnected = 1 << 5,
    DeviceDisconnected = 1 << 6,
    DevicePaused = 1 << 7,
    DeviceResumed = 1 << 8,
    DeviceRejected = 1 << 9,
    
    // События папок
    FolderAdded = 1 << 10,
    FolderRemoved = 1 << 11,
    FolderPaused = 1 << 12,
    FolderResumed = 1 << 13,
    FolderScanning = 1 << 14,
    FolderScanComplete = 1 << 15,
    FolderWatchStateChanged = 1 << 16,
    FolderSummaryChanged = 1 << 17,
    FolderCompletion = 1 << 18,
    FolderErrors = 1 << 19,
    
    // События файлов (очень частые)
    LocalChangeDetected = 1 << 20,
    RemoteChangeDetected = 1 << 21,
    LocalIndexUpdated = 1 << 22,
    RemoteIndexUpdated = 1 << 23,
    ItemStarted = 1 << 24,
    ItemFinished = 1 << 25,
    
    // События синхронизации
    SyncStarted = 1 << 26,
    SyncCompleted = 1 << 27,
    SyncConflict = 1 << 28,
    SyncError = 1 << 29,
    
    // События передачи данных
    DownloadStarted = 1 << 30,
    DownloadCompleted = 1 << 31,
    DownloadProgress = 1L << 32,
    UploadStarted = 1L << 33,
    UploadCompleted = 1L << 34,
    UploadProgress = 1L << 35,
    
    // События безопасности и аутентификации
    LoginAttempt = 1L << 36,
    LoginSuccess = 1L << 37,
    LoginFailure = 1L << 38,
    CertificateExpiring = 1L << 39,
    CertificateRenewed = 1L << 40,
    
    // События производительности
    PerformanceAlert = 1L << 41,
    MemoryWarning = 1L << 42,
    DiskSpaceWarning = 1L << 43,
    NetworkLatencyHigh = 1L << 44,
    
    // События ошибок
    Warning = 1L << 45,
    Error = 1L << 46,
    CriticalError = 1L << 47,
    
    // Составные маски (как в Syncthing)
    AllEvents = (1L << 48) - 1,
    
    // Маски для фильтрования шумных событий
    DefaultEventMask = AllEvents & ~LocalChangeDetected & ~RemoteChangeDetected & ~DownloadProgress & ~UploadProgress,
    FileSystemEventMask = LocalChangeDetected | RemoteChangeDetected | LocalIndexUpdated | RemoteIndexUpdated,
    TransferEventMask = DownloadStarted | DownloadCompleted | UploadStarted | UploadCompleted,
    ErrorEventMask = Warning | Error | CriticalError | SyncError,
    SystemEventMask = Starting | StartupComplete | Shutdown | ConfigChanged,
    DeviceEventMask = DeviceDiscovered | DeviceConnected | DeviceDisconnected | DevicePaused | DeviceResumed,
    SecurityEventMask = LoginAttempt | LoginSuccess | LoginFailure | CertificateExpiring | CertificateRenewed
}

/// <summary>
/// Расширения для работы с типами событий
/// </summary>
public static class SyncEventTypeExtensions
{
    private static readonly Dictionary<SyncEventType, string> EventNames = new()
    {
        { SyncEventType.Starting, "Starting" },
        { SyncEventType.StartupComplete, "StartupComplete" },
        { SyncEventType.Shutdown, "Shutdown" },
        { SyncEventType.ConfigChanged, "ConfigChanged" },
        { SyncEventType.DeviceDiscovered, "DeviceDiscovered" },
        { SyncEventType.DeviceConnected, "DeviceConnected" },
        { SyncEventType.DeviceDisconnected, "DeviceDisconnected" },
        { SyncEventType.DevicePaused, "DevicePaused" },
        { SyncEventType.DeviceResumed, "DeviceResumed" },
        { SyncEventType.DeviceRejected, "DeviceRejected" },
        { SyncEventType.FolderAdded, "FolderAdded" },
        { SyncEventType.FolderRemoved, "FolderRemoved" },
        { SyncEventType.FolderPaused, "FolderPaused" },
        { SyncEventType.FolderResumed, "FolderResumed" },
        { SyncEventType.FolderScanning, "FolderScanning" },
        { SyncEventType.FolderScanComplete, "FolderScanComplete" },
        { SyncEventType.FolderWatchStateChanged, "FolderWatchStateChanged" },
        { SyncEventType.FolderSummaryChanged, "FolderSummaryChanged" },
        { SyncEventType.FolderCompletion, "FolderCompletion" },
        { SyncEventType.FolderErrors, "FolderErrors" },
        { SyncEventType.LocalChangeDetected, "LocalChangeDetected" },
        { SyncEventType.RemoteChangeDetected, "RemoteChangeDetected" },
        { SyncEventType.LocalIndexUpdated, "LocalIndexUpdated" },
        { SyncEventType.RemoteIndexUpdated, "RemoteIndexUpdated" },
        { SyncEventType.ItemStarted, "ItemStarted" },
        { SyncEventType.ItemFinished, "ItemFinished" },
        { SyncEventType.SyncStarted, "SyncStarted" },
        { SyncEventType.SyncCompleted, "SyncCompleted" },
        { SyncEventType.SyncConflict, "SyncConflict" },
        { SyncEventType.SyncError, "SyncError" },
        { SyncEventType.DownloadStarted, "DownloadStarted" },
        { SyncEventType.DownloadCompleted, "DownloadCompleted" },
        { SyncEventType.DownloadProgress, "DownloadProgress" },
        { SyncEventType.UploadStarted, "UploadStarted" },
        { SyncEventType.UploadCompleted, "UploadCompleted" },
        { SyncEventType.UploadProgress, "UploadProgress" },
        { SyncEventType.LoginAttempt, "LoginAttempt" },
        { SyncEventType.LoginSuccess, "LoginSuccess" },
        { SyncEventType.LoginFailure, "LoginFailure" },
        { SyncEventType.CertificateExpiring, "CertificateExpiring" },
        { SyncEventType.CertificateRenewed, "CertificateRenewed" },
        { SyncEventType.PerformanceAlert, "PerformanceAlert" },
        { SyncEventType.MemoryWarning, "MemoryWarning" },
        { SyncEventType.DiskSpaceWarning, "DiskSpaceWarning" },
        { SyncEventType.NetworkLatencyHigh, "NetworkLatencyHigh" },
        { SyncEventType.Warning, "Warning" },
        { SyncEventType.Error, "Error" },
        { SyncEventType.CriticalError, "CriticalError" }
    };

    public static string GetEventName(this SyncEventType eventType)
    {
        return EventNames.TryGetValue(eventType, out var name) ? name : "Unknown";
    }

    public static SyncEventType ParseEventType(string eventName)
    {
        var kvp = EventNames.FirstOrDefault(x => x.Value == eventName);
        return kvp.Key;
    }

    /// <summary>
    /// Проверяет, содержится ли событие в маске
    /// </summary>
    public static bool MatchesMask(this SyncEventType eventType, SyncEventType mask)
    {
        return (mask & eventType) != 0;
    }

    /// <summary>
    /// Возвращает приоритет события для обработки
    /// </summary>
    public static EventPriority GetPriority(this SyncEventType eventType)
    {
        return eventType switch
        {
            SyncEventType.CriticalError => EventPriority.Critical,
            SyncEventType.Error or SyncEventType.SyncError => EventPriority.High,
            SyncEventType.Warning or SyncEventType.PerformanceAlert => EventPriority.Medium,
            SyncEventType.LocalChangeDetected or SyncEventType.RemoteChangeDetected => EventPriority.Low,
            SyncEventType.DownloadProgress or SyncEventType.UploadProgress => EventPriority.Lowest,
            _ => EventPriority.Normal
        };
    }
}

/// <summary>
/// Приоритеты событий для обработки
/// </summary>
public enum EventPriority
{
    Lowest = 0,
    Low = 1,
    Normal = 2,
    Medium = 3,
    High = 4,
    Critical = 5
}