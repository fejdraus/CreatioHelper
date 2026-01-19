using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Security;

/// <summary>
/// Implementation of comprehensive audit logging service.
/// Stores audit entries in JSON files with automatic rotation.
/// </summary>
public class AuditLogService : IAuditLogService, IDisposable
{
    private readonly ILogger<AuditLogService> _logger;
    private readonly string _logDirectory;
    private readonly AuditLogConfiguration _config;
    private readonly ConcurrentQueue<AuditLogEntry> _buffer = new();
    private readonly SemaphoreSlim _flushLock = new(1);
    private readonly Timer _flushTimer;
    private readonly object _statsLock = new();

    private long _nextId = 1;
    private long _totalEntries;
    private DateTime? _oldestEntry;
    private DateTime? _newestEntry;
    private readonly ConcurrentDictionary<AuditCategory, long> _entriesByCategory = new();
    private readonly ConcurrentDictionary<AuditOutcome, long> _entriesByOutcome = new();
    private bool _disposed;

    public AuditLogService(
        ILogger<AuditLogService> logger,
        string? logDirectory = null,
        AuditLogConfiguration? config = null)
    {
        _logger = logger;
        _logDirectory = logDirectory ?? GetDefaultLogDirectory();
        _config = config ?? new AuditLogConfiguration();

        // Ensure directory exists
        Directory.CreateDirectory(_logDirectory);

        // Load existing entries count
        _ = InitializeStatisticsAsync();

        // Start flush timer
        _flushTimer = new Timer(
            _ => _ = FlushBufferAsync(),
            null,
            TimeSpan.FromSeconds(_config.FlushIntervalSeconds),
            TimeSpan.FromSeconds(_config.FlushIntervalSeconds));

        _logger.LogInformation("Audit log service initialized at {Path}", _logDirectory);
    }

