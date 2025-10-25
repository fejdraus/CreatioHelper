using System.ComponentModel.DataAnnotations;

namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Статистика устройства (на основе Syncthing DeviceStatistics)
/// </summary>
public class DeviceStatistics
{
    /// <summary>
    /// Время последнего подключения устройства
    /// </summary>
    public DateTime LastSeen { get; set; } = DateTime.UnixEpoch;
    
    /// <summary>
    /// Длительность последнего подключения в секундах
    /// </summary>
    public double LastConnectionDurationSeconds { get; set; }
    
    /// <summary>
    /// Общее количество переданных байт
    /// </summary>
    public long TotalBytesOut { get; set; }
    
    /// <summary>
    /// Общее количество полученных байт
    /// </summary>
    public long TotalBytesIn { get; set; }
    
    /// <summary>
    /// Количество успешных подключений
    /// </summary>
    public long SuccessfulConnections { get; set; }
    
    /// <summary>
    /// Количество неудачных подключений
    /// </summary>
    public long FailedConnections { get; set; }
    
    /// <summary>
    /// Средняя скорость отправки (байт/сек)
    /// </summary>
    public double AverageUploadSpeed { get; set; }
    
    /// <summary>
    /// Средняя скорость получения (байт/сек)
    /// </summary>
    public double AverageDownloadSpeed { get; set; }
    
    /// <summary>
    /// Время первого подключения
    /// </summary>
    public DateTime FirstSeen { get; set; } = DateTime.UnixEpoch;
    
    /// <summary>
    /// Общее время подключения
    /// </summary>
    public TimeSpan TotalConnectionTime { get; set; }
}

/// <summary>
/// Информация о последнем файле (на основе Syncthing LastFile)
/// </summary>
public class LastFileInfo
{
    /// <summary>
    /// Время обработки файла
    /// </summary>
    public DateTime At { get; set; }
    
    /// <summary>
    /// Имя файла
    /// </summary>
    public string FileName { get; set; } = string.Empty;
    
    /// <summary>
    /// Был ли файл удален
    /// </summary>
    public bool Deleted { get; set; }
    
    /// <summary>
    /// Размер файла
    /// </summary>
    public long Size { get; set; }
    
    /// <summary>
    /// Тип операции
    /// </summary>
    public string Action { get; set; } = string.Empty; // Created, Modified, Deleted, Downloaded, Uploaded
}

/// <summary>
/// Статистика папки (на основе Syncthing FolderStatistics)
/// </summary>
public class FolderStatistics
{
    /// <summary>
    /// Информация о последнем файле
    /// </summary>
    public LastFileInfo LastFile { get; set; } = new();
    
    /// <summary>
    /// Время последнего сканирования
    /// </summary>
    public DateTime LastScan { get; set; }
    
    /// <summary>
    /// Общее количество файлов
    /// </summary>
    public long TotalFiles { get; set; }
    
    /// <summary>
    /// Общий размер папки в байтах
    /// </summary>
    public long TotalSize { get; set; }
    
    /// <summary>
    /// Количество локальных файлов
    /// </summary>
    public long LocalFiles { get; set; }
    
    /// <summary>
    /// Размер локальных файлов
    /// </summary>
    public long LocalSize { get; set; }
    
    /// <summary>
    /// Количество удаленных файлов
    /// </summary>
    public long RemoteFiles { get; set; }
    
    /// <summary>
    /// Размер удаленных файлов
    /// </summary>
    public long RemoteSize { get; set; }
    
    /// <summary>
    /// Количество файлов в очереди синхронизации
    /// </summary>
    public long PendingFiles { get; set; }
    
    /// <summary>
    /// Размер файлов в очереди синхронизации
    /// </summary>
    public long PendingSize { get; set; }
    
    /// <summary>
    /// Процент завершенности синхронизации
    /// </summary>
    public double CompletionPercentage { get; set; }
    
    /// <summary>
    /// Количество конфликтов
    /// </summary>
    public long Conflicts { get; set; }
    
    /// <summary>
    /// Количество ошибок
    /// </summary>
    public long Errors { get; set; }
    
    /// <summary>
    /// Время последней синхронизации
    /// </summary>
    public DateTime LastSync { get; set; }
    
