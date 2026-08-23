using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace CreatioHelper.Agent.Logging;

public sealed class SqliteLogSink : ILogEventSink, IDisposable
{
    private const int MaxRows = 50000;
    private const int MaxQueue = 20000;
    private const int MaxBatch = 5000;

    private static readonly MessageTemplateTextFormatter MessageFormatter = new("{Message:lj}", null);

    private readonly SqliteConnection _connection;
    private readonly ConcurrentQueue<(DateTime Timestamp, string Level, string Facility, string Message)> _queue = new();
    private readonly Timer _flushTimer;
    private readonly object _flushLock = new();
    private long _sinceRetention;
    private bool _disposed;

    public SqliteLogSink(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        _connection = new SqliteConnection($"Data Source={databasePath}");
        _connection.Open();

        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute(@"CREATE TABLE IF NOT EXISTS system_log (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    timestamp TEXT NOT NULL,
                    level TEXT NOT NULL,
                    facility TEXT NOT NULL DEFAULT 'app',
                    message TEXT NOT NULL);");
        Execute("CREATE INDEX IF NOT EXISTS idx_system_log_id ON system_log(id DESC);");
        Execute("CREATE INDEX IF NOT EXISTS idx_system_log_level ON system_log(level);");
        Execute("CREATE INDEX IF NOT EXISTS idx_system_log_facility ON system_log(facility);");

        _flushTimer = new Timer(_ => Flush(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void Emit(LogEvent logEvent)
    {
        if (_disposed)
        {
            return;
        }

        using var writer = new StringWriter();
        MessageFormatter.Format(logEvent, writer);
        var message = writer.ToString();

        _queue.Enqueue((logEvent.Timestamp.UtcDateTime,
            SystemLogFormat.Abbreviate(logEvent.Level),
            SystemLogFormat.ExtractFacility(message),
            message));

        while (_queue.Count > MaxQueue && _queue.TryDequeue(out _))
        {
        }
    }

    private void Flush()
    {
        if (_queue.IsEmpty || !Monitor.TryEnter(_flushLock))
        {
            return;
        }

        try
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO system_log (timestamp, level, facility, message) VALUES ($t, $l, $f, $m);";
            var pt = command.CreateParameter(); pt.ParameterName = "$t"; command.Parameters.Add(pt);
            var pl = command.CreateParameter(); pl.ParameterName = "$l"; command.Parameters.Add(pl);
            var pf = command.CreateParameter(); pf.ParameterName = "$f"; command.Parameters.Add(pf);
            var pm = command.CreateParameter(); pm.ParameterName = "$m"; command.Parameters.Add(pm);

            var written = 0;
            while (written < MaxBatch && _queue.TryDequeue(out var entry))
            {
                pt.Value = entry.Timestamp.ToString("o");
                pl.Value = entry.Level;
                pf.Value = entry.Facility;
                pm.Value = entry.Message;
                command.ExecuteNonQuery();
                written++;
            }

            transaction.Commit();

            _sinceRetention += written;
            if (_sinceRetention >= 1000)
            {
                _sinceRetention = 0;
                using var trim = _connection.CreateCommand();
                trim.CommandText = "DELETE FROM system_log WHERE id <= (SELECT MAX(id) FROM system_log) - $max;";
                var pmax = trim.CreateParameter(); pmax.ParameterName = "$max"; pmax.Value = MaxRows; trim.Parameters.Add(pmax);
                trim.ExecuteNonQuery();
            }
        }
        catch
        {
        }
        finally
        {
            Monitor.Exit(_flushLock);
        }
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _flushTimer.Dispose();
        Flush();
        _connection.Dispose();
    }
}
