using CreatioHelper.Domain.Entities.Events;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Service for REST API-style event subscriptions.
/// Compatible with Syncthing's /rest/events endpoint.
/// </summary>
public interface IEventSubscriptionService
{
    /// <summary>
    /// Gets events since a specific event ID with filtering and timeout support.
    /// Compatible with GET /api/events?since=100&amp;limit=50&amp;timeout=60&amp;events=DeviceConnected,FolderError
    /// </summary>
    /// <param name="sinceId">Return events with ID greater than this value</param>
    /// <param name="limit">Maximum number of events to return</param>
    /// <param name="timeout">Maximum time to wait for events</param>
    /// <param name="eventMask">Bitwise mask of event types to include</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Matching events</returns>
    Task<IEnumerable<SyncEvent>> GetEventsAsync(
        long sinceId,
        int limit,
        TimeSpan timeout,
        SyncEventType eventMask,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses a comma-separated list of event type names into a bitwise mask.
    /// </summary>
    /// <param name="eventsCsv">Comma-separated event type names (e.g., "DeviceConnected,FolderError")</param>
    /// <returns>Bitwise mask of event types</returns>
    SyncEventType ParseEventMask(string? eventsCsv);

    /// <summary>
    /// Gets the ID of the last event.
    /// </summary>
    long GetLastEventId();

    /// <summary>
    /// Gets events since a specific ID without waiting.
    /// </summary>
    IEnumerable<SyncEvent> GetEventsSince(long sinceId, int limit = 100, SyncEventType? mask = null);
}
