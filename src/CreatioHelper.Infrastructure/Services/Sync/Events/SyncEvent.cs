using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreatioHelper.Infrastructure.Services.Sync.Events;

/// <summary>
/// Types of sync events that can be broadcast to clients.
/// Compatible with Syncthing's event system.
/// </summary>
public enum SyncEventType
{
    /// <summary>Device connected to the local node.</summary>
    DeviceConnected,

    /// <summary>Device disconnected from the local node.</summary>
    DeviceDisconnected,

    /// <summary>Device discovered via global/local discovery.</summary>
    DeviceDiscovered,

    /// <summary>Device was paused.</summary>
    DevicePaused,

    /// <summary>Device was resumed.</summary>
    DeviceResumed,

    /// <summary>Folder scan started.</summary>
    FolderScanProgress,

    /// <summary>Folder scan completed.</summary>
    FolderScanComplete,

    /// <summary>Folder state changed (idle, scanning, syncing, etc.).</summary>
    FolderStateChanged,

    /// <summary>Folder synchronization completed.</summary>
    FolderCompletion,

    /// <summary>Folder error occurred.</summary>
    FolderError,

    /// <summary>Folder was paused.</summary>
    FolderPaused,

    /// <summary>Folder was resumed.</summary>
    FolderResumed,

    /// <summary>Local file changed.</summary>
    LocalChangeDetected,

    /// <summary>Remote file change received.</summary>
    RemoteChangeReceived,

    /// <summary>File download started.</summary>
    ItemStarted,

    /// <summary>File download completed.</summary>
    ItemFinished,

    /// <summary>Download progress update.</summary>
    DownloadProgress,

    /// <summary>Configuration changed.</summary>
    ConfigSaved,

    /// <summary>Connection error.</summary>
    ConnectionError,

    /// <summary>General sync error.</summary>
    SyncError,

    /// <summary>Conflict detected.</summary>
    ConflictDetected,

    /// <summary>State changed event (aggregated state).</summary>
    StateChanged,

    /// <summary>Pending changes detected.</summary>
    PendingChanges,

    /// <summary>NAT type detected.</summary>
    NatTypeDetected,

    /// <summary>External address discovered.</summary>
    ExternalAddressDiscovered
}

/// <summary>
/// Base class for all sync events.
/// Compatible with Syncthing's event structure.
/// </summary>
public class SyncEvent
{
    /// <summary>
    /// Unique event ID for ordering and deduplication.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// Global timestamp for when the event occurred.
    /// </summary>
    public DateTime GlobalId { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Event type.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SyncEventType Type { get; set; }

    /// <summary>
    /// Event timestamp.
    /// </summary>
    public DateTime Time { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Event data (type-specific). Thread-safe for concurrent access.
    /// </summary>
    public ConcurrentDictionary<string, object?> Data { get; set; } = new();

    /// <summary>
    /// Serializes the event to JSON.
    /// </summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }
}

/// <summary>
/// Device connection event data.
/// </summary>
public class DeviceEventData
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? ConnectionType { get; set; }
    public string? ClientName { get; set; }
    public string? ClientVersion { get; set; }
}

/// <summary>
/// Folder event data.
/// </summary>
public class FolderEventData
{
    public string FolderId { get; set; } = string.Empty;
    public string FolderLabel { get; set; } = string.Empty;
    public string? State { get; set; }
    public string? PrevState { get; set; }
    public int? LocalFiles { get; set; }
    public long? LocalBytes { get; set; }
    public int? GlobalFiles { get; set; }
    public long? GlobalBytes { get; set; }
    public int? NeedFiles { get; set; }
    public long? NeedBytes { get; set; }
    public double? Completion { get; set; }
}

/// <summary>
/// Item (file) event data.
/// </summary>
public class ItemEventData
{
    public string FolderId { get; set; } = string.Empty;
    public string Item { get; set; } = string.Empty;
    public string? Type { get; set; }
    public string? Action { get; set; }
    public long? Size { get; set; }
    public string? Error { get; set; }
    public string? FromDevice { get; set; }
}

/// <summary>
/// Download progress event data.
/// </summary>
public class DownloadProgressData
{
    public string FolderId { get; set; } = string.Empty;
    public string Item { get; set; } = string.Empty;
    public long BytesTotal { get; set; }
    public long BytesDone { get; set; }
    public int BlocksTotal { get; set; }
    public int BlocksDone { get; set; }
    public double BytesPerSecond { get; set; }
}

/// <summary>
/// Error event data.
/// </summary>
public class ErrorEventData
{
    public string? FolderId { get; set; }
    public string? DeviceId { get; set; }
    public string Error { get; set; } = string.Empty;
    public string? Details { get; set; }
}
