using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services.Metrics;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Events;

/// <summary>
/// Event queue for reliable event delivery.
/// Supports persistence, replay, and backpressure.
/// Compatible with Syncthing's event subscription model.
/// </summary>
public class SyncEventQueue : IDisposable
{
    private readonly ILogger<SyncEventQueue> _logger;
    private readonly Channel<SyncEvent> _eventChannel;
    private readonly ConcurrentDictionary<string, SyncEventSubscription> _subscriptions;
    private readonly ConcurrentQueue<SyncEvent> _recentEvents;
    private readonly int _maxRecentEvents;
    private readonly CancellationTokenSource _cancellationTokenSource;
    private readonly Task _processingTask;
    private readonly IEventLogRepository? _eventLogRepository;
    private readonly Channel<SyncEvent>? _persistenceChannel;
    private readonly Task? _persistenceTask;

    private long _nextEventId = 1;

    /// <summary>
    /// Creates a new event queue with specified capacity.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="capacity">Maximum queue capacity (default: 10000).</param>
    /// <param name="maxRecentEvents">Maximum recent events to keep for replay (default: 1000).</param>
    /// <param name="eventLogRepository">Optional repository for event persistence.</param>
    public SyncEventQueue(
        ILogger<SyncEventQueue> logger,
        int capacity = 10000,
        int maxRecentEvents = 1000,
        IEventLogRepository? eventLogRepository = null)
    {
        _logger = logger;
        _maxRecentEvents = maxRecentEvents;
        _eventLogRepository = eventLogRepository;
        _subscriptions = new ConcurrentDictionary<string, SyncEventSubscription>();
        _recentEvents = new ConcurrentQueue<SyncEvent>();
        _cancellationTokenSource = new CancellationTokenSource();

        // Bounded channel with backpressure
        _eventChannel = Channel.CreateBounded<SyncEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        _processingTask = ProcessEventsAsync(_cancellationTokenSource.Token);

        // Set up persistence channel if repository is provided
        if (_eventLogRepository != null)
        {
            _persistenceChannel = Channel.CreateBounded<SyncEvent>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
            _persistenceTask = PersistEventsAsync(_cancellationTokenSource.Token);
        }
    }

    /// <summary>
    /// Publishes an event to all subscribers.
    /// </summary>
    public async ValueTask PublishAsync(SyncEvent syncEvent)
    {
        syncEvent.Id = Interlocked.Increment(ref _nextEventId);
        syncEvent.Time = DateTime.UtcNow;

        // Record event creation metric
        SyncMetrics.EventCreated(MapToDomainsEventType(syncEvent.Type));

        // Add to recent events buffer for replay
        _recentEvents.Enqueue(syncEvent);
        while (_recentEvents.Count > _maxRecentEvents)
        {
            _recentEvents.TryDequeue(out _);
        }

        // Update queue size metric
        SyncMetrics.SetQueueSize(_recentEvents.Count);

        // Write to channel for subscriber distribution
        if (!_eventChannel.Writer.TryWrite(syncEvent))
        {
            _logger.LogWarning("Event queue full, dropping oldest events. EventId={EventId}", syncEvent.Id);
            SyncMetrics.EventDropped(MapToDomainsEventType(syncEvent.Type));
            await _eventChannel.Writer.WriteAsync(syncEvent);
        }

        // Write to persistence channel if enabled
        if (_persistenceChannel != null)
        {
            _persistenceChannel.Writer.TryWrite(syncEvent);
        }

        // Record delivery metric
        SyncMetrics.EventDelivered(MapToDomainsEventType(syncEvent.Type));

        _logger.LogDebug("Event published: Type={Type}, Id={Id}", syncEvent.Type, syncEvent.Id);
    }

