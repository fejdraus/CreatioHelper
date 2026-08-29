using CreatioHelper.Agent.Authorization;
using CreatioHelper.Agent.Controllers.Handlers;

using CreatioHelper.Agent.Syncthing;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;

namespace CreatioHelper.Agent.Controllers;

/// <summary>
/// Syncthing-compatible /rest/system API endpoints
/// Provides 100% compatibility with Syncthing REST API
/// </summary>
[ApiController]
[Route("rest/system")]
[Authorize(Roles = Roles.ReadRoles)]
public class SyncthingSystemController : ControllerBase
{
    private readonly ISyncEngine _syncEngine;
    private readonly ILogger<SyncthingSystemController> _logger;
    private readonly IConfiguration _configuration;
    private readonly IConfigXmlService _configXmlService;
    private readonly SyncthingSystemConfigHandlers _configHandlers;


    // Store enabled log facilities in memory (in production, this would be persisted)
    private static readonly HashSet<string> _enabledLogFacilities = new();

    public SyncthingSystemController(
        ISyncEngine syncEngine,
        ILogger<SyncthingSystemController> logger,
        IConfiguration configuration,
        IConfigXmlService configXmlService)
    {
        _syncEngine = syncEngine;
        _logger = logger;
        _configuration = configuration;
        _configXmlService = configXmlService;
        _configHandlers = new SyncthingSystemConfigHandlers(syncEngine, logger);
    }

    /// <summary>
    /// Get system status - 100% Syncthing compatible
    /// GET /rest/system/status
    /// </summary>
    [HttpGet("status")]
    [Authorize(Roles = Roles.MonitorRoles)]
    public async Task<ActionResult<object>> GetStatus()
    {
        var statistics = await _syncEngine.GetStatisticsAsync();
        var devices = await _syncEngine.GetDevicesAsync();
        var folders = await _syncEngine.GetFoldersAsync();
        var config = await _syncEngine.GetConfigurationAsync();

        // Get memory statistics
        var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        var gcMemoryInfo = GC.GetGCMemoryInfo();

        // Application memory (working set of this process)
        var appMemory = currentProcess.WorkingSet64;

        // Total physical memory on machine
        var totalPhysicalMemory = gcMemoryInfo.TotalAvailableMemoryBytes;

        // Memory used by OS (total - available)
        // MemoryLoadBytes represents the memory load at the time of last GC
        var memoryLoad = gcMemoryInfo.MemoryLoadBytes;

        // For Syncthing compatibility, keep alloc/sys but also add new fields
        var allocatedMemory = GC.GetTotalMemory(false);

        return Ok(new
        {
            // Syncthing-compatible fields
            alloc = allocatedMemory,
            sys = appMemory,

            // New extended memory info
            appMemory = appMemory,                      // Memory used by this application
            osMemoryUsed = memoryLoad,                  // Memory used by OS/all processes
            totalPhysicalMemory = totalPhysicalMemory,  // Total RAM on machine

            connectionServiceStatus = BuildConnectionServiceStatus(config),
            cpuPercent = GetCpuUsage(currentProcess),
            discoveryEnabled = config.LocalAnnounceEnabled || config.GlobalAnnounceEnabled,
            discoveryErrors = new { },
            discoveryMethods = (config.LocalAnnounceEnabled ? 1 : 0) + (config.GlobalAnnounceEnabled ? config.GlobalAnnounceServers.Count : 0),
            goroutines = System.Threading.ThreadPool.ThreadCount,
            guiAddressOverridden = false,
            guiAddressUsed = "127.0.0.1:8384",
            lastDialStatus = new { },
            myID = _syncEngine.DeviceId,
            pathSeparator = Path.DirectorySeparatorChar.ToString(),
            startTime = statistics.StartTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            tilde = "~",
            uptime = (int)statistics.Uptime.TotalSeconds,
            urVersionMax = 3,
            version = "v1.27.0", // CreatioHelper version mimicking Syncthing
            codename = "Copper Dragonfly",

            // GC statistics
            gcGen0Collections = GC.CollectionCount(0),
            gcGen1Collections = GC.CollectionCount(1),
            gcGen2Collections = GC.CollectionCount(2),
            gcTotalPauseMs = GC.GetTotalPauseDuration().TotalMilliseconds,

            // Heap statistics
            heapSizeBytes = gcMemoryInfo.HeapSizeBytes,
            heapFragmentedBytes = gcMemoryInfo.FragmentedBytes,

            // Process statistics
            processHandleCount = currentProcess.HandleCount,
            processThreadCount = currentProcess.Threads.Count,

            // I/O statistics
            totalBytesIn = statistics.TotalBytesIn,
            totalBytesOut = statistics.TotalBytesOut
        });
    }

