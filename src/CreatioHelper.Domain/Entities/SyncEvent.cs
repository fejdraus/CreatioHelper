using System.Text.Json;

namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Событие синхронизации (на основе Syncthing Event)
/// </summary>
public class SyncEvent
{
    /// <summary>
    /// Последовательный ID события для подписки
    /// </summary>
    public int SubscriptionId { get; set; }

    /// <summary>
    /// Глобальный ID события во всех подписках
    /// </summary>
    public int GlobalId { get; set; }

    /// <summary>
    /// Временная метка события (высокая точность)
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Тип события
    /// </summary>
    public SyncEventType EventType { get; set; }

    /// <summary>
    /// Данные события (JSON)
    /// </summary>
    public object? Data { get; set; }

    /// <summary>
    /// ID устройства, связанного с событием
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// ID папки, связанной с событием
    /// </summary>
    public string? FolderId { get; set; }

    /// <summary>
    /// Путь к файлу (для файловых событий)
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Уровень важности события
    /// </summary>
    public EventPriority Priority { get; set; }

    /// <summary>
    /// Сообщение события
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Дополнительные метаданные события
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }

    /// <summary>
    /// Сериализация данных события в JSON
    /// </summary>
    public string SerializeData()
    {
        if (Data == null) return "{}";
        
        try
        {
            return JsonSerializer.Serialize(Data, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        }
        catch
        {
            return "{}";
        }
    }

    /// <summary>
    /// Десериализация данных события из JSON
    /// </summary>
    public T? DeserializeData<T>() where T : class
    {
        if (Data == null) return null;

        try
        {
            if (Data is JsonElement jsonElement)
            {
                return JsonSerializer.Deserialize<T>(jsonElement.GetRawText());
            }
            
            if (Data is string jsonString)
            {
                return JsonSerializer.Deserialize<T>(jsonString);
            }

            var serialized = JsonSerializer.Serialize(Data);
            return JsonSerializer.Deserialize<T>(serialized);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Создает событие с типизированными данными
    /// </summary>
    public static SyncEvent Create<T>(SyncEventType eventType, T data, string? message = null, 
        string? deviceId = null, string? folderId = null, string? filePath = null)
    {
        return new SyncEvent
        {
            EventType = eventType,
            Data = data,
            Message = message,
            DeviceId = deviceId,
            FolderId = folderId,
            FilePath = filePath,
            Priority = eventType.GetPriority(),
            Metadata = new Dictionary<string, object>()
        };
    }

    /// <summary>
    /// Создает системное событие
    /// </summary>
    public static SyncEvent SystemEvent(SyncEventType eventType, string message, object? data = null)
    {
        return Create(eventType, data ?? new { }, message);
    }

    /// <summary>
    /// Создает событие устройства
    /// </summary>
    public static SyncEvent DeviceEvent(SyncEventType eventType, string deviceId, string message, object? data = null)
    {
        return Create(eventType, data ?? new { }, message, deviceId: deviceId);
    }

    /// <summary>
    /// Создает событие папки
    /// </summary>
    public static SyncEvent FolderEvent(SyncEventType eventType, string folderId, string message, object? data = null)
    {
        return Create(eventType, data ?? new { }, message, folderId: folderId);
    }

    /// <summary>
    /// Создает событие файла
    /// </summary>
    public static SyncEvent FileEvent(SyncEventType eventType, string folderId, string filePath, string message, object? data = null)
    {
        return Create(eventType, data ?? new { }, message, folderId: folderId, filePath: filePath);
    }

    /// <summary>
    /// Создает событие ошибки
    /// </summary>
    public static SyncEvent ErrorEvent(Exception exception, string? context = null, string? deviceId = null, string? folderId = null)
    {
        var data = new
        {
            ExceptionType = exception.GetType().Name,
            ExceptionMessage = exception.Message,
            StackTrace = exception.StackTrace,
            Context = context,
            InnerException = exception.InnerException?.Message
        };

        return Create(SyncEventType.Error, data, exception.Message, deviceId, folderId);
    }
}

/// <summary>
/// Специализированные типы данных для событий
/// </summary>
    /// <summary>
    /// Данные события подключения устройства
    /// </summary>
    public class DeviceConnectedEventData
    {
        public string DeviceId { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string Address { get; set; } = string.Empty;
        public string ConnectionType { get; set; } = string.Empty; // TCP, QUIC, etc.
        public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
        public Dictionary<string, string>? Properties { get; set; }
    }

    /// <summary>
    /// Данные события прогресса синхронизации
    /// </summary>
    public class SyncProgressEventData
    {
        public string FolderId { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public long TotalBytes { get; set; }
        public long CompletedBytes { get; set; }
        public int TotalFiles { get; set; }
        public int CompletedFiles { get; set; }
        public double ProgressPercentage => TotalBytes > 0 ? (double)CompletedBytes / TotalBytes * 100.0 : 0.0;
        public double TransferRateBytesPerSecond { get; set; }
        public TimeSpan EstimatedTimeRemaining { get; set; }
    }

    /// <summary>
    /// Данные события файла
    /// </summary>
    public class FileEventData
    {
        public string FolderId { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public long Size { get; set; }
        public DateTime ModifiedTime { get; set; }
        public string Action { get; set; } = string.Empty; // Created, Modified, Deleted, Renamed
        public string? OldPath { get; set; } // For rename operations
        public string FileHash { get; set; } = string.Empty;
        public Dictionary<string, object>? Attributes { get; set; }
    }

    /// <summary>
    /// Данные события конфликта
    /// </summary>
    public class ConflictEventData
    {
        public string FolderId { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public string LocalDeviceId { get; set; } = string.Empty;
        public string RemoteDeviceId { get; set; } = string.Empty;
        public DateTime LocalModifiedTime { get; set; }
        public DateTime RemoteModifiedTime { get; set; }
        public string ConflictResolution { get; set; } = string.Empty;
        public string? ConflictCopyPath { get; set; }
    }

    /// <summary>
    /// Данные события производительности
    /// </summary>
    public class PerformanceEventData
    {
        public string MetricName { get; set; } = string.Empty;
        public double CurrentValue { get; set; }
        public double ThresholdValue { get; set; }
        public string Unit { get; set; } = string.Empty;
        public string Severity { get; set; } = string.Empty; // Info, Warning, Critical
        public Dictionary<string, double>? AdditionalMetrics { get; set; }
    }

    /// <summary>
    /// Данные статистики передачи данных
    /// </summary>
    public class TransferStatsEventData
    {
        public string DeviceId { get; set; } = string.Empty;
        public long BytesSent { get; set; }
        public long BytesReceived { get; set; }
        public double SendRateBytesPerSecond { get; set; }
        public double ReceiveRateBytesPerSecond { get; set; }
        public int ActiveConnections { get; set; }
        public TimeSpan ConnectionDuration { get; set; }
    }