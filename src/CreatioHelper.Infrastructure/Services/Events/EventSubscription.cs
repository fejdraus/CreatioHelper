using System.Threading.Channels;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Entities.Events;
using Microsoft.Extensions.Logging;
using EventType = CreatioHelper.Domain.Entities.Events.SyncEventType;
using SyncEvent = CreatioHelper.Domain.Entities.Events.SyncEvent;

namespace CreatioHelper.Infrastructure.Services.Events;

/// <summary>
/// Реализация подписки на события (на основе Syncthing subscription)
/// </summary>
public class EventSubscription : IEventSubscription
{
    private readonly int _subscriptionId;
    private readonly EventLogger _eventLogger;
    private readonly Channel<SyncEvent> _eventChannel;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly ILogger _logger;
    private int _nextSubscriptionEventId = 1;
    private volatile bool _disposed = false;

    public EventType EventMask { get; }
    
    public IAsyncEnumerable<SyncEvent> Events => _eventChannel.Reader.ReadAllAsync(_cancellationTokenSource.Token);

    public EventSubscription(int subscriptionId, EventType eventMask, EventLogger eventLogger, ILogger? logger = null)
    {
        _subscriptionId = subscriptionId;
        EventMask = eventMask;
        _eventLogger = eventLogger;
        _cancellationTokenSource = new CancellationTokenSource();
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        // Создаем канал для событий подписки (аналог subscription.events в Syncthing)
        var options = new BoundedChannelOptions(1000) // BufferSize как в Syncthing
        {
            FullMode = BoundedChannelFullMode.DropOldest, // Отбрасываем старые события если буфер полон
            SingleReader = true,
            SingleWriter = false
        };
        _eventChannel = Channel.CreateBounded<SyncEvent>(options);
    }

    /// <summary>
    /// Получить событие с таймаутом (аналог Poll в Syncthing)
    /// </summary>
    public async Task<SyncEvent?> PollAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(EventSubscription));

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _cancellationTokenSource.Token);
            cts.CancelAfter(timeout);

            return await _eventChannel.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Timeout
            return null;
        }
        catch (InvalidOperationException)
        {
            // Channel closed
            return null;
        }
    }

    /// <summary>
    /// Отправить событие в подписку (вызывается из EventLogger)
    /// </summary>
    internal async Task SendEventAsync(SyncEvent syncEvent)
    {
        if (_disposed) return;

        try
        {
            // Устанавливаем ID события для этой подписки
            var eventCopy = new SyncEvent
            {
                SubscriptionId = _nextSubscriptionEventId++,
                GlobalId = syncEvent.GlobalId,
                Time = syncEvent.Time,
                Type = syncEvent.Type,
                Data = syncEvent.Data,
                DeviceId = syncEvent.DeviceId,
                FolderId = syncEvent.FolderId,
                FilePath = syncEvent.FilePath,
                Priority = syncEvent.Priority,
                Message = syncEvent.Message,
                Metadata = syncEvent.Metadata != null ? new Dictionary<string, object?>(syncEvent.Metadata) : new Dictionary<string, object?>()
            };

            // Пытаемся отправить событие в канал с коротким таймаутом
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(15)); // eventLogTimeout
            if (!await _eventChannel.Writer.WaitToWriteAsync(cts.Token))
            {
                return; // Channel closed
            }

            if (!_eventChannel.Writer.TryWrite(eventCopy))
            {
                _logger.LogWarning("Failed to send event {Type} to subscription {SubscriptionId}", 
                    syncEvent.Type, _subscriptionId);
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout - событие отбрасывается как в Syncthing
            _logger.LogTrace("Timeout sending event {Type} to subscription {SubscriptionId}", 
                syncEvent.Type, _subscriptionId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending event {Type} to subscription {SubscriptionId}: {Message}", 
                syncEvent.Type, _subscriptionId, ex.Message);
        }
    }

    /// <summary>
    /// Отменить подписку
    /// </summary>
    public void Unsubscribe()
    {
        if (_disposed) return;

        _logger.LogDebug("Unsubscribing subscription {SubscriptionId} with mask {EventMask}", _subscriptionId, EventMask);
        
        // Уведомляем EventLogger о необходимости удалить подписку
        _eventLogger.RemoveSubscription(_subscriptionId);
        
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Закрываем канал событий
        _eventChannel.Writer.Complete();
        
        // Отменяем все операции
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();

        _logger.LogTrace("Disposed subscription {SubscriptionId}", _subscriptionId);
    }
}

