namespace CreatioHelper.Domain.Entities.Events;

/// <summary>
/// Sync event types (based on Syncthing EventType)
/// Uses bitwise flags for event masking and filtering
/// </summary>
[Flags]
public enum SyncEventType : long
{
    None = 0,
    
    // System events
    Starting = 1L << 0,
    StartupComplete = 1L << 1,
    ConfigSaved = 1L << 2,
    Failure = 1L << 3,
    
    // Device events  
    DeviceDiscovered = 1L << 4,
    DeviceConnected = 1L << 5,
    DeviceDisconnected = 1L << 6,
    DeviceRejected = 1L << 7,
    PendingDevicesChanged = 1L << 8,
    DevicePaused = 1L << 9,
    DeviceResumed = 1L << 10,
    ClusterConfigReceived = 1L << 11,
    
    // Change detection events
    LocalChangeDetected = 1L << 12,
    RemoteChangeDetected = 1L << 13,
    LocalIndexUpdated = 1L << 14,
    RemoteIndexUpdated = 1L << 15,
    
    // File transfer events
    ItemStarted = 1L << 16,
    ItemFinished = 1L << 17,
    StateChanged = 1L << 18,
    DownloadProgress = 1L << 19,
    RemoteDownloadProgress = 1L << 20,
    
    // Folder events
    FolderRejected = 1L << 21,
    PendingFoldersChanged = 1L << 22,
    FolderSummary = 1L << 23,
    FolderCompletion = 1L << 24,
    FolderErrors = 1L << 25,
    FolderScanProgress = 1L << 26,
    FolderPaused = 1L << 27,
    FolderResumed = 1L << 28,
    FolderWatchStateChanged = 1L << 29,
    
    // Network events
    ListenAddressesChanged = 1L << 30,
    LoginAttempt = 1L << 31,
    
    // Custom CreatioHelper events
    Warning = 1L << 32,
    Information = 1L << 33,
    Debug = 1L << 34,
    Shutdown = 1L << 48,
    FolderScanComplete = 1L << 49,
    NetworkLatencyHigh = 1L << 50,
    PerformanceAlert = 1L << 51,
    DownloadCompleted = 1L << 52,
    UploadCompleted = 1L << 53,
    DownloadFailed = 1L << 55,
    UploadFailed = 1L << 56,
    
    // Security events
    CertificateExpired = 1L << 35,
    CertificateRenewed = 1L << 36,
    SecurityAuditEvent = 1L << 37,
    
    // Sync-specific events  
    SyncStarted = 1L << 38,
    SyncCompleted = 1L << 39,
    SyncPaused = 1L << 40,
    SyncResumed = 1L << 41,
    SyncError = 1L << 42,
    
    // File versioning events
    FileVersioned = 1L << 43,
    FileRestored = 1L << 44,
    VersionCleanup = 1L << 45,
    
    // Bandwidth and priority events
    BandwidthThrottled = 1L << 46,
    PriorityChanged = 1L << 47,
    
    // Discovery events
    DiscoveryStarted = 1L << 48,
    DiscoveryCompleted = 1L << 49,
    DiscoveryFailed = 1L << 50,
    
    // Transport events
    QuicConnectionEstablished = 1L << 51,
    QuicConnectionClosed = 1L << 52,
    TcpConnectionEstablished = 1L << 53,
    TcpConnectionClosed = 1L << 54,
    
    // All events mask
    AllEvents = (1L << 57) - 1,
    
    // Common event masks for subscriptions
    DeviceEvents = DeviceDiscovered | DeviceConnected | DeviceDisconnected | 
                   DeviceRejected | PendingDevicesChanged | DevicePaused | DeviceResumed,
                   
    FolderEvents = FolderRejected | PendingFoldersChanged | FolderSummary | 
                   FolderCompletion | FolderErrors | FolderScanProgress | 
                   FolderPaused | FolderResumed | FolderWatchStateChanged,
                   
    TransferEvents = ItemStarted | ItemFinished | DownloadProgress | 
                     RemoteDownloadProgress | StateChanged,
                     