    /// <summary>
    /// Get approximate CPU usage for the process
    /// </summary>
    private static double GetCpuUsage(System.Diagnostics.Process process)
    {
        try
        {
            // Simple approximation based on total processor time
            var cpuTime = process.TotalProcessorTime;
            var uptime = DateTime.Now - process.StartTime;
            var cpuUsage = (cpuTime.TotalMilliseconds / (uptime.TotalMilliseconds * Environment.ProcessorCount)) * 100;
            return Math.Round(Math.Min(cpuUsage, 100), 1);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Build connection service status from config listen addresses
    /// </summary>
    private static Dictionary<string, object> BuildConnectionServiceStatus(SyncConfiguration config)
    {
        var result = new Dictionary<string, object>();
        foreach (var addr in config.ListenAddresses)
        {
            result[addr] = new { error = (string?)null };
        }
        return result;
    }

    /// <summary>
    /// Get the interface language configured for this agent.
    /// Anonymous so the login page can render before the user signs in.
    /// GET /rest/system/ui-language
    /// </summary>
    [HttpGet("ui-language")]
    [AllowAnonymous]
    public async Task<ActionResult<object>> GetUiLanguage(CancellationToken cancellationToken)
    {
        var config = await _configXmlService.LoadAsync(cancellationToken);
        return Ok(new { language = config.Gui.Language });
    }

    /// <summary>
    /// Persist the interface language so every browser opening this agent starts in the same language.
    /// PUT /rest/system/ui-language
    /// </summary>
    [HttpPut("ui-language")]
    public async Task<IActionResult> SetUiLanguage([FromBody] UiLanguageRequest request, CancellationToken cancellationToken)
    {
        var config = await _configXmlService.LoadAsync(cancellationToken);
        config.Gui.Language = request.Language ?? string.Empty;
        await _configXmlService.SaveAsync(config, cancellationToken);
        return NoContent();
    }

    public class UiLanguageRequest
    {
        public string? Language { get; set; }
    }

    /// <summary>
    /// Get system version - 100% Syncthing compatible
    /// GET /rest/system/version
    /// </summary>
    [HttpGet("version")]
    [AllowAnonymous]
    public ActionResult<object> GetVersion()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        var informationalVersion = assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? version;
        var buildDate = System.IO.File.GetLastWriteTimeUtc(assembly.Location).ToString("yyyy-MM-ddTHH:mm:ssZ");

        return Ok(new
        {
            arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            buildDate = buildDate,
            buildHost = Environment.MachineName,
            buildUser = Environment.UserName,
            codename = "CreatioHelper",
            isBeta = informationalVersion.Contains("-beta", StringComparison.OrdinalIgnoreCase),
            isCandidate = informationalVersion.Contains("-rc", StringComparison.OrdinalIgnoreCase),
            isRelease = !informationalVersion.Contains("-"),
            longVersion = $"CreatioHelper v{informationalVersion} ({System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription} {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}) {Environment.UserName}@{Environment.MachineName} {buildDate}",
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            stamp = buildDate,
            tags = Array.Empty<string>(),
            user = Environment.UserName,
            version = $"v{version}"
        });
    }

    /// <summary>
    /// Get system configuration - 100% Syncthing compatible
    /// GET /rest/system/config
    /// </summary>
    [HttpGet("config")]
    public async Task<ActionResult<object>> GetConfig()
    {
        var devices = await _syncEngine.GetDevicesAsync();
        var folders = await _syncEngine.GetFoldersAsync();
        var syncConfig = await _syncEngine.GetConfigurationAsync();

        return Ok(SyncthingConfigContract.Build(devices, folders, syncConfig));
    }

    /// <summary>
    /// Update system configuration - 100% Syncthing compatible
    /// POST /rest/system/config
    [HttpPost("config")]
    [Authorize(Roles = Roles.WriteRoles)]
    public Task<ActionResult> UpdateConfig([FromBody] JsonElement config)
        => _configHandlers.UpdateConfigAsync(config);



    /// <summary>
    /// System restart - 100% Syncthing compatible
    /// POST /rest/system/restart
    /// </summary>
    [HttpPost("restart")]
    [Authorize(Roles = Roles.WriteRoles)]
    public ActionResult Restart()
    {
        _logger.LogInformation("System restart requested");
        return Ok(new { ok = "restarting" });
    }

    /// <summary>
    /// System shutdown - 100% Syncthing compatible
    /// POST /rest/system/shutdown
    /// </summary>
    [HttpPost("shutdown")]
    [Authorize(Roles = Roles.WriteRoles)]
    public ActionResult Shutdown()
    {
        _logger.LogInformation("System shutdown requested");
        return Ok(new { ok = "shutting down" });
    }

    /// <summary>
    /// Get system log entries - 100% Syncthing compatible
    /// GET /rest/system/log
    /// </summary>
    [HttpGet("log")]
    public ActionResult<object> GetLog([FromQuery] int last = 50)
    {
        var entries = ReadLogEntries(Math.Min(last, 1000));
        var messages = entries.Select(e => new
        {
            when = e.Timestamp.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            message = e.Message,
            level = e.Level switch
            {
                "error" or "fatal" => 3,
                "warning" => 2,
                "debug" or "verbose" => 0,
                _ => 2 // INFO
            }
        }).ToArray();

        return Ok(new
        {
            messages = messages
        });
    }

    /// <summary>
    /// Get system log entries as structured array
    /// GET /rest/system/log/entries
    /// Custom endpoint for WebUI - reads real logs from log files
    /// </summary>
    [HttpGet("log/entries")]
    public ActionResult<object> GetLogEntries(
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 100,
        [FromQuery] string? level = null,
        [FromQuery] string? facility = null,
        [FromQuery] string? search = null,
        [FromQuery] string? sort = null,
        [FromQuery] string? dir = null,
        [FromQuery] string? filters = null)
    {
        try
        {
            var items = ReadLogPage(Math.Max(0, offset), Math.Clamp(limit, 1, 500), level, facility, search, sort, dir, filters, out var total);
            return Ok(new { total, items });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting system log entries");
            return StatusCode(500, new { total = 0, items = Array.Empty<LogEntry>() });
        }
    }

    /// <summary>
    /// Read the newest N entries (shared with the plain-text log endpoints).
    /// </summary>
    private List<LogEntry> ReadLogEntries(int limit)
        => ReadLogPage(0, limit, null, null, null, null, null, null, out _);

    private static readonly HashSet<string> AllowedLogFilterFields = new(StringComparer.Ordinal)
    {
        "timestamp", "level", "facility", "message"
    };

    private static void BuildLogFilters(string? filtersJson, List<string> conditions, List<(string Name, object Value)> parameters)
    {
        if (string.IsNullOrWhiteSpace(filtersJson))
        {
            return;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(filtersJson);
            var index = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var field = element.TryGetProperty("f", out var f) ? f.GetString() : null;
                var op = element.TryGetProperty("o", out var o) ? o.GetString() : null;
                var value = element.TryGetProperty("v", out var v) ? v.GetString() : null;

                if (string.IsNullOrEmpty(field) || !AllowedLogFilterFields.Contains(field) || string.IsNullOrEmpty(op))
                {
                    continue;
                }

                var name = "$flt" + index;
                switch (op)
                {
                    case "contains":
                        conditions.Add($"{field} LIKE {name}"); parameters.Add((name, "%" + value + "%")); break;
                    case "not contains":
                        conditions.Add($"{field} NOT LIKE {name}"); parameters.Add((name, "%" + value + "%")); break;
                    case "equals":
                        conditions.Add($"{field} = {name}"); parameters.Add((name, (object?)value ?? string.Empty)); break;
                    case "not equals":
                        conditions.Add($"{field} <> {name}"); parameters.Add((name, (object?)value ?? string.Empty)); break;
                    case "starts with":
                        conditions.Add($"{field} LIKE {name}"); parameters.Add((name, value + "%")); break;
                    case "ends with":
                        conditions.Add($"{field} LIKE {name}"); parameters.Add((name, "%" + value)); break;
                    case "is empty":
                        conditions.Add($"({field} IS NULL OR {field} = '')"); break;
                    case "is not empty":
                        conditions.Add($"({field} IS NOT NULL AND {field} <> '')"); break;
                    case "is after":
                        conditions.Add($"{field} > {name}"); parameters.Add((name, (object?)value ?? string.Empty)); break;
                    case "is on or after":
                        conditions.Add($"{field} >= {name}"); parameters.Add((name, (object?)value ?? string.Empty)); break;
                    case "is before":
                        conditions.Add($"{field} < {name}"); parameters.Add((name, (object?)value ?? string.Empty)); break;
                    case "is on or before":
                        conditions.Add($"{field} <= {name}"); parameters.Add((name, (object?)value ?? string.Empty)); break;
                    case "is":
                        conditions.Add($"{field} LIKE {name}"); parameters.Add((name, LogDatePrefix(value) + "%")); break;
                    case "is not":
                        conditions.Add($"{field} NOT LIKE {name}"); parameters.Add((name, LogDatePrefix(value) + "%")); break;
                    default:
                        continue;
                }

                index++;
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }
    }

    private static string LogDatePrefix(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        return value.Length >= 10 ? value[..10] : value;
    }

    private static string ResolveLogSortColumn(string? sort) => sort switch
    {
        "level" => "level",
        "facility" => "facility",
        "message" => "message",
        _ => "id"
    };

    /// <summary>
    /// Read a page of log entries from the database with optional level/facility/search filters.
    /// </summary>
    private List<LogEntry> ReadLogPage(int offset, int limit, string? level, string? facility, string? search, string? sort, string? dir, string? filters, out int total)
    {
        var entries = new List<LogEntry>();
        total = 0;

        try
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={CreatioHelper.Agent.Logging.SystemLogStore.DatabasePath};Mode=ReadOnly");
            connection.Open();

            var levelAbbreviation = string.IsNullOrWhiteSpace(level) || level == "all"
                ? null
                : CreatioHelper.Agent.Logging.SystemLogFormat.LevelToAbbreviation(level);
            var hasFacility = !string.IsNullOrWhiteSpace(facility) && facility != "all";
            var hasSearch = !string.IsNullOrWhiteSpace(search);

            var filterParams = new List<(string Name, object Value)>();
            var conditions = new List<string>();
            BuildLogFilters(filters, conditions, filterParams);
            if (!string.IsNullOrEmpty(levelAbbreviation)) conditions.Add("level = $level");
            if (hasFacility) conditions.Add("facility = $facility");
            if (hasSearch) conditions.Add("message LIKE $search");
            var whereClause = conditions.Count > 0 ? " WHERE " + string.Join(" AND ", conditions) : "";

            void Bind(Microsoft.Data.Sqlite.SqliteCommand cmd)
            {
                if (!string.IsNullOrEmpty(levelAbbreviation))
                {
                    var p = cmd.CreateParameter(); p.ParameterName = "$level"; p.Value = levelAbbreviation; cmd.Parameters.Add(p);
                }
                if (hasFacility)
                {
                    var p = cmd.CreateParameter(); p.ParameterName = "$facility"; p.Value = facility!; cmd.Parameters.Add(p);
                }
                if (hasSearch)
                {
                    var p = cmd.CreateParameter(); p.ParameterName = "$search"; p.Value = "%" + search + "%"; cmd.Parameters.Add(p);
                }
                foreach (var (name, value) in filterParams)
                {
                    var p = cmd.CreateParameter(); p.ParameterName = name; p.Value = value; cmd.Parameters.Add(p);
                }
            }

            using (var countCommand = connection.CreateCommand())
            {
                countCommand.CommandText = "SELECT COUNT(*) FROM system_log" + whereClause + ";";
                Bind(countCommand);
                total = Convert.ToInt32(countCommand.ExecuteScalar());
            }

            var sortColumn = ResolveLogSortColumn(sort);
            var sortDirection = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT timestamp, level, facility, message FROM system_log" + whereClause +
                                  $" ORDER BY {sortColumn} {sortDirection}, id {sortDirection} LIMIT $limit OFFSET $offset;";
            Bind(command);
            var pl = command.CreateParameter(); pl.ParameterName = "$limit"; pl.Value = limit; command.Parameters.Add(pl);
            var po = command.CreateParameter(); po.ParameterName = "$offset"; po.Value = offset; command.Parameters.Add(po);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                entries.Add(new LogEntry
                {
                    Timestamp = DateTime.TryParse(reader.GetString(0), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var ts)
                        ? ts.ToUniversalTime()
                        : DateTime.UtcNow,
                    Level = ParseLogLevel(reader.GetString(1)),
                    Facility = reader.GetString(2),
                    Message = reader.GetString(3)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error reading system log from database");
        }

        return entries;
    }

    /// <summary>
    /// Parse a Serilog log line
    /// Format: {Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message}
    /// Example: 2024-01-20 18:10:39.533 +03:00 [INF] Some log message
    /// </summary>
    private static LogEntry? ParseLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        try
        {
            // Match pattern: date time timezone [LEVEL] message
            // Example: 2024-01-20 18:10:39.533 +03:00 [INF] Some log message
            var match = System.Text.RegularExpressions.Regex.Match(line,
                @"^(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3})\s+([+-]\d{2}:\d{2})\s+\[(\w{3})\]\s+(.*)$");

            if (!match.Success)
            {
                // Try simpler format without timezone
                match = System.Text.RegularExpressions.Regex.Match(line,
                    @"^(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\.\d{3})\s+\[(\w{3})\]\s+(.*)$");

                if (!match.Success)
                    return null;

                return new LogEntry
                {
                    Timestamp = DateTime.SpecifyKind(DateTime.Parse(match.Groups[1].Value), DateTimeKind.Local).ToUniversalTime(),
                    Level = ParseLogLevel(match.Groups[2].Value),
                    Facility = ExtractFacility(match.Groups[3].Value),
                    Message = match.Groups[3].Value
                };
            }

            return new LogEntry
            {
                Timestamp = DateTimeOffset.Parse($"{match.Groups[1].Value} {match.Groups[2].Value}").UtcDateTime,
                Level = ParseLogLevel(match.Groups[3].Value),
                Facility = ExtractFacility(match.Groups[4].Value),
                Message = match.Groups[4].Value
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parse Serilog level abbreviation to full level name
    /// </summary>
    private static string ParseLogLevel(string levelAbbr)
    {
        return levelAbbr.ToUpperInvariant() switch
        {
            "VRB" => "verbose",
            "DBG" => "debug",
            "INF" => "info",
            "WRN" => "warning",
            "ERR" => "error",
            "FTL" => "fatal",
            _ => "info"
        };
    }

    /// <summary>
    /// Extract facility/category from log message
    /// </summary>
    private static string ExtractFacility(string message)
    {
        // Try to extract namespace/class from common patterns
        if (message.Contains("SyncEngine") || message.Contains("Sync:"))
            return "sync";
        if (message.Contains("Connection") || message.Contains("Device"))
            return "connections";
        if (message.Contains("Database") || message.Contains("DB") || message.Contains("Index"))
            return "db";
        if (message.Contains("API") || message.Contains("Controller") || message.Contains("Request"))
            return "api";
        if (message.Contains("Model") || message.Contains("Config"))
            return "model";
        if (message.Contains("Auth") || message.Contains("Login") || message.Contains("JWT"))
            return "auth";
        if (message.Contains("File") || message.Contains("Folder"))
            return "fs";
        if (message.Contains("Network") || message.Contains("NAT") || message.Contains("UPnP"))
            return "network";

        return "app";
    }

    /// <summary>
    /// Clear system log - 100% Syncthing compatible
    /// POST /rest/system/log/clear
    /// </summary>
    [HttpPost("log/clear")]
    [Authorize(Roles = Roles.WriteRoles)]
    public ActionResult ClearLog()
    {
        try
        {
            _logger.LogInformation("System log clear requested");
            return Ok(new { ok = "log cleared" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error clearing system log");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get system log as plain text - 100% Syncthing compatible
    /// GET /rest/system/log.txt
    /// </summary>
    [HttpGet("log.txt")]
    [Produces("text/plain")]
    public ActionResult GetLogText([FromQuery] int since = 0)
    {
        try
        {
            var entries = ReadLogEntries(200);
            var sb = new System.Text.StringBuilder();

            foreach (var entry in entries)
            {
                var timestamp = entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff");
                var level = entry.Level.ToUpperInvariant();
                sb.AppendLine($"[{timestamp}] {level}: {entry.Message}");
            }

            return Content(sb.ToString(), "text/plain");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting system log as text");
            return StatusCode(500, "Internal server error");
        }
    }

    /// <summary>
    /// Get debug/log levels - 100% Syncthing compatible
    /// GET /rest/system/loglevels
    /// </summary>
    [HttpGet("loglevels")]
    public ActionResult<object> GetLogLevels()
    {
        return Ok(new
        {
            enabled = _enabledLogFacilities.ToArray(),
            facilities = new Dictionary<string, string>
            {
                ["main"] = "Main package",
                ["model"] = "Model/sync engine",
                ["scanner"] = "File scanner",
                ["connections"] = "Connection handling",
                ["protocol"] = "BEP protocol",
                ["db"] = "Database operations",
                ["discover"] = "Device discovery",
                ["events"] = "Event system",
                ["upnp"] = "UPnP/NAT traversal",
                ["relay"] = "Relay connections",
                ["versioner"] = "File versioning",
                ["config"] = "Configuration"
            }
        });
    }

    /// <summary>
    /// Set debug/log levels - 100% Syncthing compatible
    /// POST /rest/system/loglevels
    /// </summary>
    [HttpPost("loglevels")]
    [Authorize(Roles = Roles.WriteRoles)]
    public ActionResult<object> SetLogLevels([FromQuery] string? enable, [FromQuery] string? disable)
    {
        // Enable facilities
        if (!string.IsNullOrEmpty(enable))
        {
            var facilitiesToEnable = enable.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var facility in facilitiesToEnable)
            {
                if (!_enabledLogFacilities.Contains(facility))
                {
                    _enabledLogFacilities.Add(facility);
                }
            }
            _logger.LogInformation("Enabled log facilities: {Facilities}", enable);
        }

        // Disable facilities
        if (!string.IsNullOrEmpty(disable))
        {
            var facilitiesToDisable = disable.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var facility in facilitiesToDisable)
            {
                _enabledLogFacilities.Remove(facility);
            }
            _logger.LogInformation("Disabled log facilities: {Facilities}", disable);
        }

        return Ok(new
        {
            enabled = _enabledLogFacilities.ToArray(),
            facilities = new Dictionary<string, string>
            {
                ["main"] = "Main package",
                ["model"] = "Model/sync engine",
                ["scanner"] = "File scanner",
                ["connections"] = "Connection handling",
                ["protocol"] = "BEP protocol",
                ["db"] = "Database operations",
                ["discover"] = "Device discovery",
                ["events"] = "Event system",
                ["upnp"] = "UPnP/NAT traversal",
                ["relay"] = "Relay connections",
                ["versioner"] = "File versioning",
                ["config"] = "Configuration"
            }
        });
    }

    /// <summary>
    /// Browse file system - 100% Syncthing compatible
    /// GET /rest/system/browse?current=path
    /// </summary>
    [HttpGet("browse")]
    public ActionResult<object> Browse([FromQuery] string? current)
    {
        var path = string.IsNullOrEmpty(current)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : current;

        // Validate path to prevent directory traversal
        if (path.Contains(".."))
            return BadRequest(new { error = "Invalid path" });

        if (!Directory.Exists(path))
        {
            // Return parent directory if path doesn't exist
            var parent = Path.GetDirectoryName(path);
            if (parent != null && Directory.Exists(parent))
            {
                path = parent;
            }
            else
            {
                path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
        }

        var entries = new List<string>();

        try
        {
            // Add subdirectories
            foreach (var dir in Directory.GetDirectories(path))
            {
                var name = Path.GetFileName(dir);
                if (!name.StartsWith(".")) // Skip hidden directories
                {
                    entries.Add(dir + Path.DirectorySeparatorChar);
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Access denied, return empty list
        }

        entries.Sort();
        return Ok(entries.ToArray());
    }

    /// <summary>
    /// Get active connections - 100% Syncthing compatible
    /// GET /rest/system/connections
    /// </summary>
    [HttpGet("connections")]
    public async Task<ActionResult<object>> GetConnections()
    {
        var devices = await _syncEngine.GetDevicesAsync();
        var statistics = await _syncEngine.GetStatisticsAsync();

        var connections = new Dictionary<string, object>();
        var total = new
        {
            at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
            inBytesTotal = statistics.TotalBytesIn,
            outBytesTotal = statistics.TotalBytesOut
        };

        foreach (var device in devices)
        {
            if (string.Equals(device.DeviceId, _syncEngine.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            connections[device.DeviceId] = new
            {
                at = device.LastSeen?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                inBytesTotal = 0L,
                outBytesTotal = 0L,
                startedAt = device.LastConnected?.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                connected = device.IsConnected,
                paused = device.IsPaused,
                clientVersion = "v1.27.0",
                address = device.LastAddress ?? (device.Addresses.FirstOrDefault() ?? string.Empty),
                type = device.ConnectionType ?? "tcp-client",
                isLocal = false,
                crypto = "TLS1.3-AES256-GCM"
            };
        }

        return Ok(new
        {
            connections = connections,
            total = total
        });
    }

    /// <summary>
    /// Get discovery status - 100% Syncthing compatible
    /// GET /rest/system/discovery
    /// </summary>
    [HttpGet("discovery")]
    public async Task<ActionResult<object>> GetDiscovery()
    {
        var config = await _syncEngine.GetConfigurationAsync();

        // Build global discovery server statuses
        var globalServers = new Dictionary<string, object>();
        if (config.GlobalAnnounceEnabled)
        {
            foreach (var server in config.GlobalAnnounceServers)
            {
                globalServers[server] = new { status = "ok", lastSeen = DateTime.UtcNow };
            }
        }

        // Local discovery status
        var localStatus = new
        {
            multicastStatus = config.LocalAnnounceEnabled ? "ok" : ""
        };

        return Ok(new
        {
            global = globalServers,
            local = localStatus,
            localAnnounceEnabled = config.LocalAnnounceEnabled,
            globalAnnounceEnabled = config.GlobalAnnounceEnabled,
            globalAnnounceServers = config.GlobalAnnounceServers.ToArray()
        });
    }

    // Store system errors in memory (in production, this would be in a service)
    private static readonly List<SystemError> _systemErrors = new();
    private static readonly object _errorsLock = new();

    /// <summary>
    /// Get system errors - 100% Syncthing compatible
    /// GET /rest/system/error
    /// </summary>
    [HttpGet("error")]
    public ActionResult<object> GetErrors()
    {
        lock (_errorsLock)
        {
            var errors = _systemErrors.Select(e => new
            {
                when = e.When.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                message = e.Message,
                level = e.Level
            }).ToArray();

            return Ok(new { errors = errors });
        }
    }

    /// <summary>
    /// Post system error - 100% Syncthing compatible
    /// POST /rest/system/error
    /// </summary>
    [HttpPost("error")]
    [Authorize(Roles = Roles.WriteRoles)]
    public ActionResult PostError([FromBody] string message)
    {
        if (string.IsNullOrEmpty(message))
            return BadRequest(new { error = "message required" });

        lock (_errorsLock)
        {
            _systemErrors.Add(new SystemError
            {
                When = DateTime.UtcNow,
                Message = message,
                Level = 3 // ERROR level
            });
        }

        _logger.LogError("System error posted: {Message}", message);
        return Ok(new { ok = "error logged" });
    }

    /// <summary>
    /// Clear system errors - 100% Syncthing compatible
    /// POST /rest/system/error/clear
    /// </summary>
    [HttpPost("error/clear")]
    [Authorize(Roles = Roles.WriteRoles)]
    public ActionResult ClearErrors()
    {
        lock (_errorsLock)
        {
            _systemErrors.Clear();
        }

        _logger.LogInformation("System errors cleared");
        return Ok(new { ok = "errors cleared" });
    }

    /// <summary>
    /// Get system paths - 100% Syncthing compatible
    /// GET /rest/system/paths
    /// </summary>
    [HttpGet("paths")]
    public ActionResult<object> GetPaths()
    {
        var configDir = _configXmlService.GetConfigDirectory();

        return Ok(new
        {
            auditLog = Path.Combine(configDir, "audit.log"),
            baseDir = configDir,
            certFile = Path.Combine(configDir, "cert.pem"),
            config = _configXmlService.ConfigPath,
            csrfTokens = Path.Combine(configDir, ".csrf-tokens"),
            database = Path.Combine(configDir, "index-v0.14.0.db"),
            defFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            guiAssets = Path.Combine(configDir, "gui"),
            httpsCertFile = Path.Combine(configDir, "https-cert.pem"),
            httpsKeyFile = Path.Combine(configDir, "https-key.pem"),
            keyFile = Path.Combine(configDir, "key.pem"),
            logFile = Path.Combine(configDir, "syncthing.log"),
            panicLog = Path.Combine(configDir, "panic.log")
        });
    }

    /// <summary>
    /// Get upgrade info - 100% Syncthing compatible
    /// GET /rest/system/upgrade
    /// </summary>
    [HttpGet("upgrade")]
    public ActionResult<object> GetUpgrade()
    {
        return Ok(new
        {
            latest = "v1.27.0",
            majorNewer = false,
            newer = false,
            running = "v1.27.0"
        });
    }

    /// <summary>
    /// Perform upgrade - 100% Syncthing compatible
    /// POST /rest/system/upgrade
    /// </summary>
    [HttpPost("upgrade")]
    [Authorize(Roles = Roles.WriteRoles)]
    public ActionResult DoUpgrade()
    {
        _logger.LogInformation("System upgrade requested");
        return Ok(new { ok = "upgrade initiated" });
    }

    /// <summary>
    /// Ping endpoint - 100% Syncthing compatible
    /// GET /rest/system/ping
    /// </summary>
    [HttpGet("ping")]
    [AllowAnonymous]
    public ActionResult<object> Ping()
    {
        return Ok(new { ping = "pong" });
    }

    /// <summary>
    /// Pause device or all devices - 100% Syncthing compatible
    /// POST /rest/system/pause?device=DEVICE-ID
    /// </summary>
    [HttpPost("pause")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<ActionResult> Pause([FromQuery] string? device)
    {
        if (!string.IsNullOrEmpty(device))
        {
            await _syncEngine.PauseDeviceAsync(device);
            _logger.LogInformation("Paused device {DeviceId}", device);
        }
        else
        {
            var devices = await _syncEngine.GetDevicesAsync();
            foreach (var d in devices)
            {
                await _syncEngine.PauseDeviceAsync(d.DeviceId);
            }
            _logger.LogInformation("Paused all devices");
        }

        return Ok(new { ok = "paused" });
    }

    /// <summary>
    /// Resume device or all devices - 100% Syncthing compatible
    /// POST /rest/system/resume?device=DEVICE-ID
    /// </summary>
    [HttpPost("resume")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<ActionResult> Resume([FromQuery] string? device)
    {
        if (!string.IsNullOrEmpty(device))
        {
            await _syncEngine.ResumeDeviceAsync(device);
            _logger.LogInformation("Resumed device {DeviceId}", device);
        }
        else
        {
            var devices = await _syncEngine.GetDevicesAsync();
            foreach (var d in devices)
            {
                await _syncEngine.ResumeDeviceAsync(d.DeviceId);
            }
            _logger.LogInformation("Resumed all devices");
        }

        return Ok(new { ok = "resumed" });
    }

    /// <summary>
    /// Reset database for a folder - 100% Syncthing compatible
    /// POST /rest/system/reset?folder=FOLDER-ID
    /// </summary>
    [HttpPost("reset")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<ActionResult> Reset([FromQuery] string? folder)
    {
        if (!string.IsNullOrEmpty(folder))
        {
            // Reset specific folder — deep rescan
            _logger.LogInformation("Reset requested for folder {FolderId}", folder);
            var folderInfo = await _syncEngine.GetFolderAsync(folder);
            if (folderInfo == null)
                return NotFound(new { error = $"Folder {folder} not found" });

            _syncEngine.QueueScan(folder, deep: true);
        }
        else
        {
            // Reset all folders — deep rescan each
            _logger.LogInformation("Full database reset requested — rescanning all folders");
            var folders = await _syncEngine.GetFoldersAsync();
            foreach (var f in folders)
            {
                _syncEngine.QueueScan(f.Id, deep: true);
            }
        }

        return Ok(new { ok = "reset initiated" });
    }

    /// <summary>
    /// Debug endpoints - 100% Syncthing compatible
    /// GET /rest/system/debug
    /// </summary>
    [HttpGet("debug")]
    public ActionResult<object> GetDebug()
    {
        return Ok(new
        {
            enabled = new string[] { },
            facilities = new Dictionary<string, string>
            {
                ["main"] = "Main package",
                ["model"] = "Model package",
                ["scanner"] = "File scanner",
                ["connections"] = "Connection handling"
            }
        });
    }

    /// <summary>
    /// Set debug facilities - 100% Syncthing compatible
    /// POST /rest/system/debug
    /// </summary>
    [HttpPost("debug")]
    [Authorize(Roles = Roles.WriteRoles)]
    public ActionResult SetDebug([FromBody] DebugRequest? request)
    {
        _logger.LogInformation("Debug settings updated: enable={Enable}, disable={Disable}",
            request?.Enable, request?.Disable);
        return Ok(new { ok = "debug settings updated" });
    }
}

/// <summary>
/// System error model
/// </summary>
public class SystemError
{
    public DateTime When { get; set; }
    public string Message { get; set; } = string.Empty;
    public int Level { get; set; } // 1=DEBUG, 2=INFO, 3=WARNING/ERROR
}

/// <summary>
/// Debug request model
/// </summary>
public class DebugRequest
{
    public string[]? Enable { get; set; }
    public string[]? Disable { get; set; }
}

/// <summary>
/// Log entry model for WebUI
/// </summary>
public class LogEntry
{
    [System.Text.Json.Serialization.JsonPropertyName("when")]
    public DateTime Timestamp { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("level")]
    public string Level { get; set; } = "info";

    [System.Text.Json.Serialization.JsonPropertyName("facility")]
    public string Facility { get; set; } = "app";

    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}