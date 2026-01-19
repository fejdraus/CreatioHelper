using System.Text.Json;

namespace CreatioHelper.Infrastructure.Services.Sync.Diagnostics;

/// <summary>
/// Structured JSON logging service based on Syncthing's lib/logger/logger.go
///
/// Provides structured logging with:
/// - JSON output format for machine parsing
/// - Log levels with severity
/// - Contextual fields support
/// - Log rotation and retention
/// - Buffered writes for performance
/// - Category/component filtering
/// </summary>
public interface IStructuredLogger : IAsyncDisposable
{
    /// <summary>
    /// Log a debug message.
    /// </summary>
    void Debug(string message, params object?[] args);

    /// <summary>
    /// Log a debug message with structured data.
    /// </summary>
    void Debug(string message, Dictionary<string, object?> fields);

    /// <summary>
    /// Log an info message.
    /// </summary>
    void Info(string message, params object?[] args);

    /// <summary>
    /// Log an info message with structured data.
    /// </summary>
    void Info(string message, Dictionary<string, object?> fields);

    /// <summary>
    /// Log a warning message.
    /// </summary>
    void Warn(string message, params object?[] args);

    /// <summary>
    /// Log a warning message with structured data.
    /// </summary>
    void Warn(string message, Dictionary<string, object?> fields);

    /// <summary>
    /// Log an error message.
    /// </summary>
    void Error(string message, params object?[] args);

    /// <summary>
    /// Log an error message with structured data.
    /// </summary>
    void Error(string message, Dictionary<string, object?> fields);

    /// <summary>
    /// Log an error with exception.
    /// </summary>
    void Error(string message, Exception exception, Dictionary<string, object?>? fields = null);

    /// <summary>
    /// Log a fatal message.
    /// </summary>
    void Fatal(string message, params object?[] args);

    /// <summary>
    /// Log a fatal message with structured data.
    /// </summary>
    void Fatal(string message, Dictionary<string, object?> fields);

    /// <summary>
    /// Create a child logger with additional context fields.
    /// </summary>
    IStructuredLogger WithFields(Dictionary<string, object?> fields);

    /// <summary>
    /// Create a child logger with a component name.
    /// </summary>
    IStructuredLogger WithComponent(string component);

    /// <summary>
    /// Check if debug logging is enabled.
    /// </summary>
    bool IsDebugEnabled { get; }

    /// <summary>
    /// Set minimum log level.
    /// </summary>
    void SetLevel(StructuredLogLevel level);

    /// <summary>
    /// Enable/disable a specific facility for debug logging.
    /// </summary>
    void SetFacilityDebug(string facility, bool enabled);

    /// <summary>
    /// Get logging statistics.
    /// </summary>
    StructuredLogStatistics GetStatistics();

    /// <summary>
    /// Flush buffered log entries.
    /// </summary>
    Task FlushAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Log severity levels.
/// </summary>
public enum StructuredLogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
    Fatal = 4
}

/// <summary>
/// Represents a structured log entry.
/// </summary>
public class StructuredLogEntry
{
    /// <summary>
    /// Log timestamp in ISO 8601 format.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Log level/severity.
    /// </summary>
    public StructuredLogLevel Level { get; set; }

    /// <summary>
    /// Level as string for JSON output.
    /// </summary>
    public string LevelString => Level.ToString().ToLowerInvariant();

    /// <summary>
    /// Log message.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Component/module that generated the log.
    /// </summary>
    public string? Component { get; set; }

    /// <summary>
    /// Additional structured fields.
    /// </summary>
    public Dictionary<string, object?> Fields { get; set; } = new();

    /// <summary>
    /// Exception information if present.
    /// </summary>
    public ExceptionInfo? Exception { get; set; }

    /// <summary>
    /// Unique log entry ID.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
}

/// <summary>
/// Exception information for structured logging.
/// </summary>
public class ExceptionInfo
{
    public string Type { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? StackTrace { get; set; }
    public ExceptionInfo? InnerException { get; set; }
}

/// <summary>
/// Configuration for structured logging.
/// </summary>
public class StructuredLogConfiguration
{
    /// <summary>
    /// Minimum log level to output.
    /// </summary>
    public StructuredLogLevel MinLevel { get; set; } = StructuredLogLevel.Info;

    /// <summary>
    /// Output file path (null for console only).
    /// </summary>
    public string? OutputPath { get; set; }

    /// <summary>
    /// Whether to output to console.
    /// </summary>
    public bool OutputToConsole { get; set; } = true;

    /// <summary>
    /// Whether to use JSON format (false = plain text).
    /// </summary>
    public bool JsonFormat { get; set; } = true;

    /// <summary>
    /// Whether to include timestamps.
    /// </summary>
    public bool IncludeTimestamp { get; set; } = true;

    /// <summary>
    /// Buffer size before flushing (entries).
    /// </summary>
    public int BufferSize { get; set; } = 100;

    /// <summary>
    /// Flush interval in milliseconds.
    /// </summary>
    public int FlushIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Maximum log file size in bytes before rotation.
    /// </summary>
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024; // 10 MB

    /// <summary>
    /// Number of rotated log files to keep.
    /// </summary>
    public int MaxRotatedFiles { get; set; } = 5;

    /// <summary>
    /// Debug facilities enabled (Syncthing-compatible).
    /// </summary>
    public HashSet<string> DebugFacilities { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// JSON serializer options.
    /// </summary>
    public JsonSerializerOptions JsonOptions { get; set; } = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

/// <summary>
/// Statistics for structured logging.
/// </summary>
public class StructuredLogStatistics
{
    public long TotalEntries { get; set; }
    public long DebugEntries { get; set; }
    public long InfoEntries { get; set; }
    public long WarnEntries { get; set; }
    public long ErrorEntries { get; set; }
    public long FatalEntries { get; set; }
    public long DroppedEntries { get; set; }
    public long BytesWritten { get; set; }
    public int CurrentBufferSize { get; set; }
    public DateTime? LastFlush { get; set; }
    public int FileRotations { get; set; }
}
