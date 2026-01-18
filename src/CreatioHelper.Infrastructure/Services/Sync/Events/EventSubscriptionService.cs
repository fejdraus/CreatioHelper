using CreatioHelper.Application.Interfaces;
using DomainEvents = CreatioHelper.Domain.Entities.Events;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Events;

/// <summary>
/// Service for REST API-style event subscriptions.
/// Provides Syncthing-compatible event polling with long-polling support.
/// </summary>
public class EventSubscriptionService : IEventSubscriptionService
{
    private readonly SyncEventQueue _eventQueue;
    private readonly ILogger<EventSubscriptionService> _logger;

    /// <summary>
    /// Polling interval when waiting for events.
    /// </summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    public EventSubscriptionService(
        SyncEventQueue eventQueue,
        ILogger<EventSubscriptionService> logger)
    {
        _eventQueue = eventQueue;
        _logger = logger;
    }

    public async Task<IEnumerable<DomainEvents.SyncEvent>> GetEventsAsync(
        long sinceId,
        int limit,
        TimeSpan timeout,
        DomainEvents.SyncEventType eventMask,
        CancellationToken cancellationToken = default)
    {
        var events = new List<DomainEvents.SyncEvent>();
        var deadline = DateTime.UtcNow + timeout;
        var currentSinceId = sinceId;

        _logger.LogDebug(
            "Getting events since {SinceId} with limit {Limit}, timeout {Timeout}ms, mask {Mask}",
            sinceId, limit, timeout.TotalMilliseconds, eventMask);

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            // Get batch of events from queue
            var batch = _eventQueue.GetEventsSince(currentSinceId, limit * 2);

            foreach (var evt in batch)
            {
                var domainEvent = ConvertToDomainEvent(evt);

                // Apply bitwise filter
                if ((eventMask & domainEvent.Type) != 0)
                {
                    events.Add(domainEvent);
                    currentSinceId = evt.Id;

                    if (events.Count >= limit)
                    {
                        _logger.LogDebug("Returning {Count} events (limit reached)", events.Count);
                        return events;
                    }
                }
                else
                {
                    // Still update sinceId even for filtered events
                    currentSinceId = evt.Id;
                }
            }

            // If we got some events, return them
            if (events.Count > 0)
            {
                _logger.LogDebug("Returning {Count} events", events.Count);
                return events;
            }

            // Wait for more events (long-polling)
            try
            {
                await Task.Delay(PollingInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogDebug("Returning {Count} events (timeout or cancelled)", events.Count);
        return events;
    }

    public DomainEvents.SyncEventType ParseEventMask(string? eventsCsv)
    {
        if (string.IsNullOrEmpty(eventsCsv))
        {
            return DomainEvents.SyncEventType.DefaultEventMask;
        }

        var mask = DomainEvents.SyncEventType.None;
        var names = eventsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var name in names)
        {
            if (Enum.TryParse<DomainEvents.SyncEventType>(name, ignoreCase: true, out var eventType))
            {
                mask |= eventType;
            }
            else
            {
                _logger.LogWarning("Unknown event type: {EventType}", name);
            }
        }

        // If no valid types were parsed, use default mask
        return mask == DomainEvents.SyncEventType.None ? DomainEvents.SyncEventType.DefaultEventMask : mask;
    }

    public long GetLastEventId()
    {
        return _eventQueue.GetLastEventId();
    }

    public IEnumerable<DomainEvents.SyncEvent> GetEventsSince(long sinceId, int limit = 100, DomainEvents.SyncEventType? mask = null)
    {
        var events = _eventQueue.GetEventsSince(sinceId, limit)
            .Select(ConvertToDomainEvent);

        if (mask.HasValue)
        {
            events = events.Where(e => (mask.Value & e.Type) != 0);
        }

        return events;
    }

    /// <summary>
    /// Converts Infrastructure SyncEvent to Domain SyncEvent.
    /// </summary>
    private DomainEvents.SyncEvent ConvertToDomainEvent(SyncEvent infraEvent)
    {
        return new DomainEvents.SyncEvent
        {
            GlobalId = (int)(infraEvent.Id % int.MaxValue),
            Time = infraEvent.Time,
            Type = MapEventType(infraEvent.Type),
            Data = infraEvent.Data.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        };
    }

    /// <summary>
    /// Maps Infrastructure SyncEventType to Domain SyncEventType.
    /// </summary>
    private static DomainEvents.SyncEventType MapEventType(SyncEventType infraType)
    {
        return infraType switch
        {
            SyncEventType.DeviceConnected => DomainEvents.SyncEventType.DeviceConnected,
            SyncEventType.DeviceDisconnected => DomainEvents.SyncEventType.DeviceDisconnected,
            SyncEventType.DeviceDiscovered => DomainEvents.SyncEventType.DeviceDiscovered,
            SyncEventType.DevicePaused => DomainEvents.SyncEventType.DevicePaused,
            SyncEventType.DeviceResumed => DomainEvents.SyncEventType.DeviceResumed,
            SyncEventType.FolderScanProgress => DomainEvents.SyncEventType.FolderScanProgress,
            SyncEventType.FolderScanComplete => DomainEvents.SyncEventType.FolderScanComplete,
            SyncEventType.FolderStateChanged => DomainEvents.SyncEventType.StateChanged,
            SyncEventType.FolderCompletion => DomainEvents.SyncEventType.FolderCompletion,
            SyncEventType.FolderError => DomainEvents.SyncEventType.FolderErrors,
            SyncEventType.FolderPaused => DomainEvents.SyncEventType.FolderPaused,
            SyncEventType.FolderResumed => DomainEvents.SyncEventType.FolderResumed,
            SyncEventType.LocalChangeDetected => DomainEvents.SyncEventType.LocalChangeDetected,
            SyncEventType.RemoteChangeReceived => DomainEvents.SyncEventType.RemoteChangeDetected,
            SyncEventType.ItemStarted => DomainEvents.SyncEventType.ItemStarted,
            SyncEventType.ItemFinished => DomainEvents.SyncEventType.ItemFinished,
            SyncEventType.DownloadProgress => DomainEvents.SyncEventType.DownloadProgress,
            SyncEventType.ConfigSaved => DomainEvents.SyncEventType.ConfigSaved,
            SyncEventType.ConnectionError => DomainEvents.SyncEventType.Failure,
            SyncEventType.SyncError => DomainEvents.SyncEventType.SyncError,
            SyncEventType.ConflictDetected => DomainEvents.SyncEventType.Warning,
            SyncEventType.StateChanged => DomainEvents.SyncEventType.StateChanged,
            SyncEventType.PendingChanges => DomainEvents.SyncEventType.Information,
            SyncEventType.NatTypeDetected => DomainEvents.SyncEventType.Information,
            SyncEventType.ExternalAddressDiscovered => DomainEvents.SyncEventType.ListenAddressesChanged,
            _ => DomainEvents.SyncEventType.Information
        };
    }
}

