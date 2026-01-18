using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Diagnostics;

/// <summary>
/// Debug categories for categorized logging (based on Syncthing logger.go)
/// </summary>
public static class DebugCategories
{
    public const string Model = "model";
    public const string Scanner = "scanner";
    public const string Protocol = "protocol";
    public const string Database = "database";
    public const string Events = "events";
    public const string Connections = "connections";
    public const string Discovery = "discovery";
    public const string Relay = "relay";
    public const string Versioning = "versioning";
    public const string Encryption = "encryption";
    public const string Upnp = "upnp";
    public const string Config = "config";
    public const string Api = "api";
    public const string Metrics = "metrics";

    public static readonly string[] All = new[]
    {
        Model, Scanner, Protocol, Database, Events, Connections,
        Discovery, Relay, Versioning, Encryption, Upnp, Config, Api, Metrics
    };
}

/// <summary>
/// Debug entry with timestamp and category
/// </summary>
public record DebugEntry
{
    public DateTime Timestamp { get; init; }
    public string Category { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public LogLevel Level { get; init; }
    public string? Exception { get; init; }
    public Dictionary<string, object>? Properties { get; init; }
}

/// <summary>
/// Provides categorized debugging facilities (based on Syncthing logger.go)
/// </summary>
public interface IDebugFacilities
{
    /// <summary>
    /// Enable debug logging for a category
    /// </summary>
    void EnableCategory(string category);

    /// <summary>
    /// Disable debug logging for a category
    /// </summary>
    void DisableCategory(string category);

    /// <summary>
    /// Check if a category is enabled
    /// </summary>
    bool IsCategoryEnabled(string category);

    /// <summary>
    /// Get all enabled categories
    /// </summary>
    IReadOnlySet<string> GetEnabledCategories();

    /// <summary>
    /// Log a debug message for a category
    /// </summary>
    void Debug(string category, string message, params object[] args);

    /// <summary>
    /// Get recent debug entries
    /// </summary>
    IReadOnlyList<DebugEntry> GetRecentEntries(int count = 100, string? category = null);

    /// <summary>
    /// Clear debug entries
    /// </summary>
    void ClearEntries();

    /// <summary>
    /// Parse debug categories from environment or configuration
    /// </summary>
    void ParseDebugString(string debugString);
}

/// <summary>
/// Implementation of debug facilities (based on Syncthing logger.go)
/// </summary>
public class DebugFacilities : IDebugFacilities
{
    private readonly ILogger<DebugFacilities> _logger;
    private readonly HashSet<string> _enabledCategories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<DebugEntry> _entries = new();
    private readonly object _lock = new();
    private const int MaxEntries = 10000;

    public DebugFacilities(ILogger<DebugFacilities> logger)
    {
        _logger = logger;

        // Check environment variable for default categories
        var envDebug = Environment.GetEnvironmentVariable("STTRACE");
        if (!string.IsNullOrEmpty(envDebug))
        {
            ParseDebugString(envDebug);
        }
    }

    public void EnableCategory(string category)
    {
        lock (_lock)
        {
            if (_enabledCategories.Add(category))
            {
                _logger.LogDebug("Enabled debug category: {Category}", category);
            }
        }
    }

    public void DisableCategory(string category)
    {
        lock (_lock)
        {
            if (_enabledCategories.Remove(category))
            {
                _logger.LogDebug("Disabled debug category: {Category}", category);
            }
        }
    }

    public bool IsCategoryEnabled(string category)
    {
        lock (_lock)
        {
            return _enabledCategories.Contains(category) ||
                   _enabledCategories.Contains("all");
        }
    }

    public IReadOnlySet<string> GetEnabledCategories()
    {
        lock (_lock)
        {
            return new HashSet<string>(_enabledCategories);
        }
    }

    public void Debug(string category, string message, params object[] args)
    {
        if (!IsCategoryEnabled(category))
            return;

        var formattedMessage = args.Length > 0 ? string.Format(message, args) : message;

        var entry = new DebugEntry
        {
            Timestamp = DateTime.UtcNow,
            Category = category,
            Message = formattedMessage,
            Level = LogLevel.Debug
        };

        AddEntry(entry);
        _logger.LogDebug("[{Category}] {Message}", category, formattedMessage);
    }

    public IReadOnlyList<DebugEntry> GetRecentEntries(int count = 100, string? category = null)
    {
        var entries = _entries.ToList();

        if (!string.IsNullOrEmpty(category))
        {
            entries = entries.Where(e =>
                e.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return entries.TakeLast(count).ToList();
    }

    public void ClearEntries()
    {
        while (_entries.TryDequeue(out _)) { }
        _logger.LogDebug("Cleared debug entries");
    }

    public void ParseDebugString(string debugString)
    {
        if (string.IsNullOrEmpty(debugString))
            return;

        var categories = debugString.Split(',', StringSplitOptions.RemoveEmptyEntries);

        lock (_lock)
        {
            foreach (var category in categories)
            {
                var trimmed = category.Trim().ToLowerInvariant();
                if (trimmed == "all")
                {
                    foreach (var cat in DebugCategories.All)
                    {
                        _enabledCategories.Add(cat);
                    }
                    _enabledCategories.Add("all");
                }
                else
                {
                    _enabledCategories.Add(trimmed);
                }
            }
        }

        _logger.LogInformation("Parsed debug categories: {Categories}", debugString);
    }

    private void AddEntry(DebugEntry entry)
    {
        _entries.Enqueue(entry);

        // Trim old entries
        while (_entries.Count > MaxEntries)
        {
            _entries.TryDequeue(out _);
        }
    }
}