    /// <inheritdoc />
    public async Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
    {
        entry.Id = Interlocked.Increment(ref _nextId);
        entry.Timestamp = DateTime.UtcNow;

        // Update statistics
        UpdateStatistics(entry);

        // Add to buffer
        _buffer.Enqueue(entry);

        // Immediate flush for high severity
        if (entry.Severity >= AuditSeverity.Error || _buffer.Count >= _config.BufferSize)
        {
            await FlushBufferAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task LogAuthenticationAsync(AuthenticationAuditEvent authEvent, CancellationToken cancellationToken = default)
    {
        var entry = new AuditLogEntry
        {
            Category = AuditCategory.Authentication,
            Action = authEvent.Action,
            Outcome = authEvent.Outcome,
            Severity = authEvent.Outcome == AuditOutcome.Success ? AuditSeverity.Info : AuditSeverity.Warning,
            Actor = authEvent.UserId != null ? new AuditActor
            {
                Type = ActorType.User,
                Id = authEvent.UserId,
                Name = authEvent.Username ?? authEvent.UserId
            } : null,
            IpAddress = authEvent.IpAddress,
            UserAgent = authEvent.UserAgent,
            Message = BuildAuthenticationMessage(authEvent),
            Details = authEvent.Details,
            ErrorMessage = authEvent.FailureReason
        };

        if (!string.IsNullOrEmpty(authEvent.TokenId))
        {
            entry.Details["TokenId"] = authEvent.TokenId;
        }

        await LogAsync(entry, cancellationToken);
    }

    /// <inheritdoc />
    public async Task LogAuthorizationAsync(AuthorizationAuditEvent authzEvent, CancellationToken cancellationToken = default)
    {
        var entry = new AuditLogEntry
        {
            Category = AuditCategory.Authorization,
            Action = $"Access:{authzEvent.Permission}",
            Outcome = authzEvent.Outcome,
            Severity = authzEvent.Outcome == AuditOutcome.Denied ? AuditSeverity.Warning : AuditSeverity.Debug,
            Actor = authzEvent.ActorId != null ? new AuditActor
            {
                Type = ActorType.User,
                Id = authzEvent.ActorId,
                Name = authzEvent.ActorName ?? authzEvent.ActorId
            } : null,
            Target = new AuditTarget
            {
                Type = TargetType.Configuration,
                Id = authzEvent.Resource,
                Name = authzEvent.Resource
            },
            Message = $"Authorization {authzEvent.Outcome} for {authzEvent.Permission} on {authzEvent.Resource}",
            Details = authzEvent.Details,
            ErrorMessage = authzEvent.Reason
        };

        await LogAsync(entry, cancellationToken);
    }

    /// <inheritdoc />
    public async Task LogConfigurationChangeAsync(ConfigurationChangeEvent configEvent, CancellationToken cancellationToken = default)
    {
        var entry = new AuditLogEntry
        {
            Category = AuditCategory.Configuration,
            Action = "ConfigChange",
            Outcome = AuditOutcome.Success,
            Severity = AuditSeverity.Info,
            Actor = configEvent.ChangedBy != null ? new AuditActor
            {
                Type = ActorType.User,
                Id = configEvent.ChangedBy,
                Name = configEvent.ChangedBy
            } : null,
            Target = new AuditTarget
            {
                Type = TargetType.Configuration,
                Id = $"{configEvent.ConfigSection}.{configEvent.Setting}",
                Name = configEvent.Setting
            },
            Message = $"Configuration changed: {configEvent.ConfigSection}.{configEvent.Setting}",
            OldValue = configEvent.OldValue,
            NewValue = configEvent.NewValue,
            Details = new Dictionary<string, object?>
            {
                ["Section"] = configEvent.ConfigSection,
                ["Setting"] = configEvent.Setting,
                ["Reason"] = configEvent.Reason
            }
        };

        await LogAsync(entry, cancellationToken);
    }

    /// <inheritdoc />
    public async Task LogDeviceEventAsync(DeviceAuditEvent deviceEvent, CancellationToken cancellationToken = default)
    {
        var entry = new AuditLogEntry
        {
            Category = AuditCategory.DeviceManagement,
            Action = deviceEvent.Action,
            Outcome = deviceEvent.Outcome,
            Severity = deviceEvent.Outcome == AuditOutcome.Success ? AuditSeverity.Info : AuditSeverity.Warning,
            Target = new AuditTarget
            {
                Type = TargetType.Device,
                Id = deviceEvent.DeviceId,
                Name = deviceEvent.DeviceName ?? deviceEvent.DeviceId
            },
            IpAddress = deviceEvent.IpAddress,
            Message = $"Device {deviceEvent.Action}: {deviceEvent.DeviceName ?? deviceEvent.DeviceId}",
            Details = deviceEvent.Details
        };

        await LogAsync(entry, cancellationToken);
    }

    /// <inheritdoc />
    public async Task LogFolderEventAsync(FolderAuditEvent folderEvent, CancellationToken cancellationToken = default)
    {
        var entry = new AuditLogEntry
        {
            Category = AuditCategory.FolderManagement,
            Action = folderEvent.Action,
            Outcome = folderEvent.Outcome,
            Severity = folderEvent.Outcome == AuditOutcome.Success ? AuditSeverity.Info : AuditSeverity.Warning,
            Target = new AuditTarget
            {
                Type = TargetType.Folder,
                Id = folderEvent.FolderId,
                Name = folderEvent.FolderLabel ?? folderEvent.FolderId
            },
            Message = $"Folder {folderEvent.Action}: {folderEvent.FolderLabel ?? folderEvent.FolderId}",
            Details = folderEvent.Details
        };

        if (!string.IsNullOrEmpty(folderEvent.Path))
        {
            entry.Details["Path"] = folderEvent.Path;
        }

        await LogAsync(entry, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AuditLogQueryResult> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken = default)
    {
        var allEntries = await LoadAllEntriesAsync(cancellationToken);

        // Apply filters
        var filtered = allEntries.AsEnumerable();

        if (query.StartDate.HasValue)
            filtered = filtered.Where(e => e.Timestamp >= query.StartDate.Value);

        if (query.EndDate.HasValue)
            filtered = filtered.Where(e => e.Timestamp <= query.EndDate.Value);

        if (query.Category.HasValue)
            filtered = filtered.Where(e => e.Category == query.Category.Value);

        if (query.Outcome.HasValue)
            filtered = filtered.Where(e => e.Outcome == query.Outcome.Value);

        if (query.MinSeverity.HasValue)
            filtered = filtered.Where(e => e.Severity >= query.MinSeverity.Value);

        if (!string.IsNullOrEmpty(query.ActorId))
            filtered = filtered.Where(e => e.Actor?.Id == query.ActorId);

        if (!string.IsNullOrEmpty(query.TargetId))
            filtered = filtered.Where(e => e.Target?.Id == query.TargetId);

        if (!string.IsNullOrEmpty(query.Action))
            filtered = filtered.Where(e => e.Action.Contains(query.Action, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(query.IpAddress))
            filtered = filtered.Where(e => e.IpAddress == query.IpAddress);

        if (!string.IsNullOrEmpty(query.SearchText))
        {
            var searchLower = query.SearchText.ToLowerInvariant();
            filtered = filtered.Where(e =>
                e.Message.Contains(searchLower, StringComparison.OrdinalIgnoreCase) ||
                (e.ErrorMessage?.Contains(searchLower, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        // Sort
        filtered = query.SortDescending
            ? filtered.OrderByDescending(e => e.Timestamp)
            : filtered.OrderBy(e => e.Timestamp);

        var total = filtered.Count();

        // Paginate
        var entries = filtered
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToList();

        return new AuditLogQueryResult
        {
            Entries = entries,
            TotalCount = total,
            Page = query.Page,
            PageSize = query.PageSize
        };
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AuditLogEntry>> GetRecentEventsAsync(int count = 100, CancellationToken cancellationToken = default)
    {
        var result = await QueryAsync(new AuditLogQuery
        {
            Page = 1,
            PageSize = count,
            SortDescending = true
        }, cancellationToken);

        return result.Entries;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AuditLogEntry>> GetEntityEventsAsync(string entityType, string entityId, int count = 50, CancellationToken cancellationToken = default)
    {
        var result = await QueryAsync(new AuditLogQuery
        {
            TargetId = entityId,
            Page = 1,
            PageSize = count,
            SortDescending = true
        }, cancellationToken);

        return result.Entries;
    }

    /// <inheritdoc />
    public async Task<string> ExportAsync(AuditLogExportOptions options, CancellationToken cancellationToken = default)
    {
        var query = options.Query ?? new AuditLogQuery { PageSize = options.MaxEntries ?? 10000 };
        var result = await QueryAsync(query, cancellationToken);

        var outputPath = options.OutputPath ?? Path.Combine(
            _logDirectory,
            $"audit-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{options.Format.ToString().ToLower()}");

        switch (options.Format)
        {
            case AuditExportFormat.Json:
                await ExportJsonAsync(result.Entries, outputPath, options.IncludeSensitive, cancellationToken);
                break;

            case AuditExportFormat.Csv:
                await ExportCsvAsync(result.Entries, outputPath, options.IncludeSensitive, cancellationToken);
                break;

            case AuditExportFormat.Xml:
                await ExportXmlAsync(result.Entries, outputPath, options.IncludeSensitive, cancellationToken);
                break;
        }

        _logger.LogInformation("Exported {Count} audit entries to {Path}", result.Entries.Count, outputPath);
        return outputPath;
    }

    /// <inheritdoc />
    public async Task<int> CleanupAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var cleanedUp = 0;

        var files = Directory.GetFiles(_logDirectory, "audit-*.json");
        foreach (var file in files)
        {
            try
            {
                var fileName = Path.GetFileNameWithoutExtension(file);
                if (fileName.StartsWith("audit-") &&
                    DateTime.TryParseExact(fileName[6..], "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out var fileDate))
                {
                    if (fileDate < cutoff.Date)
                    {
                        File.Delete(file);
                        cleanedUp++;
                        _logger.LogDebug("Deleted old audit log file: {File}", file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to process audit file for cleanup: {File}", file);
            }
        }

        if (cleanedUp > 0)
        {
            _logger.LogInformation("Cleaned up {Count} old audit log files", cleanedUp);
        }

        return cleanedUp;
    }

    /// <inheritdoc />
    public AuditLogStatistics GetStatistics()
    {
        var now = DateTime.UtcNow;

        return new AuditLogStatistics
        {
            TotalEntries = Interlocked.Read(ref _totalEntries),
            OldestEntry = _oldestEntry,
            NewestEntry = _newestEntry,
            ByCategory = new Dictionary<AuditCategory, long>(_entriesByCategory),
            ByOutcome = new Dictionary<AuditOutcome, long>(_entriesByOutcome),
            StorageSizeBytes = GetStorageSize()
        };
    }

    private void UpdateStatistics(AuditLogEntry entry)
    {
        Interlocked.Increment(ref _totalEntries);

        lock (_statsLock)
        {
            if (!_oldestEntry.HasValue || entry.Timestamp < _oldestEntry.Value)
                _oldestEntry = entry.Timestamp;

            if (!_newestEntry.HasValue || entry.Timestamp > _newestEntry.Value)
                _newestEntry = entry.Timestamp;
        }

        _entriesByCategory.AddOrUpdate(entry.Category, 1, (_, count) => count + 1);
        _entriesByOutcome.AddOrUpdate(entry.Outcome, 1, (_, count) => count + 1);
    }

    private async Task FlushBufferAsync(CancellationToken cancellationToken = default)
    {
        if (_buffer.IsEmpty) return;

        await _flushLock.WaitAsync(cancellationToken);
        try
        {
            var entries = new List<AuditLogEntry>();
            while (_buffer.TryDequeue(out var entry))
            {
                entries.Add(entry);
            }

            if (entries.Count > 0)
            {
                var filePath = GetCurrentLogFilePath();
                var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });

                // Append to file (create if not exists)
                await File.AppendAllTextAsync(filePath, json + Environment.NewLine, cancellationToken);

                _logger.LogDebug("Flushed {Count} audit entries to {File}", entries.Count, filePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush audit log buffer");
        }
        finally
        {
            _flushLock.Release();
        }
    }

    private async Task<List<AuditLogEntry>> LoadAllEntriesAsync(CancellationToken cancellationToken)
    {
        var entries = new List<AuditLogEntry>();

        var files = Directory.GetFiles(_logDirectory, "audit-*.json")
            .OrderByDescending(f => f);

        foreach (var file in files)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file, cancellationToken);
                var fileEntries = JsonSerializer.Deserialize<List<AuditLogEntry>>(json);
                if (fileEntries != null)
                {
                    entries.AddRange(fileEntries);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load audit entries from {File}", file);
            }
        }

        return entries;
    }

    private async Task InitializeStatisticsAsync()
    {
        try
        {
            var entries = await LoadAllEntriesAsync(default);
            foreach (var entry in entries)
            {
                UpdateStatistics(entry);
            }
            _nextId = entries.Count > 0 ? entries.Max(e => e.Id) + 1 : 1;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to initialize audit log statistics");
        }
    }

    private string GetCurrentLogFilePath()
    {
        return Path.Combine(_logDirectory, $"audit-{DateTime.UtcNow:yyyyMMdd}.json");
    }

    private long GetStorageSize()
    {
        try
        {
            return Directory.GetFiles(_logDirectory, "audit-*.json")
                .Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return 0;
        }
    }

    private static string BuildAuthenticationMessage(AuthenticationAuditEvent authEvent)
    {
        var msg = authEvent.Action switch
        {
            "Login" => authEvent.Outcome == AuditOutcome.Success
                ? $"User {authEvent.Username} logged in successfully"
                : $"Failed login attempt for user {authEvent.Username}: {authEvent.FailureReason}",
            "Logout" => $"User {authEvent.Username} logged out",
            "TokenRefresh" => $"Token refreshed for user {authEvent.Username}",
            "TokenRevoked" => $"Token revoked for user {authEvent.Username}",
            _ => $"Authentication event: {authEvent.Action}"
        };

        if (!string.IsNullOrEmpty(authEvent.IpAddress))
        {
            msg += $" from {authEvent.IpAddress}";
        }

        return msg;
    }

    private async Task ExportJsonAsync(List<AuditLogEntry> entries, string path, bool includeSensitive, CancellationToken ct)
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(entries, options);
        await File.WriteAllTextAsync(path, json, ct);
    }

    private async Task ExportCsvAsync(List<AuditLogEntry> entries, string path, bool includeSensitive, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Id,Timestamp,Category,Action,Outcome,Severity,ActorId,ActorName,TargetId,TargetName,IpAddress,Message");

        foreach (var e in entries)
        {
            sb.AppendLine($"{e.Id},{e.Timestamp:O},{e.Category},{Escape(e.Action)},{e.Outcome},{e.Severity}," +
                $"{Escape(e.Actor?.Id)},{Escape(e.Actor?.Name)},{Escape(e.Target?.Id)},{Escape(e.Target?.Name)}," +
                $"{e.IpAddress},{Escape(e.Message)}");
        }

        await File.WriteAllTextAsync(path, sb.ToString(), ct);
    }

    private async Task ExportXmlAsync(List<AuditLogEntry> entries, string path, bool includeSensitive, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<AuditLog>");

        foreach (var e in entries)
        {
            sb.AppendLine($"  <Entry Id=\"{e.Id}\" Timestamp=\"{e.Timestamp:O}\">");
            sb.AppendLine($"    <Category>{e.Category}</Category>");
            sb.AppendLine($"    <Action>{System.Security.SecurityElement.Escape(e.Action)}</Action>");
            sb.AppendLine($"    <Outcome>{e.Outcome}</Outcome>");
            sb.AppendLine($"    <Message>{System.Security.SecurityElement.Escape(e.Message)}</Message>");
            sb.AppendLine("  </Entry>");
        }

        sb.AppendLine("</AuditLog>");
        await File.WriteAllTextAsync(path, sb.ToString(), ct);
    }

    private static string? Escape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Contains(',') || value.Contains('"')
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;
    }

    private static string GetDefaultLogDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "CreatioHelper", "audit");
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _flushTimer.Dispose();
            _ = FlushBufferAsync();
            _flushLock.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Configuration for audit log service.
/// </summary>
public class AuditLogConfiguration
{
    /// <summary>
    /// Buffer size before automatic flush.
    /// </summary>
    public int BufferSize { get; set; } = 100;

    /// <summary>
    /// Flush interval in seconds.
    /// </summary>
    public int FlushIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// Maximum age for audit entries (for cleanup).
    /// </summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>
    /// Enable file rotation.
    /// </summary>
    public bool EnableRotation { get; set; } = true;

    /// <summary>
    /// Maximum file size in MB before rotation.
    /// </summary>
    public int MaxFileSizeMb { get; set; } = 100;
}
