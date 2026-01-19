using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Diagnostics;

/// <summary>
/// Implementation of structured JSON logging service.
/// Based on Syncthing's lib/logger/logger.go
/// </summary>
public class StructuredLogger : IStructuredLogger
{
    private readonly ILogger<StructuredLogger>? _microsoftLogger;
    private readonly StructuredLogConfiguration _config;
    private readonly ConcurrentQueue<StructuredLogEntry> _buffer = new();
    private readonly Dictionary<string, object?> _contextFields = new();
    private readonly string? _component;
    private readonly object _statsLock = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly Timer? _flushTimer;
    private readonly HashSet<string> _debugFacilities;
    private readonly StructuredLogger? _rootLogger; // Reference to root for stat sharing

    private StreamWriter? _fileWriter;
    private long _currentFileSize;
    private StructuredLogLevel _minLevel;
    private bool _disposed;

    // Statistics (only used on root logger)
    private long _totalEntries;
    private long _debugEntries;
    private long _infoEntries;
    private long _warnEntries;
    private long _errorEntries;
    private long _fatalEntries;
    private long _droppedEntries;
    private long _bytesWritten;
    private DateTime? _lastFlush;
    private int _fileRotations;

    public StructuredLogger(
        ILogger<StructuredLogger>? microsoftLogger = null,
        StructuredLogConfiguration? config = null)
    {
        _microsoftLogger = microsoftLogger;
        _config = config ?? new StructuredLogConfiguration();
        _minLevel = _config.MinLevel;
        _debugFacilities = new HashSet<string>(_config.DebugFacilities, StringComparer.OrdinalIgnoreCase);

        InitializeFileWriter();

        // Start flush timer
        if (_config.FlushIntervalMs > 0)
        {
            _flushTimer = new Timer(
                _ => _ = FlushAsync(),
                null,
                _config.FlushIntervalMs,
                _config.FlushIntervalMs);
        }
    }

    // Private constructor for child loggers
    private StructuredLogger(
        StructuredLogger parent,
        Dictionary<string, object?> contextFields,
        string? component)
    {
        _microsoftLogger = parent._microsoftLogger;
        _config = parent._config;
        _buffer = parent._buffer;
        _contextFields = new Dictionary<string, object?>(parent._contextFields);
        foreach (var field in contextFields)
        {
            _contextFields[field.Key] = field.Value;
        }
        _component = component ?? parent._component;
        _debugFacilities = parent._debugFacilities;
        _minLevel = parent._minLevel;
        _fileWriter = parent._fileWriter;
        _flushLock = parent._flushLock;

        // Share statistics with root logger
        _rootLogger = parent._rootLogger ?? parent;
        // Child loggers don't have their own timer or file writer
    }

    public bool IsDebugEnabled => _minLevel <= StructuredLogLevel.Debug;

    public void Debug(string message, params object?[] args)
    {
        Log(StructuredLogLevel.Debug, message, args);
    }

    public void Debug(string message, Dictionary<string, object?> fields)
    {
        Log(StructuredLogLevel.Debug, message, fields);
    }

    public void Info(string message, params object?[] args)
    {
        Log(StructuredLogLevel.Info, message, args);
    }

    public void Info(string message, Dictionary<string, object?> fields)
    {
        Log(StructuredLogLevel.Info, message, fields);
    }

    public void Warn(string message, params object?[] args)
    {
        Log(StructuredLogLevel.Warn, message, args);
    }

    public void Warn(string message, Dictionary<string, object?> fields)
    {
        Log(StructuredLogLevel.Warn, message, fields);
    }

    public void Error(string message, params object?[] args)
    {
        Log(StructuredLogLevel.Error, message, args);
    }

    public void Error(string message, Dictionary<string, object?> fields)
    {
        Log(StructuredLogLevel.Error, message, fields);
    }

    public void Error(string message, Exception exception, Dictionary<string, object?>? fields = null)
    {
        var entry = CreateEntry(StructuredLogLevel.Error, message, fields ?? new Dictionary<string, object?>());
        entry.Exception = CreateExceptionInfo(exception);
        EnqueueEntry(entry);
    }