    /// <summary>
    /// Maps Infrastructure SyncEventType to Domain SyncEventType for metrics.
    /// </summary>
    private static Domain.Entities.Events.SyncEventType MapToDomainsEventType(SyncEventType infraType)
    {
        return infraType switch
        {
            SyncEventType.DeviceConnected => Domain.Entities.Events.SyncEventType.DeviceConnected,
            SyncEventType.DeviceDisconnected => Domain.Entities.Events.SyncEventType.DeviceDisconnected,
            SyncEventType.DeviceDiscovered => Domain.Entities.Events.SyncEventType.DeviceDiscovered,
            SyncEventType.DevicePaused => Domain.Entities.Events.SyncEventType.DevicePaused,
            SyncEventType.DeviceResumed => Domain.Entities.Events.SyncEventType.DeviceResumed,
            SyncEventType.FolderScanProgress => Domain.Entities.Events.SyncEventType.FolderScanProgress,
            SyncEventType.FolderScanComplete => Domain.Entities.Events.SyncEventType.FolderScanComplete,
            SyncEventType.FolderStateChanged => Domain.Entities.Events.SyncEventType.StateChanged,
            SyncEventType.FolderCompletion => Domain.Entities.Events.SyncEventType.FolderCompletion,
            SyncEventType.FolderError => Domain.Entities.Events.SyncEventType.FolderErrors,
            SyncEventType.FolderPaused => Domain.Entities.Events.SyncEventType.FolderPaused,
            SyncEventType.FolderResumed => Domain.Entities.Events.SyncEventType.FolderResumed,
            SyncEventType.LocalChangeDetected => Domain.Entities.Events.SyncEventType.LocalChangeDetected,
            SyncEventType.RemoteChangeReceived => Domain.Entities.Events.SyncEventType.RemoteChangeDetected,
            SyncEventType.ItemStarted => Domain.Entities.Events.SyncEventType.ItemStarted,
            SyncEventType.ItemFinished => Domain.Entities.Events.SyncEventType.ItemFinished,
            SyncEventType.DownloadProgress => Domain.Entities.Events.SyncEventType.DownloadProgress,
            SyncEventType.ConfigSaved => Domain.Entities.Events.SyncEventType.ConfigSaved,
            SyncEventType.ConnectionError => Domain.Entities.Events.SyncEventType.Failure,
            SyncEventType.SyncError => Domain.Entities.Events.SyncEventType.SyncError,
            SyncEventType.ConflictDetected => Domain.Entities.Events.SyncEventType.Warning,
            SyncEventType.StateChanged => Domain.Entities.Events.SyncEventType.StateChanged,
            _ => Domain.Entities.Events.SyncEventType.Information
        };
    }

    /// <summary>
    /// Subscribes to events with an optional filter.
    /// </summary>
    /// <param name="subscriberId">Unique subscriber identifier.</param>
    /// <param name="filter">Optional event type filter.</param>
    /// <param name="sinceEventId">Optional event ID to replay from.</param>
    /// <returns>Subscription for receiving events.</returns>
    public SyncEventSubscription Subscribe(
        string subscriberId,
        SyncEventType[]? filter = null,
        long? sinceEventId = null)
    {
        var subscription = new SyncEventSubscription(subscriberId, filter);

        // Remove existing subscription if any
        if (_subscriptions.TryRemove(subscriberId, out var existing))
        {
            existing.Dispose();
        }

        _subscriptions[subscriberId] = subscription;

        // Replay recent events if requested
        if (sinceEventId.HasValue)
        {
            foreach (var evt in _recentEvents)
            {
                if (evt.Id > sinceEventId.Value && subscription.MatchesFilter(evt))
                {
                    subscription.TryEnqueue(evt);
                }
            }
        }

        _logger.LogInformation("Subscription created: SubscriberId={SubscriberId}, Filter={Filter}",
            subscriberId, filter != null ? string.Join(",", filter) : "all");

        return subscription;
    }

    /// <summary>
    /// Unsubscribes a subscriber.
    /// </summary>
    public void Unsubscribe(string subscriberId)
    {
        if (_subscriptions.TryRemove(subscriberId, out var subscription))
        {
            subscription.Dispose();
            _logger.LogInformation("Subscription removed: SubscriberId={SubscriberId}", subscriberId);
        }
    }

    /// <summary>
    /// Gets events since a specific event ID.
    /// </summary>
    public IEnumerable<SyncEvent> GetEventsSince(long sinceEventId, int limit = 100, SyncEventType[]? filter = null)
    {
        // Materialize the collection to avoid lazy enumeration issues with concurrent access
        var events = _recentEvents
            .Where(e => e.Id > sinceEventId)
            .Where(e => filter == null || filter.Contains(e.Type))
            .Take(limit)
            .ToList();

        return events;
    }

