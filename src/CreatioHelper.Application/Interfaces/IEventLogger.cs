using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Entities.Events;
using EventType = CreatioHelper.Domain.Entities.Events.SyncEventType;
using SyncEvent = CreatioHelper.Domain.Entities.Events.SyncEvent;
using EventPriority = CreatioHelper.Domain.Entities.Events.EventPriority;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Интерфейс для логирования и управления событиями синхронизации (на основе Syncthing events.Logger)
/// </summary>
public interface IEventLogger
{
    /// <summary>
    /// Записать событие в лог
    /// </summary>
    void LogEvent(SyncEvent syncEvent);

    /// <summary>
    /// Записать событие по типу и данным
    /// </summary>
    void LogEvent(EventType eventType, object? data = null, string? message = null, 
        string? deviceId = null, string? folderId = null, string? filePath = null);

    /// <summary>
    /// Подписаться на события по маске
    /// </summary>
    IEventSubscription Subscribe(EventType eventMask);

    /// <summary>
    /// Создать буферизованную подписку для REST API
    /// </summary>
    IBufferedEventSubscription CreateBufferedSubscription(EventType eventMask, int bufferSize = 1000);

    /// <summary>
    /// Получить последние события начиная с указанного ID
    /// </summary>
    Task<List<SyncEvent>> GetEventsSinceAsync(int sinceId, EventType eventMask = EventType.DefaultEventMask, 
        int limit = 100, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Получить статистику событий
    /// </summary>
    EventStatistics GetEventStatistics();

    /// <summary>
    /// Очистить старые события
    /// </summary>
    Task CleanupOldEventsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);
}

/// <summary>
/// Интерфейс подписки на события (аналог Syncthing Subscription)
/// </summary>
public interface IEventSubscription : IDisposable
{
    /// <summary>
    /// Канал для получения событий
    /// </summary>
    IAsyncEnumerable<SyncEvent> Events { get; }

    /// <summary>
    /// Маска событий подписки
    /// </summary>
    EventType EventMask { get; }

    /// <summary>
    /// Получить событие с таймаутом (polling)
    /// </summary>
    Task<SyncEvent?> PollAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Отменить подписку
    /// </summary>
    void Unsubscribe();
}

/// <summary>
/// Интерфейс буферизованной подписки для REST API (аналог Syncthing BufferedSubscription)
/// </summary>
public interface IBufferedEventSubscription : IDisposable
{
    /// <summary>
    /// Получить события начиная с указанного ID
    /// </summary>
    Task<List<SyncEvent>> GetEventsSinceAsync(int sinceId, int limit = 100, TimeSpan? timeout = null, 
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Маска событий подписки
    /// </summary>
    EventType EventMask { get; }

    /// <summary>
    /// Размер буфера
    /// </summary>
    int BufferSize { get; }

    /// <summary>
    /// Текущий ID события
    /// </summary>
    int CurrentEventId { get; }
}

/// <summary>
/// Статистика событий
/// </summary>
public class EventStatistics
{
    /// <summary>
    /// Общее количество событий
    /// </summary>
    public long TotalEvents { get; set; }

    /// <summary>
    /// События по типам
    /// </summary>
    public Dictionary<EventType, long> EventsByType { get; set; } = new();

    /// <summary>
    /// События по приоритетам
    /// </summary>
    public Dictionary<EventPriority, long> EventsByPriority { get; set; } = new();

    /// <summary>
    /// Количество активных подписок
    /// </summary>
    public int ActiveSubscriptions { get; set; }

    /// <summary>
    /// Количество буферизованных подписок
    /// </summary>
    public int BufferedSubscriptions { get; set; }

    /// <summary>
    /// Событий в секунду (средняя скорость)
    /// </summary>
    public double EventsPerSecond { get; set; }

    /// <summary>
    /// Время последнего события
    /// </summary>
    public DateTime? LastEventTime { get; set; }

    /// <summary>
    /// Размер буфера событий
    /// </summary>
    public int BufferSize { get; set; }

    /// <summary>
    /// Количество отброшенных событий
    /// </summary>
    public long DroppedEvents { get; set; }
}

/// <summary>
/// Расширения для упрощения работы с событиями
/// </summary>
public static class EventLoggerExtensions
{
    /// <summary>
    /// Записать системное событие
    /// </summary>
    public static void LogSystemEvent(this IEventLogger logger, EventType eventType, string message, object? data = null)
    {
        logger.LogEvent(SyncEvent.SystemEvent(eventType, message, data));
    }

    /// <summary>
    /// Записать событие устройства
    /// </summary>
    public static void LogDeviceEvent(this IEventLogger logger, EventType eventType, string deviceId, string message, object? data = null)
    {
        logger.LogEvent(SyncEvent.DeviceEvent(eventType, deviceId, message, data));
    }

    /// <summary>
    /// Записать событие папки
    /// </summary>
    public static void LogFolderEvent(this IEventLogger logger, EventType eventType, string folderId, string message, object? data = null)
    {
        logger.LogEvent(SyncEvent.FolderEvent(eventType, folderId, message, data));
    }

    /// <summary>
    /// Записать событие файла
    /// </summary>
    public static void LogFileEvent(this IEventLogger logger, EventType eventType, string folderId, string filePath, string message, object? data = null)
    {
        logger.LogEvent(SyncEvent.FileEvent(eventType, folderId, filePath, message, data));
    }

    /// <summary>
    /// Записать событие ошибки
    /// </summary>
    public static void LogError(this IEventLogger logger, Exception exception, string? context = null, string? deviceId = null, string? folderId = null)
    {
        logger.LogEvent(SyncEvent.ErrorEvent(exception, context, deviceId, folderId));
    }

    /// <summary>
    /// Записать предупреждение
    /// </summary>
    public static void LogWarning(this IEventLogger logger, string message, object? data = null, string? deviceId = null, string? folderId = null)
    {
        logger.LogEvent(EventType.Warning, data, message, deviceId, folderId);
    }

    /// <summary>
    /// Записать информационное сообщение
    /// </summary>
    public static void LogInfo(this IEventLogger logger, string message, object? data = null, string? deviceId = null, string? folderId = null)
    {
        logger.LogEvent(EventType.Starting, data, message, deviceId, folderId); // Using Starting as generic info event
    }
}