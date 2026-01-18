using CreatioHelper.Application.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Database;

/// <summary>
/// SQLite implementation of event log repository
/// </summary>
public class EventLogRepository : IEventLogRepository
{
    private readonly Func<SqliteConnection> _getConnection;
    private readonly ILogger _logger;
    private bool _disposed;

    public EventLogRepository(Func<SqliteConnection> getConnection, ILogger logger)
    {
        _getConnection = getConnection;
        _logger = logger;
    }

    public async Task SaveEventAsync(EventLogEntry entry)
    {
        var connection = _getConnection();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO event_log (global_id, event_type, event_time, data)
            VALUES (@globalId, @eventType, @eventTime, @data)
            RETURNING id";

        command.Parameters.AddWithValue("@globalId", entry.GlobalId.ToString("O"));
        command.Parameters.AddWithValue("@eventType", entry.EventType);
        command.Parameters.AddWithValue("@eventTime", entry.EventTime.ToString("O"));
        command.Parameters.AddWithValue("@data", entry.Data);

        var result = await command.ExecuteScalarAsync();
        if (result != null)
        {
            entry.Id = Convert.ToInt64(result);
        }
    }

    public async Task SaveEventsAsync(IEnumerable<EventLogEntry> entries)
    {
        var connection = _getConnection();
        using var transaction = connection.BeginTransaction();

        try
        {
            foreach (var entry in entries)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO event_log (global_id, event_type, event_time, data)
                    VALUES (@globalId, @eventType, @eventTime, @data)
                    RETURNING id";

                command.Parameters.AddWithValue("@globalId", entry.GlobalId.ToString("O"));
                command.Parameters.AddWithValue("@eventType", entry.EventType);
                command.Parameters.AddWithValue("@eventTime", entry.EventTime.ToString("O"));
                command.Parameters.AddWithValue("@data", entry.Data);

                var result = await command.ExecuteScalarAsync();
                if (result != null)
                {
                    entry.Id = Convert.ToInt64(result);
                }
            }

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<IReadOnlyList<EventLogEntry>> GetEventsSinceAsync(long sinceId, int limit = 100, string[]? eventTypes = null)
    {
        var connection = _getConnection();
        using var command = connection.CreateCommand();

        var sql = "SELECT id, global_id, event_type, event_time, data, created_at FROM event_log WHERE id > @sinceId";

        if (eventTypes != null && eventTypes.Length > 0)
        {
            var typeParams = string.Join(",", eventTypes.Select((_, i) => $"@type{i}"));
            sql += $" AND event_type IN ({typeParams})";
        }

        sql += " ORDER BY id ASC LIMIT @limit";

        command.CommandText = sql;
        command.Parameters.AddWithValue("@sinceId", sinceId);
        command.Parameters.AddWithValue("@limit", limit);

        if (eventTypes != null)
        {
            for (int i = 0; i < eventTypes.Length; i++)
            {
                command.Parameters.AddWithValue($"@type{i}", eventTypes[i]);
            }
        }

        var results = new List<EventLogEntry>();
        using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            results.Add(new EventLogEntry
            {
                Id = reader.GetInt64(0),
                GlobalId = DateTime.Parse(reader.GetString(1)),
                EventType = reader.GetString(2),
                EventTime = DateTime.Parse(reader.GetString(3)),
                Data = reader.GetString(4),
                CreatedAt = DateTime.Parse(reader.GetString(5))
            });
        }

        return results;
    }

    public async Task<long> GetLastEventIdAsync()
    {
        var connection = _getConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(id) FROM event_log";

        var result = await command.ExecuteScalarAsync();
        return result == DBNull.Value || result == null ? 0 : Convert.ToInt64(result);
    }

    public async Task<int> DeleteEventsOlderThanAsync(DateTime cutoff)
    {
        var connection = _getConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM event_log WHERE event_time < @cutoff";
        command.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));

        var deleted = await command.ExecuteNonQueryAsync();
        _logger.LogDebug("Deleted {Count} events older than {Cutoff}", deleted, cutoff);
        return deleted;
    }

    public async Task<long> GetEventCountAsync()
    {
        var connection = _getConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM event_log";

        var result = await command.ExecuteScalarAsync();
        return result == null ? 0 : Convert.ToInt64(result);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
