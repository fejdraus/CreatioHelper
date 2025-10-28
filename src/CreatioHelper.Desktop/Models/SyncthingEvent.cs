using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CreatioHelper.Models;

/// <summary>
/// Base DTO for Syncthing Event API response
/// Represents a single event from GET /rest/events
/// </summary>
public class SyncthingEvent
{
    /// <summary>
    /// Unique event ID, monotonically increasing
    /// </summary>
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>
    /// Event type (e.g., "StateChanged", "ItemStarted", "FolderCompletion")
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Timestamp when event occurred
    /// </summary>
    [JsonPropertyName("time")]
    public DateTime Time { get; set; }

    /// <summary>
    /// Event-specific data payload (varies by event type)
    /// </summary>
    [JsonPropertyName("data")]
    public JsonElement Data { get; set; }
}

/// <summary>
/// StateChanged event data
/// Emitted when a folder changes synchronization state
/// </summary>
public class StateChangedEventData
{
    /// <summary>
    /// Folder ID
    /// </summary>
    [JsonPropertyName("folder")]
    public string Folder { get; set; } = string.Empty;

    /// <summary>
    /// Previous state (idle, scanning, syncing, error)
    /// </summary>
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    /// <summary>
    /// New state (idle, scanning, syncing, error)
    /// </summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    /// <summary>
    /// Duration spent in 'from' state (seconds)
    /// </summary>
    [JsonPropertyName("duration")]
    public double Duration { get; set; }
}

/// <summary>
/// FolderCompletion event data
/// Emitted periodically during synchronization with completion percentage
/// </summary>
public class FolderCompletionEventData
{
    /// <summary>
    /// Folder ID
    /// </summary>
    [JsonPropertyName("folder")]
    public string Folder { get; set; } = string.Empty;

    /// <summary>
    /// Remote device ID
    /// </summary>
    [JsonPropertyName("device")]
    public string Device { get; set; } = string.Empty;

    /// <summary>
    /// Completion percentage (0-100)
    /// </summary>
    [JsonPropertyName("completion")]
    public double Completion { get; set; }

    /// <summary>
    /// Total global bytes
    /// </summary>
    [JsonPropertyName("globalBytes")]
    public long GlobalBytes { get; set; }

    /// <summary>
    /// Bytes still needed to sync
    /// </summary>
    [JsonPropertyName("needBytes")]
    public long NeedBytes { get; set; }

    /// <summary>
    /// Total global items
    /// </summary>
    [JsonPropertyName("globalItems")]
    public int GlobalItems { get; set; }

    /// <summary>
    /// Items still needed to sync
    /// </summary>
    [JsonPropertyName("needItems")]
    public int NeedItems { get; set; }
}

/// <summary>
/// ItemStarted event data
/// Emitted when Syncthing begins synchronizing a file
/// </summary>
public class ItemStartedEventData
{
    /// <summary>
    /// Item (file/directory) path
    /// </summary>
    [JsonPropertyName("item")]
    public string Item { get; set; } = string.Empty;

    /// <summary>
    /// Folder ID
    /// </summary>
    [JsonPropertyName("folder")]
    public string Folder { get; set; } = string.Empty;

    /// <summary>
    /// Item type (file, dir, symlink)
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Action being performed (update, metadata, delete)
    /// </summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;
}

/// <summary>
/// ItemFinished event data
/// Emitted when Syncthing finishes synchronizing a file
/// </summary>
public class ItemFinishedEventData
{
    /// <summary>
    /// Item (file/directory) path
    /// </summary>
    [JsonPropertyName("item")]
    public string Item { get; set; } = string.Empty;

    /// <summary>
    /// Folder ID
    /// </summary>
    [JsonPropertyName("folder")]
    public string Folder { get; set; } = string.Empty;

    /// <summary>
    /// Item type (file, dir, symlink)
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Action performed (update, metadata, delete)
    /// </summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Error message if synchronization failed, null if successful
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// DeviceConnected event data
/// Emitted when a remote device connects
/// </summary>
public class DeviceConnectedEventData
{
    /// <summary>
    /// Device ID
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Device address
    /// </summary>
    [JsonPropertyName("addr")]
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Connection type (tcp, quic, relay)
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

/// <summary>
/// DeviceDisconnected event data
/// Emitted when a remote device disconnects
/// </summary>
public class DeviceDisconnectedEventData
{
    /// <summary>
    /// Device ID
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Disconnection error/reason
    /// </summary>
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;
}
