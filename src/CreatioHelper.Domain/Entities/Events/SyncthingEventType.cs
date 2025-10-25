using System.Text.Json.Serialization;

namespace CreatioHelper.Domain.Entities.Events;

/// <summary>
/// Syncthing-compatible event types
/// Exact match to syncthing/lib/events/events.go EventType constants
/// </summary>
[Flags]
public enum SyncthingEventType : long
{
    None = 0,
    Starting = 1L << 0,                    // 1
    StartupComplete = 1L << 1,             // 2
    DeviceDiscovered = 1L << 2,            // 4
    DeviceConnected = 1L << 3,             // 8
    DeviceDisconnected = 1L << 4,          // 16
    DeviceRejected = 1L << 5,              // 32 (DEPRECATED)
    PendingDevicesChanged = 1L << 6,       // 64
    DevicePaused = 1L << 7,                // 128
    DeviceResumed = 1L << 8,               // 256
    ClusterConfigReceived = 1L << 9,       // 512
    LocalChangeDetected = 1L << 10,        // 1024
    RemoteChangeDetected = 1L << 11,       // 2048
    LocalIndexUpdated = 1L << 12,          // 4096
    RemoteIndexUpdated = 1L << 13,         // 8192
    ItemStarted = 1L << 14,                // 16384
    ItemFinished = 1L << 15,               // 32768
    StateChanged = 1L << 16,               // 65536
    FolderRejected = 1L << 17,             // 131072 (DEPRECATED)
    PendingFoldersChanged = 1L << 18,      // 262144
    ConfigSaved = 1L << 19,                // 524288
    DownloadProgress = 1L << 20,           // 1048576
    RemoteDownloadProgress = 1L << 21,     // 2097152
    FolderSummary = 1L << 22,              // 4194304
    FolderCompletion = 1L << 23,           // 8388608
    FolderErrors = 1L << 24,               // 16777216
    FolderScanProgress = 1L << 25,         // 33554432
    FolderPaused = 1L << 26,               // 67108864
    FolderResumed = 1L << 27,              // 134217728
    FolderWatchStateChanged = 1L << 28,    // 268435456
    ListenAddressesChanged = 1L << 29,     // 536870912
    LoginAttempt = 1L << 30,               // 1073741824
    Failure = 1L << 31,                    // 2147483648
    
    // All events mask (from syncthing: (1 << iota) - 1)
    AllEvents = (1L << 32) - 1
}

public static class SyncthingEventTypeExtensions
{
    /// <summary>
    /// Convert event type to string (matching Syncthing's String() method)
    /// </summary>
    public static string ToSyncthingString(this SyncthingEventType eventType)
    {
        return eventType switch
        {
            SyncthingEventType.Starting => "Starting",
            SyncthingEventType.StartupComplete => "StartupComplete",
            SyncthingEventType.DeviceDiscovered => "DeviceDiscovered",
            SyncthingEventType.DeviceConnected => "DeviceConnected",
            SyncthingEventType.DeviceDisconnected => "DeviceDisconnected",
            SyncthingEventType.DeviceRejected => "DeviceRejected",
            SyncthingEventType.PendingDevicesChanged => "PendingDevicesChanged",
            SyncthingEventType.LocalChangeDetected => "LocalChangeDetected",
            SyncthingEventType.RemoteChangeDetected => "RemoteChangeDetected",
            SyncthingEventType.LocalIndexUpdated => "LocalIndexUpdated",
            SyncthingEventType.RemoteIndexUpdated => "RemoteIndexUpdated",
            SyncthingEventType.ItemStarted => "ItemStarted",
            SyncthingEventType.ItemFinished => "ItemFinished",
            SyncthingEventType.StateChanged => "StateChanged",
            SyncthingEventType.FolderRejected => "FolderRejected",
            SyncthingEventType.PendingFoldersChanged => "PendingFoldersChanged",
            SyncthingEventType.ConfigSaved => "ConfigSaved",
            SyncthingEventType.DownloadProgress => "DownloadProgress",
            SyncthingEventType.RemoteDownloadProgress => "RemoteDownloadProgress",
            SyncthingEventType.FolderSummary => "FolderSummary",
            SyncthingEventType.FolderCompletion => "FolderCompletion",
            SyncthingEventType.FolderErrors => "FolderErrors",
            SyncthingEventType.DevicePaused => "DevicePaused",
            SyncthingEventType.DeviceResumed => "DeviceResumed",
            SyncthingEventType.ClusterConfigReceived => "ClusterConfigReceived",
            SyncthingEventType.FolderScanProgress => "FolderScanProgress",
            SyncthingEventType.FolderPaused => "FolderPaused",
            SyncthingEventType.FolderResumed => "FolderResumed",
            SyncthingEventType.ListenAddressesChanged => "ListenAddressesChanged",
            SyncthingEventType.LoginAttempt => "LoginAttempt",
            SyncthingEventType.FolderWatchStateChanged => "FolderWatchStateChanged",
            SyncthingEventType.Failure => "Failure",
            _ => "Unknown"
        };
    }
    
