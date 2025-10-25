using System.Collections.Concurrent;
using System.Text.Json;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Entities.Events;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using EventType = CreatioHelper.Domain.Entities.Events.SyncEventType;
using NewDeviceStatistics = CreatioHelper.Domain.Entities.Statistics.DeviceStatistics;
using NewFolderStatistics = CreatioHelper.Domain.Entities.Statistics.FolderStatistics;

namespace CreatioHelper.Infrastructure.Services.Statistics;

/// <summary>
/// Сборщик статистики синхронизации (на основе Syncthing Statistics)
/// </summary>
public class StatisticsCollector : BackgroundService, IStatisticsCollector
{
    private readonly ILogger<StatisticsCollector> _logger;
    private readonly ISyncDatabase _database;
    private readonly IEventLogger _eventLogger;
    
    private readonly ConcurrentDictionary<string, DeviceStatisticsReference> _deviceStats;
    private readonly ConcurrentDictionary<string, FolderStatisticsReference> _folderStats;
    
    private readonly SystemStatistics _systemStats;
    private readonly PerformanceStatistics _performanceStats;
    private readonly object _systemStatsLock = new();
    private readonly object _performanceStatsLock = new();
    
    private readonly Timer _statsUpdateTimer;
    private const int StatsUpdateIntervalMs = 30000; // 30 seconds like Syncthing
    
    public StatisticsCollector(ILogger<StatisticsCollector> logger, ISyncDatabase database, IEventLogger eventLogger)
    {
        _logger = logger;
        _database = database;
        _eventLogger = eventLogger;
        
        _deviceStats = new ConcurrentDictionary<string, DeviceStatisticsReference>();
        _folderStats = new ConcurrentDictionary<string, FolderStatisticsReference>();
        
        _systemStats = new SystemStatistics { StartTime = DateTime.UtcNow };
        _performanceStats = new PerformanceStatistics();
        
        // Timer для периодического обновления статистики
        _statsUpdateTimer = new Timer(UpdatePeriodicStatistics, null, StatsUpdateIntervalMs, StatsUpdateIntervalMs);
    }
    
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("StatisticsCollector started");
        