/// <summary>
/// Реализация буферизованной подписки (на основе Syncthing bufferedSubscription)
/// </summary>
public class BufferedEventSubscription : IBufferedEventSubscription
{
    private readonly int _subscriptionId;
    private readonly EventLogger _eventLogger;
    private readonly SyncEvent[] _buffer;
    private readonly object _bufferLock = new();
    private readonly ILogger _logger;
    
    private int _nextIndex = 0;
    private int _currentEventId = 0;
    private volatile bool _disposed = false;

    public EventType EventMask { get; }
    public int BufferSize { get; }
    public int CurrentEventId
    {
        get
        {
            lock (_bufferLock)
            {
                return _currentEventId;
            }
        }
    }

    public BufferedEventSubscription(int subscriptionId, EventType eventMask, int bufferSize, EventLogger eventLogger, ILogger? logger = null)
    {
        _subscriptionId = subscriptionId;
        EventMask = eventMask;
        BufferSize = bufferSize;
        _eventLogger = eventLogger;
        _buffer = new SyncEvent[bufferSize];
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>
    /// Добавить событие в буфер (вызывается из EventLogger)
    /// </summary>
    internal async Task AddEventAsync(SyncEvent syncEvent)
    {
        if (_disposed) return;

        await Task.Run(() =>
        {
            lock (_bufferLock)
            {
                // Копируем событие и устанавливаем ID для этой подписки
                var eventCopy = new SyncEvent
                {
                    SubscriptionId = ++_currentEventId, // Увеличиваем счетчик для этой подписки
                    GlobalId = syncEvent.GlobalId,
                    Time = syncEvent.Time,
                    Type = syncEvent.Type,
                    Data = syncEvent.Data,
                    DeviceId = syncEvent.DeviceId,
                    FolderId = syncEvent.FolderId,
                    FilePath = syncEvent.FilePath,
                    Priority = syncEvent.Priority,
                    Message = syncEvent.Message,
                    Metadata = syncEvent.Metadata != null ? new Dictionary<string, object?>(syncEvent.Metadata) : new Dictionary<string, object?>()
                };

                // Добавляем в кольцевой буфер
                _buffer[_nextIndex] = eventCopy;
                _nextIndex = (_nextIndex + 1) % BufferSize;

                _logger.LogTrace("Added event {Type} to buffered subscription {SubscriptionId}, current ID: {CurrentEventId}", 
                    syncEvent.Type, _subscriptionId, _currentEventId);
            }
        });
    }

    /// <summary>
    /// Получить события начиная с указанного ID (аналог Since в Syncthing)
    /// </summary>
    public async Task<List<SyncEvent>> GetEventsSinceAsync(int sinceId, int limit = 100, TimeSpan? timeout = null, 
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(BufferedEventSubscription));

        return await Task.Run(() =>
        {
            var result = new List<SyncEvent>();
            
            lock (_bufferLock)
            {
                // Если запрашиваемый ID больше или равен текущему, ждем новых событий
                if (sinceId >= _currentEventId)
                {
                    // В реальной реализации здесь был бы wait на condition variable
                    // Для упрощения просто возвращаем пустой список
                    return result;
                }

                // Сначала проверяем вторую половину буфера (от nextIndex до конца)
                for (int i = _nextIndex; i < BufferSize && result.Count < limit; i++)
                {
                    if (_buffer[i] != null && _buffer[i].SubscriptionId > sinceId)
                    {
                        result.Add(_buffer[i]);
                    }
                }

                // Затем проверяем первую половину (от 0 до nextIndex)
                for (int i = 0; i < _nextIndex && result.Count < limit; i++)
                {
                    if (_buffer[i] != null && _buffer[i].SubscriptionId > sinceId)
                    {
                        result.Add(_buffer[i]);
                    }
                }

                // Сортируем по SubscriptionId
                result.Sort((a, b) => a.SubscriptionId.CompareTo(b.SubscriptionId));

                _logger.LogTrace("Retrieved {Count} events since {SinceId} from buffered subscription {SubscriptionId}", 
                    result.Count, sinceId, _subscriptionId);
            }

            return result;
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Уведомляем EventLogger о необходимости удалить буферизованную подписку
        _eventLogger.RemoveBufferedSubscription(_subscriptionId);

        _logger.LogTrace("Disposed buffered subscription {SubscriptionId}", _subscriptionId);
    }
}