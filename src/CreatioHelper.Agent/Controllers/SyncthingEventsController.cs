using CreatioHelper.Agent.Authorization;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using SyncEventType = CreatioHelper.Domain.Entities.Events.SyncEventType;

namespace CreatioHelper.Agent.Controllers;

/// <summary>
/// Events controller with 100% Syncthing REST API compatibility
/// Implements all endpoints from syncthing/lib/api/api.go
/// </summary>
[ApiController]
[Route("rest")]
[Authorize(Roles = Roles.MonitorRoles)]
public class SyncthingEventsController : ControllerBase
{
    private readonly IEventLogger _eventLogger;
    private readonly ILogger<SyncthingEventsController> _logger;
    
    private static readonly TimeSpan DefaultEventTimeout = TimeSpan.FromMinutes(1);
    
    public SyncthingEventsController(IEventLogger eventLogger, ILogger<SyncthingEventsController> logger)
    {
        _eventLogger = eventLogger;
        _logger = logger;
    }
    
    /// <summary>
    /// GET /rest/events - Get index events (equivalent to Syncthing's getIndexEvents)
    /// Query parameters: since, limit, timeout, events
    /// </summary>
    [HttpGet("events")]
    public async Task<IActionResult> GetIndexEvents(
        [FromQuery] int since = 0,
        [FromQuery] int limit = 100,
        [FromQuery] int timeout = 60,
        [FromQuery] string? events = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var eventMask = GetEventMaskFromString(events);
            var timeoutSpan = timeout >= 0 ? TimeSpan.FromSeconds(timeout) : DefaultEventTimeout;
            var maxLimit = limit > 0 ? Math.Min(limit, 10000) : 100;

            var eventList = await _eventLogger.GetEventsSinceAsync(since, eventMask, maxLimit, timeoutSpan, cancellationToken);

            // Convert to Syncthing format
            var syncthingEvents = eventList.Select(e => new SyncthingEvent
            {
                SubscriptionId = e.SubscriptionId > 0 ? e.SubscriptionId : e.GlobalId,
                GlobalId = e.GlobalId,
                Time = e.Time,
                Type = e.Type.ToString(),
                Data = e.Data
            }).ToList();

            return Ok(syncthingEvents);
        }
        catch (OperationCanceledException)
        {
            return Ok(new List<SyncthingEvent>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting index events");
            return StatusCode(500, "Internal Server Error");
        }
    }
    
    /// <summary>
    /// GET /rest/events/disk - Get disk events (equivalent to Syncthing's getDiskEvents)
    /// Query parameters: since, limit, timeout
    /// </summary>
    [HttpGet("events/disk")]
    public async Task<IActionResult> GetDiskEvents(
        [FromQuery] int since = 0,
        [FromQuery] int limit = 100,
        [FromQuery] int timeout = 60,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Disk events are LocalChangeDetected | RemoteChangeDetected
            var eventMask = SyncEventType.LocalChangeDetected | SyncEventType.RemoteChangeDetected;
            var timeoutSpan = timeout >= 0 ? TimeSpan.FromSeconds(timeout) : DefaultEventTimeout;
            var maxLimit = limit > 0 ? Math.Min(limit, 10000) : 100;

            var eventList = await _eventLogger.GetEventsSinceAsync(since, eventMask, maxLimit, timeoutSpan, cancellationToken);

            // Convert to Syncthing format
            var syncthingEvents = eventList.Select(e => new SyncthingEvent
            {
                SubscriptionId = e.SubscriptionId > 0 ? e.SubscriptionId : e.GlobalId,
                GlobalId = e.GlobalId,
                Time = e.Time,
                Type = e.Type.ToString(),
                Data = e.Data
            }).ToList();

            return Ok(syncthingEvents);
        }
        catch (OperationCanceledException)
        {
            return Ok(new List<SyncthingEvent>());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting disk events");
            return StatusCode(500, "Internal Server Error");
        }
    }

    private SyncEventType GetEventMaskFromString(string? eventsParam)
    {
        if (string.IsNullOrEmpty(eventsParam))
        {
            return SyncEventType.DefaultEventMask;
        }

        // Parse comma-separated event names
        SyncEventType mask = 0;
        var eventNames = eventsParam.Split(',', StringSplitOptions.RemoveEmptyEntries);

        foreach (var eventName in eventNames)
        {
            if (Enum.TryParse<SyncEventType>(eventName.Trim(), true, out var eventType))
            {
                mask |= eventType;
            }
        }

        return mask == 0 ? SyncEventType.DefaultEventMask : mask;
    }
}

/// <summary>
/// Syncthing-compatible event structure
/// Exact match to Syncthing's Event struct in events.go
/// </summary>
public class SyncthingEvent
{
    [JsonPropertyName("id")]
    public int SubscriptionId { get; set; }

    [JsonPropertyName("globalID")]
    public long GlobalId { get; set; }

    [JsonPropertyName("time")]
    public DateTime Time { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public object? Data { get; set; }
}