/// <summary>
/// Extension methods for event subscription parsing.
/// </summary>
public static class EventSubscriptionExtensions
{
    /// <summary>
    /// Converts a SyncEventType mask to a comma-separated string of event names.
    /// </summary>
    public static string ToEventString(this DomainEvents.SyncEventType mask)
    {
        if (mask == DomainEvents.SyncEventType.None)
            return string.Empty;

        if (mask == DomainEvents.SyncEventType.AllEvents)
            return "AllEvents";

        var names = new List<string>();

        foreach (DomainEvents.SyncEventType value in Enum.GetValues(typeof(DomainEvents.SyncEventType)))
        {
            // Skip composite masks
            if (value == DomainEvents.SyncEventType.None ||
                value == DomainEvents.SyncEventType.AllEvents ||
                value == DomainEvents.SyncEventType.DefaultEventMask ||
                value == DomainEvents.SyncEventType.DeviceEvents ||
                value == DomainEvents.SyncEventType.FolderEvents ||
                value == DomainEvents.SyncEventType.TransferEvents ||
                value == DomainEvents.SyncEventType.ChangeEvents ||
                value == DomainEvents.SyncEventType.SystemEvents ||
                value == DomainEvents.SyncEventType.SecurityEvents ||
                value == DomainEvents.SyncEventType.SyncEvents ||
                value == DomainEvents.SyncEventType.VersioningEvents ||
                value == DomainEvents.SyncEventType.NetworkEvents ||
                value == DomainEvents.SyncEventType.DiscoveryEvents ||
                value == DomainEvents.SyncEventType.ErrorEvents ||
                value == DomainEvents.SyncEventType.TransferCompletedEvents ||
                value == DomainEvents.SyncEventType.TransferFailedEvents)
            {
                continue;
            }

            if ((mask & value) == value)
            {
                names.Add(value.ToString());
            }
        }

        return string.Join(",", names);
    }

    /// <summary>
    /// Gets the individual event types from a mask.
    /// </summary>
    public static IEnumerable<DomainEvents.SyncEventType> GetIndividualTypes(this DomainEvents.SyncEventType mask)
    {
        foreach (DomainEvents.SyncEventType value in Enum.GetValues(typeof(DomainEvents.SyncEventType)))
        {
            // Skip None and composite masks
            if (value == DomainEvents.SyncEventType.None || !IsSingleFlag(value))
            {
                continue;
            }

            if ((mask & value) == value)
            {
                yield return value;
            }
        }
    }

    private static bool IsSingleFlag(DomainEvents.SyncEventType value)
    {
        var v = (long)value;
        return v != 0 && (v & (v - 1)) == 0;
    }
}
