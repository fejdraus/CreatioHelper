using System.Collections.Concurrent;
using System.Threading.Channels;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Entities.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using EventType = CreatioHelper.Domain.Entities.Events.SyncEventType;
using SyncEvent = CreatioHelper.Domain.Entities.Events.SyncEvent;
using EventPriority = CreatioHelper.Domain.Entities.Events.EventPriority;

namespace CreatioHelper.Infrastructure.Services.Events;

/// <summary>
/// Реализация системы событий на основе Syncthing events.Logger
/// </summary>
public class EventLogger : BackgroundService, IEventLogger
{
    private readonly ILogger<EventLogger> _logger;
    private readonly Channel<SyncEvent> _eventChannel;
    private readonly ConcurrentDictionary<int, EventSubscription> _subscriptions;
    private readonly ConcurrentDictionary<int, BufferedEventSubscription> _bufferedSubscriptions;
    private readonly EventStatistics _statistics;
    private readonly object _statsLock = new();
    
    private int _nextGlobalId = 1;
    private int _nextSubscriptionId = 1;
    private const int DefaultChannelCapacity = 10000;
    private const int EventLogTimeoutMs = 15;

    public EventLogger(ILogger<EventLogger> logger)
    {
        _logger = logger;
        
        // Создаем канал для событий с буферизацией (аналог events channel в Syncthing)
        var options = new BoundedChannelOptions(DefaultChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };
        _eventChannel = Channel.CreateBounded<SyncEvent>(options);
        
        _subscriptions = new ConcurrentDictionary<int, EventSubscription>();
        _bufferedSubscriptions = new ConcurrentDictionary<int, BufferedEventSubscription>();
        _statistics = new EventStatistics
        {
            BufferSize = DefaultChannelCapacity
        };
    }

    /// <summary>
    /// Основной цикл обработки событий (аналог Serve в Syncthing)
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EventLogger started, processing events...");
        
