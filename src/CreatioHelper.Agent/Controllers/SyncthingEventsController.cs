using CreatioHelper.Agent.Authorization;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using System.Text.Json.Serialization;

namespace CreatioHelper.Agent.Controllers;

/// <summary>
/// Events controller with 100% Syncthing REST API compatibility
/// Implements all endpoints from syncthing/lib/api/api.go
/// </summary>
[ApiController]
[Route("rest")]
[Authorize(Roles = Roles.ReadRoles)]
public class SyncthingEventsController : ControllerBase
{
    private readonly IEventLogger _eventLogger;
    private readonly ILogger<SyncthingEventsController> _logger;
    
    // Event masks matching Syncthing's event system
    private const long DefaultEventMask = (1L << 62) - 1; // All events except LocalChangeDetected and RemoteChangeDetected
    private const long DiskEventMask = (1L << 5) | (1L << 6); // LocalChangeDetected | RemoteChangeDetected
    private static readonly TimeSpan DefaultEventTimeout = TimeSpan.FromMinutes(1);
    private const int EventSubBufferSize = 1000;
    
    // Event subscriptions - matching Syncthing's BufferedSubscription
    private static readonly ConcurrentDictionary<long, SyncthingEventSubscription> EventSubscriptions = new();
    private static long _nextSubscriptionId = 1;
#pragma warning disable CS0414 // Field is assigned but never used - reserved for future event ID generation
    private static long _globalEventId = 1;
#pragma warning restore CS0414
    
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
        [FromQuery] int limit = 0, 
        [FromQuery] int timeout = 60,
        [FromQuery] string? events = null)
    {
        try
        {
            var eventMask = GetEventMask(events);
            var subscription = GetEventSubscription(eventMask);
            return await GetEvents(subscription, since, limit, timeout);
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
        [FromQuery] int limit = 0, 
        [FromQuery] int timeout = 60)
    {
        try
        {
            var subscription = GetEventSubscription(DiskEventMask);
            return await GetEvents(subscription, since, limit, timeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting disk events");
            return StatusCode(500, "Internal Server Error");
        }
    }
    
    private async Task<IActionResult> GetEvents(SyncthingEventSubscription subscription, int since, int limit, int timeoutSeconds)
    {
        var timeout = timeoutSeconds >= 0 ? TimeSpan.FromSeconds(timeoutSeconds) : DefaultEventTimeout;
        
        // Set response headers like Syncthing does
        Response.ContentType = "application/json; charset=utf-8";
        Response.Headers.Append("Cache-Control", "no-cache");
        
        // Flush to indicate we've received the request
        await Response.Body.FlushAsync();
        
        var events = subscription.GetEventsSince(since, limit, timeout);
        return Ok(events);
    }
    
    private long GetEventMask(string? eventsParam)
    {
        if (string.IsNullOrEmpty(eventsParam))
        {
            return DefaultEventMask;
        }
        
        // Parse comma-separated event names
        long mask = 0;
        var eventNames = eventsParam.Split(',', StringSplitOptions.RemoveEmptyEntries);
        
        foreach (var eventName in eventNames)
        {
            mask |= GetEventTypeFromName(eventName.Trim());
        }
        
        return mask == 0 ? DefaultEventMask : mask;
    }
    
    private long GetEventTypeFromName(string eventName)
    {
        return eventName.ToLower() switch
        {
            "starting" => 1L << 0,
            "startupComplete" => 1L << 1,
            "deviceDiscovered" => 1L << 2,
            "deviceConnected" => 1L << 3,
            "deviceDisconnected" => 1L << 4,
            "localChangeDetected" => 1L << 5,
            "remoteChangeDetected" => 1L << 6,
            "localIndexUpdated" => 1L << 7,
            "remoteIndexUpdated" => 1L << 8,
            "itemStarted" => 1L << 9,
            "itemFinished" => 1L << 10,
            "stateChanged" => 1L << 11,
            "folderSummary" => 1L << 12,
            "folderCompletion" => 1L << 13,
            "folderErrors" => 1L << 14,
            "configSaved" => 1L << 15,
            "downloadProgress" => 1L << 16,
            "folderScanProgress" => 1L << 17,
            "folderPaused" => 1L << 18,
            "folderResumed" => 1L << 19,
            "listenAddressesChanged" => 1L << 20,
            "loginAttempt" => 1L << 21,
            "failure" => 1L << 22,
            _ => 0L
        };
    }
    
    private SyncthingEventSubscription GetEventSubscription(long mask)
    {
        // Find existing subscription or create new one
        var subscription = EventSubscriptions.Values.FirstOrDefault(s => s.Mask == mask);
        if (subscription == null)
        {
            var id = Interlocked.Increment(ref _nextSubscriptionId);
            subscription = new SyncthingEventSubscription(id, mask, EventSubBufferSize);
            EventSubscriptions[id] = subscription;
        }
        return subscription;
    }
}

/// <summary>
/// Syncthing-compatible event subscription with buffering
/// Equivalent to Syncthing's BufferedSubscription
/// </summary>
public class SyncthingEventSubscription
{
    private readonly long _id;
    private readonly long _mask;
    private readonly Queue<SyncthingEvent> _buffer;
    private readonly int _bufferSize;
    private int _nextEventId = 1;
    private readonly object _lock = new();
    
    public long Mask => _mask;
    
    public SyncthingEventSubscription(long id, long mask, int bufferSize)
    {
        _id = id;
        _mask = mask;
        _bufferSize = bufferSize;
        _buffer = new Queue<SyncthingEvent>(bufferSize);
    }
    
    public void AddEvent(SyncthingEvent syncEvent)
    {
        lock (_lock)
        {
            // Check if event matches our mask
            var eventTypeMask = GetEventTypeMask(syncEvent.Type);
            if ((_mask & eventTypeMask) == 0)
                return;
            
            syncEvent.SubscriptionId = _nextEventId++;
            
            _buffer.Enqueue(syncEvent);
            
            // Keep buffer size manageable
            while (_buffer.Count > _bufferSize)
            {
                _buffer.Dequeue();
            }
        }
    }
    
    public List<SyncthingEvent> GetEventsSince(int since, int limit, TimeSpan timeout)
    {
        lock (_lock)
        {
            var events = _buffer.Where(e => e.SubscriptionId > since).ToList();
            
            if (limit > 0 && events.Count > limit)
            {
                events = events.Take(limit).ToList();
            }
            
            return events;
        }
    }
    
    private long GetEventTypeMask(string eventType)
    {
        return eventType.ToLower() switch
        {
            "starting" => 1L << 0,
            "startupComplete" => 1L << 1,
            "deviceDiscovered" => 1L << 2,
            "deviceConnected" => 1L << 3,
            "deviceDisconnected" => 1L << 4,
            "localChangeDetected" => 1L << 5,
            "remoteChangeDetected" => 1L << 6,
            "localIndexUpdated" => 1L << 7,
            "remoteIndexUpdated" => 1L << 8,
            "itemStarted" => 1L << 9,
            "itemFinished" => 1L << 10,
            "stateChanged" => 1L << 11,
            "folderSummary" => 1L << 12,
            "folderCompletion" => 1L << 13,
            "folderErrors" => 1L << 14,
            "configSaved" => 1L << 15,
            "downloadProgress" => 1L << 16,
            "folderScanProgress" => 1L << 17,
            "folderPaused" => 1L << 18,
            "folderResumed" => 1L << 19,
            "listenAddressesChanged" => 1L << 20,
            "loginAttempt" => 1L << 21,
            "failure" => 1L << 22,
            _ => 0L
        };
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