    /// <summary>
    /// Gets the last event ID.
    /// </summary>
    public long GetLastEventId()
    {
        return Interlocked.Read(ref _nextEventId) - 1;
    }

    private async Task ProcessEventsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var syncEvent in _eventChannel.Reader.ReadAllAsync(cancellationToken))
            {
                // Distribute to all matching subscribers
                foreach (var subscription in _subscriptions.Values)
                {
                    if (subscription.MatchesFilter(syncEvent))
                    {
                        if (!subscription.TryEnqueue(syncEvent))
                        {
                            _logger.LogWarning(
                                "Subscription queue full, dropping event. SubscriberId={SubscriberId}, EventId={EventId}",
                                subscription.SubscriberId, syncEvent.Id);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing events");
        }
    }

    private async Task PersistEventsAsync(CancellationToken cancellationToken)
    {
        if (_eventLogRepository == null || _persistenceChannel == null)
            return;

        var batch = new List<SyncEvent>();
        const int batchSize = 100;
        var batchTimeout = TimeSpan.FromSeconds(1);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                batch.Clear();

                // Collect events for batching
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(batchTimeout);

                try
                {
                    while (batch.Count < batchSize)
                    {
                        if (_persistenceChannel.Reader.TryRead(out var evt))
                        {
                            batch.Add(evt);
                        }
                        else
                        {
                            // Wait for more events or timeout
                            await _persistenceChannel.Reader.WaitToReadAsync(cts.Token);
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Timeout reached, persist what we have
                }

                if (batch.Count > 0)
                {
                    try
                    {
                        var entries = batch.Select(e => new EventLogEntry
                        {
                            GlobalId = e.GlobalId,
                            EventType = e.Type.ToString(),
                            EventTime = e.Time,
                            Data = JsonSerializer.Serialize(e.Data)
                        });

                        await _eventLogRepository.SaveEventsAsync(entries);
                        _logger.LogDebug("Persisted {Count} events to database", batch.Count);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error persisting events to database");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in event persistence task");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cancellationTokenSource.Cancel(); } catch (ObjectDisposedException) { }
        _eventChannel.Writer.Complete();
        _persistenceChannel?.Writer.Complete();

        // Wait for processing task to complete with timeout
        try
        {
            _processingTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Expected during cancellation
        }

        // Wait for persistence task if running
        if (_persistenceTask != null)
        {
            try
            {
                _persistenceTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
                // Expected during cancellation
            }
        }

        foreach (var subscription in _subscriptions.Values)
        {
            subscription.Dispose();
        }

        _subscriptions.Clear();
        try { _cancellationTokenSource.Dispose(); } catch (ObjectDisposedException) { }
    }

    private bool _disposed;
}

/// <summary>
/// Represents a subscription to sync events.
/// </summary>
public class SyncEventSubscription : IDisposable
{
    private readonly Channel<SyncEvent> _channel;
    private readonly HashSet<SyncEventType>? _filter;

    public string SubscriberId { get; }

    public ChannelReader<SyncEvent> Events => _channel.Reader;

    public SyncEventSubscription(string subscriberId, SyncEventType[]? filter = null, int capacity = 1000)
    {
        SubscriberId = subscriberId;
        _filter = filter?.ToHashSet();

        _channel = Channel.CreateBounded<SyncEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    }

    /// <summary>
    /// Checks if this subscription should receive the event.
    /// </summary>
    public bool MatchesFilter(SyncEvent syncEvent)
    {
        return _filter == null || _filter.Contains(syncEvent.Type);
    }

    /// <summary>
    /// Tries to enqueue an event to this subscription.
    /// </summary>
    public bool TryEnqueue(SyncEvent syncEvent)
    {
        return _channel.Writer.TryWrite(syncEvent);
    }

    /// <summary>
    /// Waits for the next event.
    /// </summary>
    public async ValueTask<SyncEvent?> WaitForEventAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            if (await _channel.Reader.WaitToReadAsync(cts.Token))
            {
                if (_channel.Reader.TryRead(out var syncEvent))
                {
                    return syncEvent;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout or cancelled
        }

        return null;
    }

    public void Dispose()
    {
        try
        {
            _channel.Writer.TryComplete();
        }
        catch (ChannelClosedException)
        {
            // Already closed
        }
    }
}