    public void Fatal(string message, params object?[] args)
    {
        Log(StructuredLogLevel.Fatal, message, args);
    }

    public void Fatal(string message, Dictionary<string, object?> fields)
    {
        Log(StructuredLogLevel.Fatal, message, fields);
    }

    public IStructuredLogger WithFields(Dictionary<string, object?> fields)
    {
        return new StructuredLogger(this, fields, _component);
    }

    public IStructuredLogger WithComponent(string component)
    {
        return new StructuredLogger(this, new Dictionary<string, object?>(), component);
    }

    public void SetLevel(StructuredLogLevel level)
    {
        _minLevel = level;
    }

    public void SetFacilityDebug(string facility, bool enabled)
    {
        if (enabled)
        {
            _debugFacilities.Add(facility);
        }
        else
        {
            _debugFacilities.Remove(facility);
        }
    }

    public StructuredLogStatistics GetStatistics()
    {
        // Delegate to root logger if this is a child
        if (_rootLogger != null)
        {
            return _rootLogger.GetStatistics();
        }

        lock (_statsLock)
        {
            return new StructuredLogStatistics
            {
                TotalEntries = Interlocked.Read(ref _totalEntries),
                DebugEntries = Interlocked.Read(ref _debugEntries),
                InfoEntries = Interlocked.Read(ref _infoEntries),
                WarnEntries = Interlocked.Read(ref _warnEntries),
                ErrorEntries = Interlocked.Read(ref _errorEntries),
                FatalEntries = Interlocked.Read(ref _fatalEntries),
                DroppedEntries = Interlocked.Read(ref _droppedEntries),
                BytesWritten = Interlocked.Read(ref _bytesWritten),
                CurrentBufferSize = _buffer.Count,
                LastFlush = _lastFlush,
                FileRotations = _fileRotations
            };
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;

        await _flushLock.WaitAsync(cancellationToken);
        try
        {
            var entries = new List<StructuredLogEntry>();
            while (_buffer.TryDequeue(out var entry))
            {
                entries.Add(entry);
            }

            if (entries.Count == 0) return;

            foreach (var entry in entries)
            {
                await WriteEntryAsync(entry, cancellationToken);
            }

            if (_fileWriter != null)
            {
                await _fileWriter.FlushAsync(cancellationToken);
            }

            _lastFlush = DateTime.UtcNow;
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _flushTimer?.Dispose();

        // Final flush
        await FlushAsync();

        if (_fileWriter != null)
        {
            await _fileWriter.DisposeAsync();
        }

        _flushLock.Dispose();
    }

    private void Log(StructuredLogLevel level, string message, object?[] args)
    {
        if (level < _minLevel) return;

        // Format message with args
        var formattedMessage = args.Length > 0 ? string.Format(message, args) : message;
        var entry = CreateEntry(level, formattedMessage, new Dictionary<string, object?>());
        EnqueueEntry(entry);
    }

    private void Log(StructuredLogLevel level, string message, Dictionary<string, object?> fields)
    {
        if (level < _minLevel) return;

        var entry = CreateEntry(level, message, fields);
        EnqueueEntry(entry);
    }

    private StructuredLogEntry CreateEntry(StructuredLogLevel level, string message, Dictionary<string, object?> fields)
    {
        var entry = new StructuredLogEntry
        {
            Level = level,
            Message = message,
            Component = _component,
            Fields = new Dictionary<string, object?>(_contextFields)
        };

        foreach (var field in fields)
        {
            entry.Fields[field.Key] = field.Value;
        }

        return entry;
    }

    private void EnqueueEntry(StructuredLogEntry entry)
    {
        // Delegate stats to root logger if this is a child
        var statsTarget = _rootLogger ?? this;

        // Update statistics on the stats target (root or self)
        Interlocked.Increment(ref statsTarget._totalEntries);
        switch (entry.Level)
        {
            case StructuredLogLevel.Debug:
                Interlocked.Increment(ref statsTarget._debugEntries);
                break;
            case StructuredLogLevel.Info:
                Interlocked.Increment(ref statsTarget._infoEntries);
                break;
            case StructuredLogLevel.Warn:
                Interlocked.Increment(ref statsTarget._warnEntries);
                break;
            case StructuredLogLevel.Error:
                Interlocked.Increment(ref statsTarget._errorEntries);
                break;
            case StructuredLogLevel.Fatal:
                Interlocked.Increment(ref statsTarget._fatalEntries);
                break;
        }

        // Check buffer limit
        if (_buffer.Count >= _config.BufferSize * 2)
        {
            Interlocked.Increment(ref statsTarget._droppedEntries);
            return;
        }

        _buffer.Enqueue(entry);

        // Auto-flush on high severity or buffer full
        if (entry.Level >= StructuredLogLevel.Error || _buffer.Count >= _config.BufferSize)
        {
            _ = FlushAsync();
        }

        // Also log to Microsoft.Extensions.Logging
        LogToMicrosoftLogger(entry);
    }

    private void LogToMicrosoftLogger(StructuredLogEntry entry)
    {
        if (_microsoftLogger == null) return;

        var logLevel = entry.Level switch
        {
            StructuredLogLevel.Debug => LogLevel.Debug,
            StructuredLogLevel.Info => LogLevel.Information,
            StructuredLogLevel.Warn => LogLevel.Warning,
            StructuredLogLevel.Error => LogLevel.Error,
            StructuredLogLevel.Fatal => LogLevel.Critical,
            _ => LogLevel.Information
        };

        using var scope = entry.Fields.Count > 0
            ? _microsoftLogger.BeginScope(entry.Fields)
            : null;

        if (entry.Exception != null)
        {
            _microsoftLogger.Log(logLevel, "[{Component}] {Message} - Exception: {ExceptionType}: {ExceptionMessage}",
                entry.Component ?? "default", entry.Message, entry.Exception.Type, entry.Exception.Message);
        }
        else
        {
            _microsoftLogger.Log(logLevel, "[{Component}] {Message}",
                entry.Component ?? "default", entry.Message);
        }
    }

    private async Task WriteEntryAsync(StructuredLogEntry entry, CancellationToken cancellationToken)
    {
        string output;

        if (_config.JsonFormat)
        {
            output = SerializeToJson(entry);
        }
        else
        {
            output = FormatPlainText(entry);
        }

        // Write to console
        if (_config.OutputToConsole)
        {
            var color = entry.Level switch
            {
                StructuredLogLevel.Debug => ConsoleColor.Gray,
                StructuredLogLevel.Info => ConsoleColor.White,
                StructuredLogLevel.Warn => ConsoleColor.Yellow,
                StructuredLogLevel.Error => ConsoleColor.Red,
                StructuredLogLevel.Fatal => ConsoleColor.DarkRed,
                _ => ConsoleColor.White
            };

            var originalColor = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(output);
            Console.ForegroundColor = originalColor;
        }

        // Write to file
        if (_fileWriter != null)
        {
            await _fileWriter.WriteLineAsync(output.AsMemory(), cancellationToken);
            var bytesWritten = Encoding.UTF8.GetByteCount(output) + Environment.NewLine.Length;
            Interlocked.Add(ref _bytesWritten, bytesWritten);
            _currentFileSize += bytesWritten;

            // Check for rotation
            if (_currentFileSize >= _config.MaxFileSizeBytes)
            {
                await RotateLogFileAsync(cancellationToken);
            }
        }
    }

    private string SerializeToJson(StructuredLogEntry entry)
    {
        var jsonObj = new Dictionary<string, object?>
        {
            ["time"] = entry.Timestamp.ToString("O"),
            ["level"] = entry.LevelString,
            ["msg"] = entry.Message,
            ["id"] = entry.Id
        };

        if (!string.IsNullOrEmpty(entry.Component))
        {
            jsonObj["component"] = entry.Component;
        }

        foreach (var field in entry.Fields)
        {
            jsonObj[field.Key] = field.Value;
        }

        if (entry.Exception != null)
        {
            jsonObj["error"] = new Dictionary<string, object?>
            {
                ["type"] = entry.Exception.Type,
                ["message"] = entry.Exception.Message,
                ["stack"] = entry.Exception.StackTrace
            };
        }

        return JsonSerializer.Serialize(jsonObj, _config.JsonOptions);
    }

    private string FormatPlainText(StructuredLogEntry entry)
    {
        var sb = new StringBuilder();

        if (_config.IncludeTimestamp)
        {
            sb.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"));
            sb.Append(' ');
        }

        sb.Append('[');
        sb.Append(entry.LevelString.ToUpperInvariant().PadRight(5));
        sb.Append(']');

        if (!string.IsNullOrEmpty(entry.Component))
        {
            sb.Append(" [");
            sb.Append(entry.Component);
            sb.Append(']');
        }

        sb.Append(' ');
        sb.Append(entry.Message);

        if (entry.Fields.Count > 0)
        {
            sb.Append(" {");
            sb.Append(string.Join(", ", entry.Fields.Select(f => $"{f.Key}={f.Value}")));
            sb.Append('}');
        }

        if (entry.Exception != null)
        {
            sb.AppendLine();
            sb.Append("  Exception: ");
            sb.Append(entry.Exception.Type);
            sb.Append(": ");
            sb.Append(entry.Exception.Message);
            if (!string.IsNullOrEmpty(entry.Exception.StackTrace))
            {
                sb.AppendLine();
                sb.Append("  ");
                sb.Append(entry.Exception.StackTrace.Replace("\n", "\n  "));
            }
        }

        return sb.ToString();
    }

    private static ExceptionInfo CreateExceptionInfo(Exception exception)
    {
        var info = new ExceptionInfo
        {
            Type = exception.GetType().FullName ?? exception.GetType().Name,
            Message = exception.Message,
            StackTrace = exception.StackTrace
        };

        if (exception.InnerException != null)
        {
            info.InnerException = CreateExceptionInfo(exception.InnerException);
        }

        return info;
    }

    private void InitializeFileWriter()
    {
        if (string.IsNullOrEmpty(_config.OutputPath)) return;

        try
        {
            var directory = Path.GetDirectoryName(_config.OutputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _fileWriter = new StreamWriter(_config.OutputPath, append: true, Encoding.UTF8)
            {
                AutoFlush = false
            };

            _currentFileSize = new FileInfo(_config.OutputPath).Length;
        }
        catch (Exception ex)
        {
            _microsoftLogger?.LogError(ex, "Failed to initialize log file: {Path}", _config.OutputPath);
        }
    }

    private async Task RotateLogFileAsync(CancellationToken cancellationToken)
    {
        if (_fileWriter == null || string.IsNullOrEmpty(_config.OutputPath)) return;

        try
        {
            await _fileWriter.FlushAsync(cancellationToken);
            await _fileWriter.DisposeAsync();

            // Rotate existing files
            for (int i = _config.MaxRotatedFiles - 1; i > 0; i--)
            {
                var oldPath = $"{_config.OutputPath}.{i}";
                var newPath = $"{_config.OutputPath}.{i + 1}";

                if (File.Exists(oldPath))
                {
                    if (i + 1 > _config.MaxRotatedFiles)
                    {
                        File.Delete(oldPath);
                    }
                    else
                    {
                        File.Move(oldPath, newPath, overwrite: true);
                    }
                }
            }

            // Move current to .1
            var rotatedPath = $"{_config.OutputPath}.1";
            File.Move(_config.OutputPath, rotatedPath, overwrite: true);

            // Create new file
            _fileWriter = new StreamWriter(_config.OutputPath, append: false, Encoding.UTF8)
            {
                AutoFlush = false
            };
            _currentFileSize = 0;
            _fileRotations++;
        }
        catch (Exception ex)
        {
            _microsoftLogger?.LogError(ex, "Failed to rotate log file");
        }
    }
}
