namespace CreatioHelper.Infrastructure.Services.Security;

/// <summary>
/// Service for comprehensive audit logging of security and administrative actions.
/// Based on Syncthing's audit logging patterns.
///
/// Provides detailed logging of:
/// - Authentication events (login, logout, failed attempts)
/// - Authorization events (access granted, denied)
/// - Configuration changes
/// - Device and folder management
/// - Security-related events
/// </summary>
public interface IAuditLogService
{
    /// <summary>
    /// Log an audit event.
    /// </summary>
    Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log an authentication event.
    /// </summary>
    Task LogAuthenticationAsync(AuthenticationAuditEvent authEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log an authorization event.
    /// </summary>
    Task LogAuthorizationAsync(AuthorizationAuditEvent authzEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a configuration change.
    /// </summary>
    Task LogConfigurationChangeAsync(ConfigurationChangeEvent configEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a device management event.
    /// </summary>
    Task LogDeviceEventAsync(DeviceAuditEvent deviceEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Log a folder management event.
    /// </summary>
    Task LogFolderEventAsync(FolderAuditEvent folderEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Query audit log entries.
    /// </summary>
    Task<AuditLogQueryResult> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get recent audit events.
    /// </summary>
    Task<IEnumerable<AuditLogEntry>> GetRecentEventsAsync(int count = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get audit events for a specific entity (device, folder, user).
    /// </summary>
    Task<IEnumerable<AuditLogEntry>> GetEntityEventsAsync(string entityType, string entityId, int count = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Export audit log to a file.
    /// </summary>
    Task<string> ExportAsync(AuditLogExportOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clean up old audit entries.
    /// </summary>
    Task<int> CleanupAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get audit log statistics.
    /// </summary>
    AuditLogStatistics GetStatistics();
}

/// <summary>
/// A single audit log entry.
/// </summary>
public class AuditLogEntry
{
    /// <summary>
    /// Unique entry ID.
    /// </summary>
    public long Id { get; set; }

    /// <summary>
    /// When the event occurred.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Event category.
    /// </summary>
    public AuditCategory Category { get; set; }

    /// <summary>
    /// Event action.
    /// </summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// Outcome of the action.
    /// </summary>
    public AuditOutcome Outcome { get; set; }

    /// <summary>
    /// Severity level.
    /// </summary>
    public AuditSeverity Severity { get; set; }

    /// <summary>
    /// Actor who performed the action (user, device, system).
    /// </summary>
    public AuditActor? Actor { get; set; }

    /// <summary>
    /// Target of the action.
    /// </summary>
    public AuditTarget? Target { get; set; }

    /// <summary>
    /// Human-readable description.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// IP address of the request origin.
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// User agent string.
    /// </summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// Request ID for correlation.
    /// </summary>
    public string? RequestId { get; set; }

    /// <summary>
    /// Session ID if applicable.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// Additional details as key-value pairs.
    /// </summary>
    public Dictionary<string, object?> Details { get; set; } = new();

    /// <summary>
    /// Old value (for changes).
    /// </summary>
    public object? OldValue { get; set; }

    /// <summary>
    /// New value (for changes).
    /// </summary>
    public object? NewValue { get; set; }

    /// <summary>
    /// Error message if outcome is failure.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Stack trace for errors.
    /// </summary>
    public string? StackTrace { get; set; }
}

/// <summary>
/// Actor who performed an audited action.
/// </summary>
public class AuditActor
{
    /// <summary>
    /// Actor type.
    /// </summary>
    public ActorType Type { get; set; }

    /// <summary>
    /// Actor identifier (user ID, device ID, etc.).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Actor name for display.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Role or permission level.
    /// </summary>
    public string? Role { get; set; }
}

/// <summary>
/// Target of an audited action.
/// </summary>
public class AuditTarget
{
    /// <summary>
    /// Target type.
    /// </summary>
    public TargetType Type { get; set; }

    /// <summary>
    /// Target identifier.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Target name for display.
    /// </summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// Actor types.
/// </summary>
public enum ActorType
{
    Unknown,
    User,
    Device,
    System,
    ApiToken,
    Service
}

/// <summary>
/// Target types.
/// </summary>
public enum TargetType
{
    Unknown,
    Device,
    Folder,
    File,
    Configuration,
    User,
    ApiToken,
    Certificate,
    Connection
}

/// <summary>
/// Audit categories.
/// </summary>
public enum AuditCategory
{
    Authentication,
    Authorization,
    Configuration,
    DeviceManagement,
    FolderManagement,
    FileOperation,
    Security,
    System,
    Network,
    Api
}

/// <summary>
/// Audit outcomes.
/// </summary>
public enum AuditOutcome
{
    Success,
    Failure,
    Denied,
    Partial,
    Pending
}

/// <summary>
/// Audit severity levels.
/// </summary>
public enum AuditSeverity
{
    Debug,
    Info,
    Warning,
    Error,
    Critical
}

/// <summary>
/// Authentication audit event.
/// </summary>
public class AuthenticationAuditEvent
{
    public string Action { get; set; } = string.Empty; // Login, Logout, Failed, TokenRefresh, etc.
    public AuditOutcome Outcome { get; set; }
    public string? UserId { get; set; }
    public string? Username { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? TokenId { get; set; }
    public string? FailureReason { get; set; }
    public Dictionary<string, object?> Details { get; set; } = new();
}

/// <summary>
/// Authorization audit event.
/// </summary>
public class AuthorizationAuditEvent
{
    public string Resource { get; set; } = string.Empty;
    public string Permission { get; set; } = string.Empty;
    public AuditOutcome Outcome { get; set; }
    public string? ActorId { get; set; }
    public string? ActorName { get; set; }
    public string? Reason { get; set; }
    public Dictionary<string, object?> Details { get; set; } = new();
}

/// <summary>
/// Configuration change audit event.
/// </summary>
public class ConfigurationChangeEvent
{
    public string ConfigSection { get; set; } = string.Empty;
    public string Setting { get; set; } = string.Empty;
    public object? OldValue { get; set; }
    public object? NewValue { get; set; }
    public string? ChangedBy { get; set; }
    public string? Reason { get; set; }
}

/// <summary>
/// Device management audit event.
/// </summary>
public class DeviceAuditEvent
{
    public string Action { get; set; } = string.Empty; // Added, Removed, Connected, Disconnected, etc.
    public string DeviceId { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string? IpAddress { get; set; }
    public AuditOutcome Outcome { get; set; }
    public Dictionary<string, object?> Details { get; set; } = new();
}

/// <summary>
/// Folder management audit event.
/// </summary>
public class FolderAuditEvent
{
    public string Action { get; set; } = string.Empty; // Created, Deleted, Modified, Shared, etc.
    public string FolderId { get; set; } = string.Empty;
    public string? FolderLabel { get; set; }
    public string? Path { get; set; }
    public AuditOutcome Outcome { get; set; }
    public Dictionary<string, object?> Details { get; set; } = new();
}

/// <summary>
/// Query parameters for audit log.
/// </summary>
public class AuditLogQuery
{
    /// <summary>
    /// Start date filter.
    /// </summary>
    public DateTime? StartDate { get; set; }

    /// <summary>
    /// End date filter.
    /// </summary>
    public DateTime? EndDate { get; set; }

    /// <summary>
    /// Category filter.
    /// </summary>
    public AuditCategory? Category { get; set; }

    /// <summary>
    /// Outcome filter.
    /// </summary>
    public AuditOutcome? Outcome { get; set; }

    /// <summary>
    /// Severity filter (minimum severity).
    /// </summary>
    public AuditSeverity? MinSeverity { get; set; }

    /// <summary>
    /// Actor ID filter.
    /// </summary>
    public string? ActorId { get; set; }

    /// <summary>
    /// Target ID filter.
    /// </summary>
    public string? TargetId { get; set; }

    /// <summary>
    /// Action filter.
    /// </summary>
    public string? Action { get; set; }

    /// <summary>
    /// IP address filter.
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>
    /// Text search in message and details.
    /// </summary>
    public string? SearchText { get; set; }

    /// <summary>
    /// Page number (1-based).
    /// </summary>
    public int Page { get; set; } = 1;

    /// <summary>
    /// Page size.
    /// </summary>
    public int PageSize { get; set; } = 50;

    /// <summary>
    /// Sort order (true = descending, newest first).
    /// </summary>
    public bool SortDescending { get; set; } = true;
}

/// <summary>
/// Result of an audit log query.
/// </summary>
public class AuditLogQueryResult
{
    /// <summary>
    /// Entries matching the query.
    /// </summary>
    public List<AuditLogEntry> Entries { get; set; } = new();

    /// <summary>
    /// Total count of matching entries.
    /// </summary>
    public int TotalCount { get; set; }

    /// <summary>
    /// Current page.
    /// </summary>
    public int Page { get; set; }

    /// <summary>
    /// Page size.
    /// </summary>
    public int PageSize { get; set; }

    /// <summary>
    /// Total pages.
    /// </summary>
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling((double)TotalCount / PageSize) : 0;

    /// <summary>
    /// Has more pages.
    /// </summary>
    public bool HasMore => Page < TotalPages;
}

/// <summary>
/// Options for exporting audit log.
/// </summary>
public class AuditLogExportOptions
{
    /// <summary>
    /// Export format.
    /// </summary>
    public AuditExportFormat Format { get; set; } = AuditExportFormat.Json;

    /// <summary>
    /// Query to filter exported entries.
    /// </summary>
    public AuditLogQuery? Query { get; set; }

    /// <summary>
    /// Maximum entries to export.
    /// </summary>
    public int? MaxEntries { get; set; }

    /// <summary>
    /// Output file path (null for auto-generated).
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Include sensitive details.
    /// </summary>
    public bool IncludeSensitive { get; set; }
}

/// <summary>
/// Export formats.
/// </summary>
public enum AuditExportFormat
{
    Json,
    Csv,
    Xml
}

/// <summary>
/// Audit log statistics.
/// </summary>
public class AuditLogStatistics
{
    /// <summary>
    /// Total entries.
    /// </summary>
    public long TotalEntries { get; set; }

    /// <summary>
    /// Entries in last 24 hours.
    /// </summary>
    public long EntriesLast24Hours { get; set; }

    /// <summary>
    /// Entries in last 7 days.
    /// </summary>
    public long EntriesLast7Days { get; set; }

    /// <summary>
    /// Entries by category.
    /// </summary>
    public Dictionary<AuditCategory, long> ByCategory { get; set; } = new();

    /// <summary>
    /// Entries by outcome.
    /// </summary>
    public Dictionary<AuditOutcome, long> ByOutcome { get; set; } = new();

    /// <summary>
    /// Failed authentication attempts in last hour.
    /// </summary>
    public int FailedAuthLast1Hour { get; set; }

    /// <summary>
    /// Storage size in bytes.
    /// </summary>
    public long StorageSizeBytes { get; set; }

    /// <summary>
    /// Oldest entry date.
    /// </summary>
    public DateTime? OldestEntry { get; set; }

    /// <summary>
    /// Newest entry date.
    /// </summary>
    public DateTime? NewestEntry { get; set; }
}