        await foreach (var syncEvent in _eventChannel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessEventAsync(syncEvent);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing event {Type}: {Message}", syncEvent.Type, ex.Message);
            }
        }
        
        _logger.LogInformation("EventLogger stopped");
    }

    /// <summary>
    /// Записать событие в лог
    /// </summary>
    public void LogEvent(SyncEvent syncEvent)
    {
        // Устанавливаем глобальный ID и временную метку
        lock (_statsLock)
        {
            syncEvent.GlobalId = _nextGlobalId++;
            syncEvent.Time = DateTime.UtcNow;
        }

        // Пытаемся записать событие в канал
        if (!_eventChannel.Writer.TryWrite(syncEvent))
        {
            // Если канал заполнен, увеличиваем счетчик отброшенных событий
            lock (_statsLock)
            {
                _statistics.DroppedEvents++;
            }
            
            _logger.LogWarning("Event channel is full, dropping event {Type}", syncEvent.Type);
        }
    }

    /// <summary>
    /// Записать событие по типу и данным
    /// </summary>
    public void LogEvent(EventType eventType, object? data = null, string? message = null, 
        string? deviceId = null, string? folderId = null, string? filePath = null)
    {
        var syncEvent = new SyncEvent
        {
            Type = eventType,
            Data = data,
            Message = message,
            DeviceId = deviceId,
            FolderId = folderId,
            FilePath = filePath,
            Priority = eventType.GetPriority()
        };

        LogEvent(syncEvent);
    }

    /// <summary>
    /// Подписаться на события по маске
    /// </summary>
    public IEventSubscription Subscribe(EventType eventMask)
    {
        var subscriptionId = Interlocked.Increment(ref _nextSubscriptionId);
        var subscription = new EventSubscription(subscriptionId, eventMask, this);
        
        _subscriptions.TryAdd(subscriptionId, subscription);
        
        lock (_statsLock)
        {
            _statistics.ActiveSubscriptions = _subscriptions.Count;
        }
        
        _logger.LogDebug("Created subscription {SubscriptionId} with mask {EventMask}", subscriptionId, eventMask);
        
        return subscription;
    }

    /// <summary>
    /// Создать буферизованную подписку для REST API
    /// </summary>
    public IBufferedEventSubscription CreateBufferedSubscription(EventType eventMask, int bufferSize = 1000)
    {
        var subscriptionId = Interlocked.Increment(ref _nextSubscriptionId);
        var bufferedSubscription = new BufferedEventSubscription(subscriptionId, eventMask, bufferSize, this);
        
        _bufferedSubscriptions.TryAdd(subscriptionId, bufferedSubscription);
        
        lock (_statsLock)
        {
            _statistics.BufferedSubscriptions = _bufferedSubscriptions.Count;
        }
        
        _logger.LogDebug("Created buffered subscription {SubscriptionId} with mask {EventMask} and buffer size {BufferSize}", 
            subscriptionId, eventMask, bufferSize);
        
        return bufferedSubscription;
    }

    /// <summary>
    /// Получить последние события начиная с указанного ID
    /// </summary>
    public async Task<List<SyncEvent>> GetEventsSinceAsync(int sinceId, EventType eventMask = EventType.DefaultEventMask, 
        int limit = 100, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        using var bufferedSubscription = CreateBufferedSubscription(eventMask);
        return await bufferedSubscription.GetEventsSinceAsync(sinceId, limit, timeout, cancellationToken);
    }

    /// <summary>
    /// Получить статистику событий
    /// </summary>
    public EventStatistics GetEventStatistics()
    {
        lock (_statsLock)
        {
            return new EventStatistics
            {
                TotalEvents = _statistics.TotalEvents,
                EventsByType = new Dictionary<EventType, long>(_statistics.EventsByType),
                EventsByPriority = new Dictionary<EventPriority, long>(_statistics.EventsByPriority),
                ActiveSubscriptions = _statistics.ActiveSubscriptions,
                BufferedSubscriptions = _statistics.BufferedSubscriptions,
                EventsPerSecond = _statistics.EventsPerSecond,
                LastEventTime = _statistics.LastEventTime,
                BufferSize = _statistics.BufferSize,
                DroppedEvents = _statistics.DroppedEvents
            };
        }
    }

    /// <summary>
    /// Очистить старые события (пока не реализовано - события хранятся только в памяти)
    /// </summary>
    public async Task CleanupOldEventsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        // Реализация будет добавлена когда добавим персистентное хранение событий
        await Task.CompletedTask;
    }

    /// <summary>
    /// Обработать событие и отправить подписчикам (аналог sendEvent в Syncthing)
    /// </summary>
    private async Task ProcessEventAsync(SyncEvent syncEvent)
    {
        // Обновляем статистику
        lock (_statsLock)
        {
            _statistics.TotalEvents++;
            _statistics.EventsByType.TryGetValue(syncEvent.Type, out var typeCount);
            _statistics.EventsByType[syncEvent.Type] = typeCount + 1;
            
            _statistics.EventsByPriority.TryGetValue(syncEvent.Priority, out var priorityCount);
            _statistics.EventsByPriority[syncEvent.Priority] = priorityCount + 1;
            
            _statistics.LastEventTime = syncEvent.Time;
            
            // Простой расчет events per second (скользящее среднее можно добавить позже)
            var now = DateTime.UtcNow;
            if (_statistics.LastEventTime.HasValue)
            {
                var elapsed = (now - _statistics.LastEventTime.Value).TotalSeconds;
                if (elapsed > 0)
                {
                    _statistics.EventsPerSecond = (_statistics.EventsPerSecond * 0.9) + (1.0 / elapsed * 0.1);
                }
            }
        }

        _logger.LogTrace("Processing event {GlobalId}: {Type} - {Message}", 
            syncEvent.GlobalId, syncEvent.Type, syncEvent.Message);

        // Отправляем событие всем подходящим подпискам
        var tasks = new List<Task>();

        foreach (var subscription in _subscriptions.Values)
        {
            if (syncEvent.Type.MatchesMask(subscription.EventMask))
            {
                tasks.Add(subscription.SendEventAsync(syncEvent));
            }
        }

        foreach (var bufferedSubscription in _bufferedSubscriptions.Values)
        {
            if (syncEvent.Type.MatchesMask(bufferedSubscription.EventMask))
            {
                tasks.Add(bufferedSubscription.AddEventAsync(syncEvent));
            }
        }

        // Ожидаем отправку всем подписчикам с таймаутом
        if (tasks.Count > 0)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(EventLogTimeoutMs));
                await Task.WhenAll(tasks).WaitAsync(cts.Token);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Timeout sending event {Type} to subscriptions", syncEvent.Type);
            }
        }
    }

    /// <summary>
    /// Удалить подписку
    /// </summary>
    internal void RemoveSubscription(int subscriptionId)
    {
        _subscriptions.TryRemove(subscriptionId, out _);
        
        lock (_statsLock)
        {
            _statistics.ActiveSubscriptions = _subscriptions.Count;
        }
    }

    /// <summary>
    /// Удалить буферизованную подписку
    /// </summary>
    internal void RemoveBufferedSubscription(int subscriptionId)
    {
        _bufferedSubscriptions.TryRemove(subscriptionId, out _);
        
        lock (_statsLock)
        {
            _statistics.BufferedSubscriptions = _bufferedSubscriptions.Count;
        }
    }

    public override void Dispose()
    {
        try
        {
            // Закрываем канал событий только если он еще не закрыт
            if (!_eventChannel.Writer.TryComplete())
            {
                // Канал уже закрыт
            }
        }
        catch (InvalidOperationException)
        {
            // Канал уже закрыт
        }
        
        // Закрываем все подписки
        foreach (var subscription in _subscriptions.Values)
        {
            try
            {
                subscription.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing subscription");
            }
        }
        
        foreach (var bufferedSubscription in _bufferedSubscriptions.Values)
        {
            try
            {
                bufferedSubscription.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing buffered subscription");
            }
        }
        
        base.Dispose();
    }
}