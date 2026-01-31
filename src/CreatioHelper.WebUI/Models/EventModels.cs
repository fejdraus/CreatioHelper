using System.Text.Json.Serialization;

namespace CreatioHelper.WebUI.Models;

/// <summary>
/// Sync event from /rest/events
/// </summary>
public class SyncEvent
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("globalID")]
    public int GlobalId { get; set; }

    [JsonPropertyName("time")]
    public DateTime Time { get; set; }

    [JsonPropertyName("type")]
    public string RawType { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public Dictionary<string, object>? Data { get; set; }

    // Convenience property for timestamp
    [JsonIgnore]
    public DateTime Timestamp => Time;

    // Parsed type enum
    [JsonIgnore]
    public EventType Type => RawType switch
    {
        "ConfigSaved" => EventType.ConfigSaved,
        "DeviceConnected" => EventType.DeviceConnected,
        "DeviceDisconnected" => EventType.DeviceDisconnected,
        "DeviceDiscovered" => EventType.DeviceDiscovered,
        "DevicePaused" => EventType.DevicePaused,
        "DeviceResumed" => EventType.DeviceResumed,
        "DeviceRejected" => EventType.DeviceRejected,
        "DownloadProgress" => EventType.DownloadProgress,
        "FolderCompletion" => EventType.FolderCompletion,
        "FolderErrors" => EventType.FolderErrors,
        "FolderPaused" => EventType.FolderPaused,
        "FolderResumed" => EventType.FolderResumed,
        "FolderRejected" => EventType.FolderRejected,
        "FolderScanProgress" => EventType.FolderScanProgress,
        "FolderSummary" => EventType.FolderSummary,
        "FolderWatchStateChanged" => EventType.FolderWatchStateChanged,
        "ItemFinished" => EventType.ItemFinished,
        "ItemStarted" => EventType.ItemStarted,
        "ListenAddressesChanged" => EventType.ListenAddressesChanged,
        "LocalChangeDetected" => EventType.LocalChangeDetected,
        "LocalIndexUpdated" => EventType.LocalIndexUpdated,
        "LoginAttempt" => EventType.LoginAttempt,
        "Failure" => EventType.Failure,
        "PendingDevicesChanged" => EventType.PendingDevicesChanged,
        "PendingFoldersChanged" => EventType.PendingFoldersChanged,
        "RemoteChangeDetected" => EventType.RemoteChangeDetected,
        "RemoteDownloadProgress" => EventType.RemoteDownloadProgress,
        "RemoteIndexUpdated" => EventType.RemoteIndexUpdated,
        "Starting" => EventType.Starting,
        "StartupComplete" => EventType.StartupComplete,
        "StateChanged" => EventType.StateChanged,
        "ClusterConfigReceived" => EventType.ClusterConfigReceived,
        "DiscoveryCompleted" => EventType.DiscoveryCompleted,
        _ => EventType.Unknown
    };

    // Helper properties to get data from the event
    [JsonIgnore]
    public string? FolderId => GetDataValue("folder") is { Length: > 0 } f ? f : GetDataValue("folderId");
    [JsonIgnore]
    public string? DeviceId => GetDataValue("device") is { Length: > 0 } d ? d : GetDataValue("id") is { Length: > 0 } i ? i : GetDataValue("deviceId");

    // Debug: get all keys in Data dictionary
    [JsonIgnore]
    public string DataKeys => Data != null ? string.Join(", ", Data.Keys) : "(no data)";
    [JsonIgnore]
    public string? FileName => GetDataValue("item") ?? GetDataValue("path");
    [JsonIgnore]
    public string? Error => GetDataValue("error");
    [JsonIgnore]
    public string? From => GetDataValue("from");
    [JsonIgnore]
    public string? To => GetDataValue("to");
    [JsonIgnore]
    public string? Message => GetDataValue("message");

    [JsonIgnore]
    public EventSeverity Severity => Type switch
    {
        EventType.Failure or EventType.FolderErrors or EventType.DeviceRejected or EventType.FolderRejected => EventSeverity.Error,
        EventType.DeviceDisconnected or EventType.DevicePaused or EventType.FolderPaused => EventSeverity.Warning,
        EventType.DeviceConnected or EventType.StartupComplete or EventType.ItemFinished => EventSeverity.Success,
        _ => EventSeverity.Info
    };

    public string GetDataValue(string key)
    {
        if (Data == null || !Data.TryGetValue(key, out var value))
            return string.Empty;

        // Handle JsonElement from System.Text.Json deserialization
        if (value is System.Text.Json.JsonElement jsonElement)
        {
            return jsonElement.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => jsonElement.GetString() ?? string.Empty,
                System.Text.Json.JsonValueKind.Number => jsonElement.GetRawText(),
                System.Text.Json.JsonValueKind.True => "true",
                System.Text.Json.JsonValueKind.False => "false",
                System.Text.Json.JsonValueKind.Null => string.Empty,
                _ => jsonElement.GetRawText()
            };
        }

        return value?.ToString() ?? string.Empty;
    }

    [JsonIgnore]
    public string Summary => Type switch
    {
        EventType.DeviceConnected => $"Device {GetDataValueSafe("id", 7)} connected",
        EventType.DeviceDisconnected => $"Device {GetDataValueSafe("id", 7)} disconnected",
        EventType.FolderCompletion => $"Folder '{GetDataValue("folder")}' completion changed",
        EventType.FolderErrors => $"Folder '{GetDataValue("folder")}' has errors",
        EventType.ItemFinished => $"Sync completed: {GetDataValue("item")}",
        EventType.ItemStarted => $"Syncing: {GetDataValue("item")}",
        EventType.StateChanged => $"Folder '{GetDataValue("folder")}': {GetDataValue("from")} -> {GetDataValue("to")}",
        EventType.LocalChangeDetected => $"Local change: {GetDataValue("path")}",
        EventType.RemoteChangeDetected => $"Remote change: {GetDataValue("path")}",
        EventType.Failure => $"Failure: {GetDataValue("error")}",
        EventType.StartupComplete => "Startup complete",
        EventType.Starting => "Starting up",
        _ => RawType
    };

    private string GetDataValueSafe(string key, int maxLength)
    {
        var value = GetDataValue(key);
        if (string.IsNullOrEmpty(value)) return "unknown";
        return value.Length > maxLength ? value[..maxLength] : value;
    }
}

public enum EventType
{
    Unknown,
    ConfigSaved,
    DeviceConnected,
    DeviceDisconnected,
    DeviceDiscovered,
    DevicePaused,
    DeviceResumed,
    DeviceRejected,
    DownloadProgress,
    FolderCompletion,
    FolderErrors,
    FolderPaused,
    FolderResumed,
    FolderRejected,
    FolderScanProgress,
    FolderSummary,
    FolderWatchStateChanged,
    ItemFinished,
    ItemStarted,
    ListenAddressesChanged,
    LocalChangeDetected,
    LocalIndexUpdated,
    LoginAttempt,
    Failure,
    PendingDevicesChanged,
    PendingFoldersChanged,
    RemoteChangeDetected,
    RemoteDownloadProgress,
    RemoteIndexUpdated,
    Starting,
    StartupComplete,
    StateChanged,
    ClusterConfigReceived,
    DiscoveryCompleted
}

public enum EventSeverity
{
    Info,
    Success,
    Warning,
    Error
}