    ChangeEvents = LocalChangeDetected | RemoteChangeDetected | 
                   LocalIndexUpdated | RemoteIndexUpdated,
                   
    SystemEvents = Starting | StartupComplete | ConfigSaved | Failure,
    
    SecurityEvents = CertificateExpired | CertificateRenewed | SecurityAuditEvent,
    
    SyncEvents = SyncStarted | SyncCompleted | SyncPaused | SyncResumed | SyncError,
    
    VersioningEvents = FileVersioned | FileRestored | VersionCleanup,
    
    NetworkEvents = ListenAddressesChanged | LoginAttempt | 
                    QuicConnectionEstablished | QuicConnectionClosed |
                    TcpConnectionEstablished | TcpConnectionClosed,
                    
    DiscoveryEvents = DiscoveryStarted | DiscoveryCompleted | DiscoveryFailed,
    
    ErrorEvents = Failure | SyncError | FolderErrors | DiscoveryFailed | DownloadFailed | UploadFailed,

    TransferCompletedEvents = DownloadCompleted | UploadCompleted,
    TransferFailedEvents = DownloadFailed | UploadFailed,
    
    // Default mask for most subscriptions (excludes low-priority events)
    DefaultEventMask = AllEvents & ~(Debug | DownloadProgress | RemoteDownloadProgress | FolderScanProgress)
}

/// <summary>
/// Extension methods for SyncEventType
/// </summary>
public static class SyncEventTypeExtensions
{
    /// <summary>
    /// Check if event type contains any of the specified flags
    /// </summary>
    public static bool HasAnyFlag(this SyncEventType eventType, SyncEventType flags)
    {
        return (eventType & flags) != 0;
    }

    /// <summary>
    /// Check if event type contains all of the specified flags
    /// </summary>
    public static bool HasAllFlags(this SyncEventType eventType, SyncEventType flags)
    {
        return (eventType & flags) == flags;
    }

    /// <summary>
    /// Get human-readable name for the event type
    /// </summary>
    public static string GetDisplayName(this SyncEventType eventType)
    {
        return eventType switch
        {
            SyncEventType.Starting => "Starting",
            SyncEventType.StartupComplete => "Startup Complete",
            SyncEventType.DeviceDiscovered => "Device Discovered",
            SyncEventType.DeviceConnected => "Device Connected",
            SyncEventType.DeviceDisconnected => "Device Disconnected",
            SyncEventType.DeviceRejected => "Device Rejected",
            SyncEventType.PendingDevicesChanged => "Pending Devices Changed",
            SyncEventType.DevicePaused => "Device Paused",
            SyncEventType.DeviceResumed => "Device Resumed",
            SyncEventType.ClusterConfigReceived => "Cluster Config Received",
            SyncEventType.LocalChangeDetected => "Local Change Detected",
            SyncEventType.RemoteChangeDetected => "Remote Change Detected",
            SyncEventType.LocalIndexUpdated => "Local Index Updated",
            SyncEventType.RemoteIndexUpdated => "Remote Index Updated",
            SyncEventType.ItemStarted => "Item Started",
            SyncEventType.ItemFinished => "Item Finished",
            SyncEventType.StateChanged => "State Changed",
            SyncEventType.FolderRejected => "Folder Rejected",
            SyncEventType.PendingFoldersChanged => "Pending Folders Changed",
            SyncEventType.ConfigSaved => "Config Saved",
            SyncEventType.DownloadProgress => "Download Progress",
            SyncEventType.RemoteDownloadProgress => "Remote Download Progress",
            SyncEventType.FolderSummary => "Folder Summary",
            SyncEventType.FolderCompletion => "Folder Completion",
            SyncEventType.FolderErrors => "Folder Errors",
            SyncEventType.FolderScanProgress => "Folder Scan Progress",
            SyncEventType.FolderPaused => "Folder Paused",
            SyncEventType.FolderResumed => "Folder Resumed",
            SyncEventType.FolderWatchStateChanged => "Folder Watch State Changed",
            SyncEventType.ListenAddressesChanged => "Listen Addresses Changed",
            SyncEventType.LoginAttempt => "Login Attempt",
            SyncEventType.Failure => "Failure",
            SyncEventType.Warning => "Warning",
            SyncEventType.Information => "Information",
            SyncEventType.Debug => "Debug",
            SyncEventType.CertificateExpired => "Certificate Expired",
            SyncEventType.CertificateRenewed => "Certificate Renewed",
            SyncEventType.SecurityAuditEvent => "Security Audit Event",
            SyncEventType.SyncStarted => "Sync Started",
            SyncEventType.SyncCompleted => "Sync Completed",
            SyncEventType.SyncPaused => "Sync Paused",
            SyncEventType.SyncResumed => "Sync Resumed",
            SyncEventType.SyncError => "Sync Error",
            SyncEventType.FileVersioned => "File Versioned",
            SyncEventType.FileRestored => "File Restored",
            SyncEventType.VersionCleanup => "Version Cleanup",
            SyncEventType.BandwidthThrottled => "Bandwidth Throttled",
            SyncEventType.PriorityChanged => "Priority Changed",
            SyncEventType.DiscoveryStarted => "Discovery Started",
            SyncEventType.DiscoveryCompleted => "Discovery Completed",
            SyncEventType.DiscoveryFailed => "Discovery Failed",
            SyncEventType.QuicConnectionEstablished => "QUIC Connection Established",
            SyncEventType.QuicConnectionClosed => "QUIC Connection Closed",
            SyncEventType.TcpConnectionEstablished => "TCP Connection Established",
            SyncEventType.TcpConnectionClosed => "TCP Connection Closed",
            _ => eventType.ToString()
        };
    }

