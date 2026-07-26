using System.Text.Json;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities.Events;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Database;

public class SqliteSyncEventStore : ISyncEventStore
{
    private readonly ILogger<SqliteSyncEventStore> _logger;
    private readonly string _connectionString;

    public SqliteSyncEventStore(ILogger<SqliteSyncEventStore> logger, string connectionString)
    {
        _logger = logger;
        _connectionString = connectionString;
    }

    public async Task AppendAsync(IReadOnlyList<SyncEvent> events, CancellationToken cancellationToken = default)
    {
        if (events.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = @"
            INSERT INTO sync_events (event_type, folder_id, device_id, file_name, event_data, timestamp)
            VALUES (@type, @folder, @device, @file, @data, @timestamp)";

        var typeParameter = command.Parameters.Add("@type", SqliteType.Text);
        var folderParameter = command.Parameters.Add("@folder", SqliteType.Text);
        var deviceParameter = command.Parameters.Add("@device", SqliteType.Text);
        var fileParameter = command.Parameters.Add("@file", SqliteType.Text);
        var dataParameter = command.Parameters.Add("@data", SqliteType.Text);
        var timestampParameter = command.Parameters.Add("@timestamp", SqliteType.Text);

        foreach (var syncEvent in events)
        {
            typeParameter.Value = syncEvent.Type.ToString();
            folderParameter.Value = (object?)syncEvent.FolderId ?? DBNull.Value;
            deviceParameter.Value = (object?)syncEvent.DeviceId ?? DBNull.Value;
            fileParameter.Value = (object?)syncEvent.FilePath ?? DBNull.Value;
            dataParameter.Value = Serialize(syncEvent);
            timestampParameter.Value = syncEvent.Time.ToString("O");

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<List<SyncEvent>> LoadRecentAsync(int limit, CancellationToken cancellationToken = default)
    {
        var events = new List<SyncEvent>();

        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT event_type, folder_id, device_id, file_name, event_data, timestamp
            FROM sync_events
            ORDER BY id DESC
            LIMIT @limit";
        command.Parameters.AddWithValue("@limit", limit);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var syncEvent = Deserialize(reader.IsDBNull(4) ? null : reader.GetString(4));

            if (Enum.TryParse<SyncEventType>(reader.GetString(0), out var eventType))
            {
                syncEvent.Type = eventType;
            }

            syncEvent.FolderId = reader.IsDBNull(1) ? null : reader.GetString(1);
            syncEvent.DeviceId = reader.IsDBNull(2) ? null : reader.GetString(2);
            syncEvent.FilePath = reader.IsDBNull(3) ? null : reader.GetString(3);

            if (DateTime.TryParse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp))
            {
                syncEvent.Time = timestamp;
            }

            events.Add(syncEvent);
        }

        events.Reverse();
        return events;
    }

    private string Serialize(SyncEvent syncEvent)
    {
        try
        {
            return JsonSerializer.Serialize(new PersistedEvent
            {
                Message = syncEvent.Message,
                Priority = syncEvent.Priority.ToString(),
                Data = syncEvent.Data
            });
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException)
        {
            // An unserializable payload must not cost us the event itself
            _logger.LogDebug(ex, "Event payload of type {Type} could not be serialized", syncEvent.Type);
            return JsonSerializer.Serialize(new PersistedEvent
            {
                Message = syncEvent.Message,
                Priority = syncEvent.Priority.ToString()
            });
        }
    }

    private static SyncEvent Deserialize(string? json)
    {
        var syncEvent = new SyncEvent();

        if (string.IsNullOrEmpty(json))
        {
            return syncEvent;
        }

        try
        {
            var persisted = JsonSerializer.Deserialize<PersistedEvent>(json);
            if (persisted != null)
            {
                syncEvent.Message = persisted.Message;
                syncEvent.Data = persisted.Data;

                if (Enum.TryParse<EventPriority>(persisted.Priority, out var priority))
                {
                    syncEvent.Priority = priority;
                }
            }
        }
        catch (JsonException)
        {
        }

        return syncEvent;
    }

    private class PersistedEvent
    {
        public string? Message { get; set; }
        public string? Priority { get; set; }
        public object? Data { get; set; }
    }
}
