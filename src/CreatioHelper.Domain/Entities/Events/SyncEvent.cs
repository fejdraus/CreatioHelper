using System.Text.Json;

namespace CreatioHelper.Domain.Entities.Events;

/// <summary>
/// Sync event entity (based on Syncthing Event struct)
/// Represents a single synchronization event with metadata
/// </summary>
public class SyncEvent
{
    /// <summary>
    /// Per-subscription sequential event ID (for REST API compatibility)
    /// </summary>
    public int SubscriptionId { get; set; }

    /// <summary>
    /// Global event ID across all subscriptions
    /// </summary>
    public int GlobalId { get; set; }

    /// <summary>
    /// Timestamp when the event occurred
    /// </summary>
    public DateTime Time { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Type of the event
    /// </summary>
    public SyncEventType Type { get; set; }

    /// <summary>
    /// Event data (can be any serializable object)
    /// </summary>
    public object? Data { get; set; }

    /// <summary>
    /// Human-readable message describing the event
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Device ID associated with the event (if applicable)
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Folder ID associated with the event (if applicable)
    /// </summary>
    public string? FolderId { get; set; }

    /// <summary>
    /// File path associated with the event (if applicable)
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Event priority level
    /// </summary>
    public EventPriority Priority { get; set; } = EventPriority.Normal;

    /// <summary>
    /// Additional metadata as key-value pairs
    /// </summary>
    public Dictionary<string, object?> Metadata { get; set; } = new();

    /// <summary>
    /// Create a system-level event
    /// </summary>
    public static SyncEvent SystemEvent(SyncEventType type, string message, object? data = null)
    {
        return new SyncEvent
        {
            Type = type,
            Message = message,
            Data = data,
            Priority = GetDefaultPriority(type)
        };
    }

    /// <summary>
    /// Create a device-related event
    /// </summary>
    public static SyncEvent DeviceEvent(SyncEventType type, string deviceId, string message, object? data = null)
    {
        return new SyncEvent
        {
            Type = type,
            DeviceId = deviceId,
            Message = message,
            Data = data,
            Priority = GetDefaultPriority(type)
        };
    }

    /// <summary>
    /// Create a folder-related event
    /// </summary>
    public static SyncEvent FolderEvent(SyncEventType type, string folderId, string message, object? data = null)
    {
        return new SyncEvent
        {
            Type = type,
            FolderId = folderId,
            Message = message,
            Data = data,
            Priority = GetDefaultPriority(type)
        };
    }

    /// <summary>
    /// Create a file-related event
    /// </summary>
    public static SyncEvent FileEvent(SyncEventType type, string folderId, string filePath, string message, object? data = null)
    {
        return new SyncEvent
        {
            Type = type,
            FolderId = folderId,
            FilePath = filePath,
            Message = message,
            Data = data,
            Priority = GetDefaultPriority(type)
        };
    }

    /// <summary>
    /// Create an error event
    /// </summary>
    public static SyncEvent ErrorEvent(Exception exception, string? context = null, string? deviceId = null, string? folderId = null)
    {
        return new SyncEvent
        {
            Type = SyncEventType.Failure,
            DeviceId = deviceId,
            FolderId = folderId,
            Message = $"{context}: {exception.Message}",
            Data = new
            {
                ExceptionType = exception.GetType().Name,
                Message = exception.Message,
                StackTrace = exception.StackTrace,
                Context = context
            },
            Priority = EventPriority.High
        };
    }

    /// <summary>
    /// Get default priority for event type
    /// </summary>
    private static EventPriority GetDefaultPriority(SyncEventType type)
    {
        return type switch
        {
            SyncEventType.Failure => EventPriority.High,
            SyncEventType.DeviceRejected => EventPriority.High,
            SyncEventType.FolderRejected => EventPriority.High,
            SyncEventType.DeviceConnected => EventPriority.Normal,
            SyncEventType.DeviceDisconnected => EventPriority.Normal,
            SyncEventType.ItemStarted => EventPriority.Low,
            SyncEventType.ItemFinished => EventPriority.Low,
            SyncEventType.DownloadProgress => EventPriority.Low,
            SyncEventType.RemoteDownloadProgress => EventPriority.Low,
            SyncEventType.LocalChangeDetected => EventPriority.Normal,
            SyncEventType.RemoteChangeDetected => EventPriority.Normal,
            _ => EventPriority.Normal
        };
    }

    /// <summary>
    /// Serialize event data to JSON
    /// </summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        });
    }

    /// <summary>
    /// Create event from JSON
    /// </summary>
    public static SyncEvent? FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SyncEvent>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Check if event matches the specified type mask
    /// </summary>
    public bool MatchesMask(SyncEventType mask)
    {
        return (Type & mask) != 0;
    }

    public override string ToString()
    {
        var parts = new List<string> { Type.ToString() };
        
        if (!string.IsNullOrEmpty(DeviceId))
            parts.Add($"Device={DeviceId}");
            
        if (!string.IsNullOrEmpty(FolderId))
            parts.Add($"Folder={FolderId}");
            
        if (!string.IsNullOrEmpty(FilePath))
            parts.Add($"File={FilePath}");
            
        if (!string.IsNullOrEmpty(Message))
            parts.Add($"Message={Message}");

        return $"SyncEvent[{string.Join(", ", parts)}]";
    }
}

/// <summary>
/// Event priority levels for filtering and handling
/// </summary>
public enum EventPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3
}