    /// <summary>
    /// Get event category for grouping
    /// </summary>
    public static string GetCategory(this SyncEventType eventType)
    {
        if (eventType.HasAnyFlag(SyncEventType.DeviceEvents)) return "Device";
        if (eventType.HasAnyFlag(SyncEventType.FolderEvents)) return "Folder";
        if (eventType.HasAnyFlag(SyncEventType.TransferEvents)) return "Transfer";
        if (eventType.HasAnyFlag(SyncEventType.ChangeEvents)) return "Changes";
        if (eventType.HasAnyFlag(SyncEventType.SystemEvents)) return "System";
        if (eventType.HasAnyFlag(SyncEventType.SecurityEvents)) return "Security";
        if (eventType.HasAnyFlag(SyncEventType.SyncEvents)) return "Sync";
        if (eventType.HasAnyFlag(SyncEventType.VersioningEvents)) return "Versioning";
        if (eventType.HasAnyFlag(SyncEventType.NetworkEvents)) return "Network";
        if (eventType.HasAnyFlag(SyncEventType.DiscoveryEvents)) return "Discovery";
        return "Other";
    }

    /// <summary>
    /// Get priority for the event type
    /// </summary>
    public static EventPriority GetPriority(this SyncEventType eventType)
    {
        return eventType switch
        {
            SyncEventType.Failure or SyncEventType.SyncError or SyncEventType.FolderErrors => EventPriority.High,
            SyncEventType.Warning or SyncEventType.DeviceRejected or SyncEventType.FolderRejected => EventPriority.High,
            SyncEventType.DeviceConnected or SyncEventType.DeviceDisconnected => EventPriority.Normal,
            SyncEventType.LocalChangeDetected or SyncEventType.RemoteChangeDetected => EventPriority.Normal,
            SyncEventType.ItemStarted or SyncEventType.ItemFinished => EventPriority.Normal,
            SyncEventType.Debug or SyncEventType.DownloadProgress or SyncEventType.FolderScanProgress => EventPriority.Low,
            _ => EventPriority.Normal
        };
    }

    /// <summary>
    /// Check if event type matches the specified mask
    /// </summary>
    public static bool MatchesMask(this SyncEventType eventType, SyncEventType mask)
    {
        return (eventType & mask) != 0;
    }
}