using CreatioHelper.Domain.Entities;
using OldDeviceStatistics = CreatioHelper.Domain.Entities.DeviceStatistics;
using OldFolderStatistics = CreatioHelper.Domain.Entities.FolderStatistics;
using OldLastFileInfo = CreatioHelper.Domain.Entities.LastFileInfo;
using CreatioHelper.Domain.Entities.Statistics;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Интерфейс для сбора и управления статистикой синхронизации (на основе Syncthing Statistics)
/// </summary>
public interface IStatisticsCollector
{
    /// <summary>
    /// Получить статистику устройства
    /// </summary>
    Task<CreatioHelper.Domain.Entities.Statistics.DeviceStatistics> GetDeviceStatisticsAsync(string deviceId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Обновить статистику устройства - устройство подключено
    /// </summary>
    Task RecordDeviceConnectedAsync(string deviceId, string connectionType, string address, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Обновить статистику устройства - устройство отключено
    /// </summary>
    Task RecordDeviceDisconnectedAsync(string deviceId, TimeSpan connectionDuration, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Записать трафик устройства
    /// </summary>
    Task RecordDeviceTrafficAsync(string deviceId, long bytesIn, long bytesOut, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Получить статистику папки
    /// </summary>
    Task<CreatioHelper.Domain.Entities.Statistics.FolderStatistics> GetFolderStatisticsAsync(string folderId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Записать обработку файла в папке
    /// </summary>
    Task RecordFileProcessedAsync(string folderId, string fileName, bool deleted, long size, string action, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Записать завершение сканирования папки
    /// </summary>
    Task RecordFolderScanCompletedAsync(string folderId, long totalFiles, long totalSize, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Обновить статистику синхронизации папки
    /// </summary>
    Task UpdateFolderSyncStatisticsAsync(string folderId, long localFiles, long localSize, long remoteFiles, long remoteSize, 
        long pendingFiles, long pendingSize, double completionPercentage, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Получить статистику соединения
    /// </summary>
    Task<ConnectionStatistics> GetConnectionStatisticsAsync(string deviceId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Обновить статистику соединения
    /// </summary>
    Task UpdateConnectionStatisticsAsync(string deviceId, long inBytes, long outBytes, double inBytesPerSecond, 
        double outBytesPerSecond, string connectionType, string address, bool isConnected, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Получить общую статистику системы
    /// </summary>
    Task<SystemStatistics> GetSystemStatisticsAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Обновить системную статистику
    /// </summary>
    Task UpdateSystemStatisticsAsync(int connectedDevices, int totalDevices, int activeFolders, int totalFolders, 
        long totalDataSize, long memoryUsage, double cpuUsage, int goroutines, int openFiles, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Получить статистику производительности
    /// </summary>
    Task<PerformanceStatistics> GetPerformanceStatisticsAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Записать метрики производительности
    /// </summary>
    Task RecordPerformanceMetricsAsync(double fileScanRate, double indexingRate, double networkLatencyMs, 
        double diskThroughput, long buffersUsed, long maxBuffers, int activeConnections, int syncQueueLength, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Получить все статистики устройств (Syncthing compatible)
    /// </summary>
    Task<Dictionary<string, CreatioHelper.Domain.Entities.Statistics.DeviceStatistics>> GetDeviceStatisticsAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Получить все статистики устройств
    /// </summary>
    Task<Dictionary<string, CreatioHelper.Domain.Entities.Statistics.DeviceStatistics>> GetAllDeviceStatisticsAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Получить все статистики папок (Syncthing compatible)
    /// </summary>
    Task<Dictionary<string, CreatioHelper.Domain.Entities.Statistics.FolderStatistics>> GetFolderStatisticsAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Получить все статистики папок
    /// </summary>
    Task<Dictionary<string, CreatioHelper.Domain.Entities.Statistics.FolderStatistics>> GetAllFolderStatisticsAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Очистить старые статистики
    /// </summary>
    Task CleanupOldStatisticsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Сбросить статистику устройства
    /// </summary>
    Task ResetDeviceStatisticsAsync(string deviceId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Сбросить статистику папки
    /// </summary>
    Task ResetFolderStatisticsAsync(string folderId, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Экспортировать статистики в JSON
    /// </summary>
    Task<string> ExportStatisticsAsync(DateTime? since = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Интерфейс для ссылки на статистику устройства (на основе Syncthing DeviceStatisticsReference)
/// </summary>
public interface IDeviceStatisticsReference
{
    /// <summary>
    /// Получить время последнего подключения
    /// </summary>
    Task<DateTime> GetLastSeenAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Получить длительность последнего подключения
    /// </summary>
    Task<TimeSpan> GetLastConnectionDurationAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Отметить, что устройство было подключено
    /// </summary>
    Task WasSeenAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Записать длительность подключения
    /// </summary>
    Task SetLastConnectionDurationAsync(TimeSpan duration, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Получить полную статистику устройства
    /// </summary>
    Task<CreatioHelper.Domain.Entities.Statistics.DeviceStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Интерфейс для ссылки на статистику папки (на основе Syncthing FolderStatisticsReference)
/// </summary>
public interface IFolderStatisticsReference
{
    /// <summary>
    /// Получить информацию о последнем файле
    /// </summary>
    Task<LastFile> GetLastFileAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Записать обработку файла
    /// </summary>
    Task ReceivedFileAsync(string fileName, bool deleted, long size, string action, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Отметить завершение сканирования
    /// </summary>
    Task ScanCompletedAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Получить время последнего сканирования
    /// </summary>
    Task<DateTime> GetLastScanTimeAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Получить полную статистику папки
    /// </summary>
    Task<CreatioHelper.Domain.Entities.Statistics.FolderStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
}