    /// <summary>
    /// Скорость синхронизации (файлов в секунду)
    /// </summary>
    public double SyncRate { get; set; }
}

/// <summary>
/// Статистика соединения (на основе Syncthing protocol.Statistics)
/// </summary>
public class ConnectionStatistics
{
    /// <summary>
    /// Время создания статистики
    /// </summary>
    public DateTime At { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Общее количество входящих байт
    /// </summary>
    public long InBytesTotal { get; set; }
    
    /// <summary>
    /// Общее количество исходящих байт
    /// </summary>
    public long OutBytesTotal { get; set; }
    
    /// <summary>
    /// Время начала соединения
    /// </summary>
    public DateTime StartedAt { get; set; }
    
    /// <summary>
    /// Текущая скорость входящих данных (байт/сек)
    /// </summary>
    public double InBytesPerSecond { get; set; }
    
    /// <summary>
    /// Текущая скорость исходящих данных (байт/сек)
    /// </summary>
    public double OutBytesPerSecond { get; set; }
    
    /// <summary>
    /// Тип соединения
    /// </summary>
    public string ConnectionType { get; set; } = string.Empty; // TCP, QUIC
    
    /// <summary>
    /// Адрес соединения
    /// </summary>
    public string Address { get; set; } = string.Empty;
    
    /// <summary>
    /// Активно ли соединение
    /// </summary>
    public bool IsConnected { get; set; }
}

/// <summary>
/// Общая статистика системы
/// </summary>
public class SystemStatistics
{
    /// <summary>
    /// Время старта системы
    /// </summary>
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Время работы системы
    /// </summary>
    public TimeSpan Uptime => DateTime.UtcNow - StartTime;
    
    /// <summary>
    /// Версия системы
    /// </summary>
    public string Version { get; set; } = string.Empty;
    
    /// <summary>
    /// Количество подключенных устройств
    /// </summary>
    public int ConnectedDevices { get; set; }
    
    /// <summary>
    /// Общее количество устройств
    /// </summary>
    public int TotalDevices { get; set; }
    
    /// <summary>
    /// Количество активных папок
    /// </summary>
    public int ActiveFolders { get; set; }
    
    /// <summary>
    /// Общее количество папок
    /// </summary>
    public int TotalFolders { get; set; }
    
    /// <summary>
    /// Общий объем данных
    /// </summary>
    public long TotalDataSize { get; set; }
    
    /// <summary>
    /// Использование памяти (байт)
    /// </summary>
    public long MemoryUsage { get; set; }
    
    /// <summary>
    /// Использование CPU (проценты)
    /// </summary>
    public double CpuUsage { get; set; }
    
    /// <summary>
    /// Количество горутин (потоков)
    /// </summary>
    public int Goroutines { get; set; }
    
    /// <summary>
    /// Количество открытых файлов
    /// </summary>
    public int OpenFiles { get; set; }
}

/// <summary>
/// Статистика производительности
/// </summary>
public class PerformanceStatistics
{
    /// <summary>
    /// Время измерения
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Скорость сканирования файлов (файлов/сек)
    /// </summary>
    public double FileScanRate { get; set; }
    
    /// <summary>
    /// Скорость индексации (файлов/сек)
    /// </summary>
    public double IndexingRate { get; set; }
    
    /// <summary>
    /// Средняя задержка сети (мс)
    /// </summary>
    public double NetworkLatencyMs { get; set; }
    
    /// <summary>
    /// Пропускная способность диска (байт/сек)
    /// </summary>
    public double DiskThroughput { get; set; }
    
    /// <summary>
    /// Использование буферов
    /// </summary>
    public long BuffersUsed { get; set; }
    
    /// <summary>
    /// Максимальное количество буферов
    /// </summary>
    public long MaxBuffers { get; set; }
    
    /// <summary>
    /// Процент использования буферов
    /// </summary>
    public double BufferUsagePercentage => MaxBuffers > 0 ? (double)BuffersUsed / MaxBuffers * 100.0 : 0.0;
    
    /// <summary>
    /// Количество активных подключений
    /// </summary>
    public int ActiveConnections { get; set; }
    
    /// <summary>
    /// Очередь синхронизации
    /// </summary>
    public int SyncQueueLength { get; set; }
}