    /// <summary>
    /// Parse event type from string (matching Syncthing's UnmarshalEventType)
    /// </summary>
    public static SyncthingEventType FromSyncthingString(string eventName)
    {
        return eventName switch
        {
            "Starting" => SyncthingEventType.Starting,
            "StartupComplete" => SyncthingEventType.StartupComplete,
            "DeviceDiscovered" => SyncthingEventType.DeviceDiscovered,
            "DeviceConnected" => SyncthingEventType.DeviceConnected,
            "DeviceDisconnected" => SyncthingEventType.DeviceDisconnected,
            "DeviceRejected" => SyncthingEventType.DeviceRejected,
            "PendingDevicesChanged" => SyncthingEventType.PendingDevicesChanged,
            "LocalChangeDetected" => SyncthingEventType.LocalChangeDetected,
            "RemoteChangeDetected" => SyncthingEventType.RemoteChangeDetected,
            "LocalIndexUpdated" => SyncthingEventType.LocalIndexUpdated,
            "RemoteIndexUpdated" => SyncthingEventType.RemoteIndexUpdated,
            "ItemStarted" => SyncthingEventType.ItemStarted,
            "ItemFinished" => SyncthingEventType.ItemFinished,
            "StateChanged" => SyncthingEventType.StateChanged,
            "FolderRejected" => SyncthingEventType.FolderRejected,
            "PendingFoldersChanged" => SyncthingEventType.PendingFoldersChanged,
            "ConfigSaved" => SyncthingEventType.ConfigSaved,
            "DownloadProgress" => SyncthingEventType.DownloadProgress,
            "RemoteDownloadProgress" => SyncthingEventType.RemoteDownloadProgress,
            "FolderSummary" => SyncthingEventType.FolderSummary,
            "FolderCompletion" => SyncthingEventType.FolderCompletion,
            "FolderErrors" => SyncthingEventType.FolderErrors,
            "DevicePaused" => SyncthingEventType.DevicePaused,
            "DeviceResumed" => SyncthingEventType.DeviceResumed,
            "ClusterConfigReceived" => SyncthingEventType.ClusterConfigReceived,
            "FolderScanProgress" => SyncthingEventType.FolderScanProgress,
            "FolderPaused" => SyncthingEventType.FolderPaused,
            "FolderResumed" => SyncthingEventType.FolderResumed,
            "ListenAddressesChanged" => SyncthingEventType.ListenAddressesChanged,
            "LoginAttempt" => SyncthingEventType.LoginAttempt,
            "FolderWatchStateChanged" => SyncthingEventType.FolderWatchStateChanged,
            "Failure" => SyncthingEventType.Failure,
            _ => SyncthingEventType.None
        };
    }
}