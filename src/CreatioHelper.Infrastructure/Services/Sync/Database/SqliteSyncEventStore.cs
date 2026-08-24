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

    private static void BuildColumnFilters(string? filtersJson, List<string> conditions, List<(string Name, object Value)> parameters)
    {
        if (string.IsNullOrWhiteSpace(filtersJson))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(filtersJson);
            var index = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var field = element.TryGetProperty("f", out var f) ? f.GetString() : null;
                var op = element.TryGetProperty("o", out var o) ? o.GetString() : null;
                var value = element.TryGetProperty("v", out var v) ? v.GetString() : null;

                if (string.IsNullOrEmpty(field) || !AllowedFilterFields.Contains(field) || string.IsNullOrEmpty(op))
                {
                    continue;
                }

                var name = "@flt" + index;
                switch (op)
                {
                    case "contains":
                        conditions.Add($"{field} LIKE {name}");
                        parameters.Add((name, "%" + value + "%"));
                        break;
                    case "not contains":
                        conditions.Add($"{field} NOT LIKE {name}");
                        parameters.Add((name, "%" + value + "%"));
                        break;
                    case "equals":
                        conditions.Add($"{field} = {name}");
                        parameters.Add((name, (object?)value ?? string.Empty));
                        break;
                    case "not equals":
                        conditions.Add($"{field} <> {name}");
                        parameters.Add((name, (object?)value ?? string.Empty));
                        break;
                    case "starts with":
                        conditions.Add($"{field} LIKE {name}");
                        parameters.Add((name, value + "%"));
                        break;
                    case "ends with":
                        conditions.Add($"{field} LIKE {name}");
                        parameters.Add((name, "%" + value));
                        break;
                    case "is empty":
                        conditions.Add($"({field} IS NULL OR {field} = '')");
                        break;
                    case "is not empty":
                        conditions.Add($"({field} IS NOT NULL AND {field} <> '')");
                        break;
                    case "is after":
                        conditions.Add($"{field} > {name}");
                        parameters.Add((name, (object?)value ?? string.Empty));
                        break;
                    case "is on or after":
                        conditions.Add($"{field} >= {name}");
                        parameters.Add((name, (object?)value ?? string.Empty));
                        break;
                    case "is before":
                        conditions.Add($"{field} < {name}");
                        parameters.Add((name, (object?)value ?? string.Empty));
                        break;
                    case "is on or before":
                        conditions.Add($"{field} <= {name}");
                        parameters.Add((name, (object?)value ?? string.Empty));
                        break;
                    case "is":
                        conditions.Add($"{field} LIKE {name}");
                        parameters.Add((name, DatePrefix(value) + "%"));
                        break;
                    case "is not":
                        conditions.Add($"{field} NOT LIKE {name}");
                        parameters.Add((name, DatePrefix(value) + "%"));
                        break;
                    default:
                        continue;
                }

                index++;
            }
        }
        catch (JsonException)
        {
        }
    }

    private static string DatePrefix(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        return value.Length >= 10 ? value[..10] : value;
    }

    private static string ResolveSortColumn(string? sort) => sort switch
    {
        "type" => "event_type",
        "folder" => "folder_id",
        "device" => "device_id",
        _ => "id"
    };

    private static readonly HashSet<string> AllowedFilterFields = new(StringComparer.Ordinal)
    {
        "event_type", "folder_id", "device_id", "file_name", "event_data", "timestamp"
    };

    public async Task<(int Total, List<SyncEvent> Items)> LoadPageAsync(
        int offset,
        int limit,
        string? eventType,
        string? folderId,
        string? deviceId,
        string? search = null,
        string? sort = null,
        string? dir = null,
        string? filters = null,
        CancellationToken cancellationToken = default)
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var hasSearch = !string.IsNullOrWhiteSpace(search);

        var filterParams = new List<(string Name, object Value)>();
        var conditions = new List<string>();
        BuildColumnFilters(filters, conditions, filterParams);
        if (!string.IsNullOrWhiteSpace(eventType) && eventType != "all")
        {
            conditions.Add("event_type = @type");
        }
        if (!string.IsNullOrWhiteSpace(folderId) && folderId != "all")
        {
            conditions.Add("folder_id = @folder");
        }
        if (!string.IsNullOrWhiteSpace(deviceId) && deviceId != "all")
        {
            conditions.Add("device_id = @device");
        }
        if (hasSearch)
        {
            conditions.Add("(event_type LIKE @search OR folder_id LIKE @search OR device_id LIKE @search OR file_name LIKE @search OR event_data LIKE @search)");
        }

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : string.Empty;

        void Bind(SqliteCommand command)
        {
            if (!string.IsNullOrWhiteSpace(eventType) && eventType != "all")
            {
                command.Parameters.AddWithValue("@type", eventType);
            }
            if (!string.IsNullOrWhiteSpace(folderId) && folderId != "all")
            {
                command.Parameters.AddWithValue("@folder", folderId);
            }
            if (!string.IsNullOrWhiteSpace(deviceId) && deviceId != "all")
            {
                command.Parameters.AddWithValue("@device", deviceId);
            }
            if (hasSearch)
            {
                command.Parameters.AddWithValue("@search", "%" + search + "%");
            }
            foreach (var (name, value) in filterParams)
            {
                command.Parameters.AddWithValue(name, value);
            }
        }

        int total;
        using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = $"SELECT COUNT(*) FROM sync_events {whereClause}";
            Bind(countCommand);
            total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        }

        var items = new List<SyncEvent>();
        using (var command = connection.CreateCommand())
        {
            var sortColumn = ResolveSortColumn(sort);
            var sortDirection = string.Equals(dir, "asc", StringComparison.OrdinalIgnoreCase) ? "ASC" : "DESC";

            command.CommandText = $@"
                SELECT id, event_type, folder_id, device_id, file_name, event_data, timestamp
                FROM sync_events
                {whereClause}
                ORDER BY {sortColumn} {sortDirection}, id {sortDirection}
                LIMIT @limit OFFSET @offset";
            Bind(command);
            command.Parameters.AddWithValue("@limit", limit);
            command.Parameters.AddWithValue("@offset", offset);

            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var folder = reader.IsDBNull(2) ? null : reader.GetString(2);
                var device = reader.IsDBNull(3) ? null : reader.GetString(3);
                var file = reader.IsDBNull(4) ? null : reader.GetString(4);
                var json = reader.IsDBNull(5) ? null : reader.GetString(5);

                var syncEvent = new SyncEvent
                {
                    GlobalId = reader.GetInt32(0),
                    FolderId = folder,
                    DeviceId = device,
                    FilePath = file
                };

                if (Enum.TryParse<SyncEventType>(reader.GetString(1), out var eventTypeValue))
                {
                    syncEvent.Type = eventTypeValue;
                }

                if (DateTime.TryParse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind, out var timestamp))
                {
                    syncEvent.Time = timestamp;
                }

                syncEvent.Data = BuildDataDictionary(json, folder, device, file, out var message);
                syncEvent.Message = message;

                items.Add(syncEvent);
            }
        }

        return (total, items);
    }

    private static Dictionary<string, object?> BuildDataDictionary(string? json, string? folderId, string? deviceId, string? fileName, out string? message)
    {
        var data = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        message = null;

        if (!string.IsNullOrEmpty(folderId))
        {
            data["folder"] = folderId;
        }
        if (!string.IsNullOrEmpty(deviceId))
        {
            data["device"] = deviceId;
        }
        if (!string.IsNullOrEmpty(fileName))
        {
            data["item"] = fileName;
        }

        if (string.IsNullOrEmpty(json))
        {
            return data;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (root.TryGetProperty("Message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String)
            {
                message = messageElement.GetString();
                if (!string.IsNullOrEmpty(message))
                {
                    data["message"] = message;
                }
            }

            if (root.TryGetProperty("Data", out var inner) && inner.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in inner.EnumerateObject())
                {
                    data[property.Name] = ReadJsonValue(property.Value);
                }
            }
        }
        catch (JsonException)
        {
        }

        return data;
    }

    private static object? ReadJsonValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => element.GetRawText()
        };
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