        // Инициализация статистики из базы данных
        await InitializeStatisticsAsync(stoppingToken);
        
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                
                // Периодическое сохранение статистики в базу данных
                await PersistStatisticsAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in StatisticsCollector background service: {Message}", ex.Message);
        }
        
        _logger.LogInformation("StatisticsCollector stopped");
    }
    
    public async Task<NewDeviceStatistics> GetDeviceStatisticsAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var reference = GetDeviceStatisticsReference(deviceId);
        return await reference.GetStatisticsAsync(cancellationToken);
    }
    
    public async Task RecordDeviceConnectedAsync(string deviceId, string connectionType, string address, CancellationToken cancellationToken = default)
    {
        var reference = GetDeviceStatisticsReference(deviceId);
        await reference.WasSeenAsync(cancellationToken);
        await reference.RecordConnectionAsync(connectionType, address, cancellationToken);
        
        _eventLogger.LogDeviceEvent(EventType.DeviceConnected, deviceId, $"Device connected via {connectionType}", 
            new { ConnectionType = connectionType, Address = address });
        
        _logger.LogDebug("Recorded device connection: {DeviceId} via {ConnectionType} at {Address}", deviceId, connectionType, address);
    }
    
    public async Task RecordDeviceDisconnectedAsync(string deviceId, TimeSpan connectionDuration, CancellationToken cancellationToken = default)
    {
        var reference = GetDeviceStatisticsReference(deviceId);
        await reference.SetLastConnectionDurationAsync(connectionDuration, cancellationToken);
        await reference.IncrementConnectionCountAsync(cancellationToken);
        
        _eventLogger.LogDeviceEvent(EventType.DeviceDisconnected, deviceId, $"Device disconnected after {connectionDuration.TotalSeconds:F1}s", 
            new { DurationSeconds = connectionDuration.TotalSeconds });
        
        _logger.LogDebug("Recorded device disconnection: {DeviceId}, duration: {Duration}", deviceId, connectionDuration);
    }
    
    public async Task RecordDeviceTrafficAsync(string deviceId, long bytesIn, long bytesOut, CancellationToken cancellationToken = default)
    {
        var reference = GetDeviceStatisticsReference(deviceId);
        await reference.AddTrafficAsync(bytesIn, bytesOut, cancellationToken);
        
        // Обновляем статистику скорости
        await reference.UpdateSpeedStatisticsAsync(cancellationToken);
        
        _logger.LogTrace("Recorded traffic for device {DeviceId}: {BytesIn} in, {BytesOut} out", deviceId, bytesIn, bytesOut);
    }
    
    public async Task<NewFolderStatistics> GetFolderStatisticsAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var reference = GetFolderStatisticsReference(folderId);
        return await reference.GetStatisticsAsync(cancellationToken);
    }
    
    public async Task RecordFileProcessedAsync(string folderId, string fileName, bool deleted, long size, string action, CancellationToken cancellationToken = default)
    {
        var reference = GetFolderStatisticsReference(folderId);
        await reference.ReceivedFileAsync(fileName, deleted, size, action, cancellationToken);
        
        var eventType = action switch
        {
            "Created" => EventType.LocalChangeDetected,
            "Modified" => EventType.LocalChangeDetected,
            "Deleted" => EventType.LocalChangeDetected,
            "Downloaded" => EventType.DownloadCompleted,
            "Uploaded" => EventType.UploadCompleted,
            _ => EventType.ItemFinished
        };
        
        _eventLogger.LogFileEvent(eventType, folderId, fileName, $"File {action}: {fileName} ({size} bytes)", 
            new { Action = action, Size = size, Deleted = deleted });
        
        _logger.LogDebug("Recorded file processing: {FolderId}/{FileName} - {Action} ({Size} bytes)", folderId, fileName, action, size);
    }
    
    public async Task RecordFolderScanCompletedAsync(string folderId, long totalFiles, long totalSize, CancellationToken cancellationToken = default)
    {
        var reference = GetFolderStatisticsReference(folderId);
        await reference.ScanCompletedAsync(totalFiles, totalSize, cancellationToken);
        
        _eventLogger.LogFolderEvent(EventType.FolderScanComplete, folderId, $"Folder scan completed: {totalFiles} files, {totalSize} bytes", 
            new { TotalFiles = totalFiles, TotalSize = totalSize });
        
        _logger.LogDebug("Recorded folder scan completion: {FolderId} - {TotalFiles} files, {TotalSize} bytes", folderId, totalFiles, totalSize);
    }
    
    public async Task UpdateFolderSyncStatisticsAsync(string folderId, long localFiles, long localSize, long remoteFiles, long remoteSize, 
        long pendingFiles, long pendingSize, double completionPercentage, CancellationToken cancellationToken = default)
    {
        var reference = GetFolderStatisticsReference(folderId);
        await reference.UpdateSyncStatisticsAsync(localFiles, localSize, remoteFiles, remoteSize, 
            pendingFiles, pendingSize, completionPercentage, cancellationToken);
        
        _logger.LogTrace("Updated folder sync statistics: {FolderId} - {CompletionPercentage:F1}% complete", folderId, completionPercentage);
    }
    
    public async Task<ConnectionStatistics> GetConnectionStatisticsAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var reference = GetDeviceStatisticsReference(deviceId);
        return await reference.GetConnectionStatisticsAsync(cancellationToken);
    }
    
    public async Task UpdateConnectionStatisticsAsync(string deviceId, long inBytes, long outBytes, double inBytesPerSecond, 
        double outBytesPerSecond, string connectionType, string address, bool isConnected, CancellationToken cancellationToken = default)
    {
        var reference = GetDeviceStatisticsReference(deviceId);
        await reference.UpdateConnectionStatisticsAsync(inBytes, outBytes, inBytesPerSecond, outBytesPerSecond, 
            connectionType, address, isConnected, cancellationToken);
        
        _logger.LogTrace("Updated connection statistics: {DeviceId} - {InSpeed:F1} KB/s in, {OutSpeed:F1} KB/s out", 
            deviceId, inBytesPerSecond / 1024, outBytesPerSecond / 1024);
    }
    
    public Task<SystemStatistics> GetSystemStatisticsAsync(CancellationToken cancellationToken = default)
    {
        lock (_systemStatsLock)
        {
            var stats = new SystemStatistics
            {
                StartTime = _systemStats.StartTime,
                Version = _systemStats.Version,
                ConnectedDevices = _systemStats.ConnectedDevices,
                TotalDevices = _systemStats.TotalDevices,
                ActiveFolders = _systemStats.ActiveFolders,
                TotalFolders = _systemStats.TotalFolders,
                TotalDataSize = _systemStats.TotalDataSize,
                MemoryUsage = _systemStats.MemoryUsage,
                CpuUsage = _systemStats.CpuUsage,
                Goroutines = _systemStats.Goroutines,
                OpenFiles = _systemStats.OpenFiles
            };
            
            return Task.FromResult(stats);
        }
    }
    
    public async Task UpdateSystemStatisticsAsync(int connectedDevices, int totalDevices, int activeFolders, int totalFolders, 
        long totalDataSize, long memoryUsage, double cpuUsage, int goroutines, int openFiles, CancellationToken cancellationToken = default)
    {
        lock (_systemStatsLock)
        {
            _systemStats.ConnectedDevices = connectedDevices;
            _systemStats.TotalDevices = totalDevices;
            _systemStats.ActiveFolders = activeFolders;
            _systemStats.TotalFolders = totalFolders;
            _systemStats.TotalDataSize = totalDataSize;
            _systemStats.MemoryUsage = memoryUsage;
            _systemStats.CpuUsage = cpuUsage;
            _systemStats.Goroutines = goroutines;
            _systemStats.OpenFiles = openFiles;
        }
        
        await Task.CompletedTask;
        _logger.LogTrace("Updated system statistics: {ConnectedDevices}/{TotalDevices} devices, {ActiveFolders}/{TotalFolders} folders", 
            connectedDevices, totalDevices, activeFolders, totalFolders);
    }
    
    public Task<PerformanceStatistics> GetPerformanceStatisticsAsync(CancellationToken cancellationToken = default)
    {
        lock (_performanceStatsLock)
        {
            return Task.FromResult(new PerformanceStatistics
            {
                Timestamp = _performanceStats.Timestamp,
                FileScanRate = _performanceStats.FileScanRate,
                IndexingRate = _performanceStats.IndexingRate,
                NetworkLatencyMs = _performanceStats.NetworkLatencyMs,
                DiskThroughput = _performanceStats.DiskThroughput,
                BuffersUsed = _performanceStats.BuffersUsed,
                MaxBuffers = _performanceStats.MaxBuffers,
                ActiveConnections = _performanceStats.ActiveConnections,
                SyncQueueLength = _performanceStats.SyncQueueLength
            });
        }
    }
    
    public async Task RecordPerformanceMetricsAsync(double fileScanRate, double indexingRate, double networkLatencyMs, 
        double diskThroughput, long buffersUsed, long maxBuffers, int activeConnections, int syncQueueLength, CancellationToken cancellationToken = default)
    {
        lock (_performanceStatsLock)
        {
            _performanceStats.Timestamp = DateTime.UtcNow;
            _performanceStats.FileScanRate = fileScanRate;
            _performanceStats.IndexingRate = indexingRate;
            _performanceStats.NetworkLatencyMs = networkLatencyMs;
            _performanceStats.DiskThroughput = diskThroughput;
            _performanceStats.BuffersUsed = buffersUsed;
            _performanceStats.MaxBuffers = maxBuffers;
            _performanceStats.ActiveConnections = activeConnections;
            _performanceStats.SyncQueueLength = syncQueueLength;
        }
        
        // Отправляем события производительности, если есть проблемы
        if (networkLatencyMs > 500)
        {
            _eventLogger.LogSystemEvent(EventType.NetworkLatencyHigh, $"High network latency: {networkLatencyMs:F1}ms", 
                new { LatencyMs = networkLatencyMs });
        }
        
        if (buffersUsed > maxBuffers * 0.9)
        {
            _eventLogger.LogSystemEvent(EventType.PerformanceAlert, $"High buffer usage: {buffersUsed}/{maxBuffers} ({buffersUsed * 100.0 / maxBuffers:F1}%)", 
                new { BuffersUsed = buffersUsed, MaxBuffers = maxBuffers });
        }
        
        await Task.CompletedTask;
        _logger.LogTrace("Updated performance statistics: scan {FileScanRate:F1}/s, index {IndexingRate:F1}/s, latency {NetworkLatencyMs:F1}ms", 
            fileScanRate, indexingRate, networkLatencyMs);
    }
    
    public async Task<Dictionary<string, NewDeviceStatistics>> GetAllDeviceStatisticsAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, NewDeviceStatistics>();
        
        foreach (var kvp in _deviceStats)
        {
            result[kvp.Key] = await kvp.Value.GetStatisticsAsync(cancellationToken);
        }
        
        return result;
    }
    
    // Syncthing-compatible method without deviceId parameter
    public async Task<Dictionary<string, NewDeviceStatistics>> GetDeviceStatisticsAsync(CancellationToken cancellationToken = default)
    {
        return await GetAllDeviceStatisticsAsync(cancellationToken);
    }
    
    public async Task<Dictionary<string, NewFolderStatistics>> GetAllFolderStatisticsAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, NewFolderStatistics>();
        
        foreach (var kvp in _folderStats)
        {
            result[kvp.Key] = await kvp.Value.GetStatisticsAsync(cancellationToken);
        }
        
        return result;
    }
    
    // Syncthing-compatible method without folderId parameter
    public async Task<Dictionary<string, NewFolderStatistics>> GetFolderStatisticsAsync(CancellationToken cancellationToken = default)
    {
        return await GetAllFolderStatisticsAsync(cancellationToken);
    }
    
    public async Task CleanupOldStatisticsAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        // Реализация очистки старых статистик из базы данных
        var cutoffDate = DateTime.UtcNow - maxAge;
        
        // В реальной реализации здесь будет очистка базы данных
        await Task.CompletedTask;
        
        _logger.LogDebug("Cleaned up statistics older than {CutoffDate}", cutoffDate);
    }
    
    public async Task ResetDeviceStatisticsAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (_deviceStats.TryRemove(deviceId, out var reference))
        {
            await reference.ResetAsync(cancellationToken);
        }
        
        _logger.LogDebug("Reset device statistics: {DeviceId}", deviceId);
    }
    
    public async Task ResetFolderStatisticsAsync(string folderId, CancellationToken cancellationToken = default)
    {
        if (_folderStats.TryRemove(folderId, out var reference))
        {
            await reference.ResetAsync(cancellationToken);
        }
        
        _logger.LogDebug("Reset folder statistics: {FolderId}", folderId);
    }
    
    public async Task<string> ExportStatisticsAsync(DateTime? since = null, CancellationToken cancellationToken = default)
    {
        var export = new
        {
            ExportedAt = DateTime.UtcNow,
            Since = since,
            SystemStatistics = await GetSystemStatisticsAsync(cancellationToken),
            PerformanceStatistics = await GetPerformanceStatisticsAsync(cancellationToken),
            DeviceStatistics = await GetAllDeviceStatisticsAsync(cancellationToken),
            FolderStatistics = await GetAllFolderStatisticsAsync(cancellationToken)
        };
        
        return JsonSerializer.Serialize(export, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }
    
    private DeviceStatisticsReference GetDeviceStatisticsReference(string deviceId)
    {
        return _deviceStats.GetOrAdd(deviceId, id => new DeviceStatisticsReference(id, _database, _logger));
    }
    
    private FolderStatisticsReference GetFolderStatisticsReference(string folderId)
    {
        return _folderStats.GetOrAdd(folderId, id => new FolderStatisticsReference(id, _database, _logger));
    }
    
    private async Task InitializeStatisticsAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Загружаем существующую статистику из базы данных
            _logger.LogDebug("Initializing statistics from database...");
            
            // Инициализация системной статистики
            lock (_systemStatsLock)
            {
                _systemStats.Version = typeof(StatisticsCollector).Assembly.GetName().Version?.ToString() ?? "1.0.0";
            }
            
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initializing statistics: {Message}", ex.Message);
        }
    }
    
    private async Task PersistStatisticsAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Сохраняем статистику в базу данных периодически
            _logger.LogTrace("Persisting statistics to database...");
            
            // В реальной реализации здесь будет сохранение в базу данных
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error persisting statistics: {Message}", ex.Message);
        }
    }
    
    private void UpdatePeriodicStatistics(object? state)
    {
        try
        {
            // Обновляем статистику производительности системы
            var process = System.Diagnostics.Process.GetCurrentProcess();
            
            lock (_systemStatsLock)
            {
                _systemStats.MemoryUsage = process.WorkingSet64;
                _systemStats.Goroutines = System.Threading.ThreadPool.ThreadCount;
            }
            
            // В реальной реализации здесь будет сбор более детальной информации о производительности
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating periodic statistics: {Message}", ex.Message);
        }
    }
    
    public override void Dispose()
    {
        _statsUpdateTimer?.Dispose();
        
        foreach (var reference in _deviceStats.Values)
        {
            reference.Dispose();
        }
        
        foreach (var reference in _folderStats.Values)
        {
            reference.Dispose();
        }
        
        base.Dispose();
    }
}