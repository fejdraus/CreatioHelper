using System.Collections.Concurrent;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;
using OldDeviceStatistics = CreatioHelper.Domain.Entities.DeviceStatistics;
using NewDeviceStatistics = CreatioHelper.Domain.Entities.Statistics.DeviceStatistics;

namespace CreatioHelper.Infrastructure.Services.Statistics;

/// <summary>
/// Ссылка на статистику устройства (на основе Syncthing DeviceStatisticsReference)
/// </summary>
public class DeviceStatisticsReference : IDeviceStatisticsReference, IDisposable
{
    private readonly string _deviceId;
    private readonly ISyncDatabase _database;
    private readonly ILogger _logger;
    
    private readonly object _statisticsLock = new();
    private OldDeviceStatistics _statistics;
    private ConnectionStatistics _connectionStatistics;
    
    // Время-скользящие окна для вычисления скорости
    private readonly Queue<(DateTime Time, long BytesIn, long BytesOut)> _trafficSamples = new();
    private readonly TimeSpan _speedCalculationWindow = TimeSpan.FromMinutes(5);
    
    private DateTime _connectionStartTime;
    private bool _disposed = false;
    
    public DeviceStatisticsReference(string deviceId, ISyncDatabase database, ILogger logger)
    {
        _deviceId = deviceId;
        _database = database;
        _logger = logger;
        
        _statistics = new OldDeviceStatistics();
        _connectionStatistics = new ConnectionStatistics { StartedAt = DateTime.UtcNow };
        
        // Загружаем существующую статистику из базы данных
        _ = Task.Run(async () => await LoadStatisticsAsync(CancellationToken.None));
    }
    
    public Task<DateTime> GetLastSeenAsync(CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            return Task.FromResult(_statistics.LastSeen);
        }
    }
    
    public Task<TimeSpan> GetLastConnectionDurationAsync(CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            return Task.FromResult(TimeSpan.FromSeconds(_statistics.LastConnectionDurationSeconds));
        }
    }
    
    public async Task WasSeenAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        
        lock (_statisticsLock)
        {
            _statistics.LastSeen = now;
            
            if (_statistics.FirstSeen == DateTime.UnixEpoch)
            {
                _statistics.FirstSeen = now;
            }
            
            _connectionStartTime = now;
        }
        
        await SaveStatisticsAsync(cancellationToken);
        _logger.LogTrace("Device {DeviceId} was seen at {Time}", _deviceId, now);
    }
    
    public async Task SetLastConnectionDurationAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            _statistics.LastConnectionDurationSeconds = duration.TotalSeconds;
            _statistics.TotalConnectionTime += duration;
        }
        
        await SaveStatisticsAsync(cancellationToken);
        _logger.LogTrace("Device {DeviceId} connection duration set to {Duration}", _deviceId, duration);
    }
    
    public Task<NewDeviceStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            return Task.FromResult(new NewDeviceStatistics
            {
                DeviceId = _deviceId,
                LastSeen = _statistics.LastSeen,
                LastConnectionDurationS = _statistics.LastConnectionDurationSeconds,
                BytesReceived = _statistics.TotalBytesIn,
                BytesSent = _statistics.TotalBytesOut,
                TotalConnections = _statistics.SuccessfulConnections + _statistics.FailedConnections,
                TotalConnectionTimeS = _statistics.TotalConnectionTime.TotalSeconds
            });
        }
    }
    
    /// <summary>
    /// Записать новое подключение
    /// </summary>
    public async Task RecordConnectionAsync(string connectionType, string address, CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            _statistics.SuccessfulConnections++;
            _connectionStatistics.ConnectionType = connectionType;
            _connectionStatistics.Address = address;
            _connectionStatistics.IsConnected = true;
            _connectionStatistics.StartedAt = DateTime.UtcNow;
        }
        
        await SaveStatisticsAsync(cancellationToken);
        _logger.LogTrace("Device {DeviceId} recorded connection via {ConnectionType} at {Address}", _deviceId, connectionType, address);
    }
    
    /// <summary>
    /// Увеличить счетчик подключений
    /// </summary>
    public async Task IncrementConnectionCountAsync(CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            _connectionStatistics.IsConnected = false;
        }
        
        await SaveStatisticsAsync(cancellationToken);
    }
    
    /// <summary>
    /// Записать неудачное подключение
    /// </summary>
    public async Task RecordFailedConnectionAsync(CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            _statistics.FailedConnections++;
        }
        
        await SaveStatisticsAsync(cancellationToken);
        _logger.LogTrace("Device {DeviceId} recorded failed connection", _deviceId);
    }
    
    /// <summary>
    /// Добавить трафик
    /// </summary>
    public Task AddTrafficAsync(long bytesIn, long bytesOut, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        
        lock (_statisticsLock)
        {
            _statistics.TotalBytesIn += bytesIn;
            _statistics.TotalBytesOut += bytesOut;
            
            _connectionStatistics.InBytesTotal += bytesIn;
            _connectionStatistics.OutBytesTotal += bytesOut;
            _connectionStatistics.At = now;
            
            // Добавляем образец для расчета скорости
            _trafficSamples.Enqueue((now, bytesIn, bytesOut));
            
            // Удаляем старые образцы
            while (_trafficSamples.Count > 0 && (now - _trafficSamples.Peek().Time) > _speedCalculationWindow)
            {
                _trafficSamples.Dequeue();
            }
        }
        
        _logger.LogTrace("Device {DeviceId} added traffic: {BytesIn} in, {BytesOut} out", _deviceId, bytesIn, bytesOut);
        return Task.CompletedTask;
    }
    
    /// <summary>
    /// Обновить статистику скорости
    /// </summary>
    public async Task UpdateSpeedStatisticsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        
        lock (_statisticsLock)
        {
            if (_trafficSamples.Count < 2)
            {
                return;
            }
            
            var samples = _trafficSamples.ToArray();
            var oldestSample = samples.First();
            var newestSample = samples.Last();
            
            var timeSpan = newestSample.Time - oldestSample.Time;
            
            if (timeSpan.TotalSeconds > 0)
            {
                var totalBytesIn = samples.Sum(s => s.BytesIn);
                var totalBytesOut = samples.Sum(s => s.BytesOut);
                
                var currentDownloadSpeed = totalBytesIn / timeSpan.TotalSeconds;
                var currentUploadSpeed = totalBytesOut / timeSpan.TotalSeconds;
                
                // Экспоненциальное сглаживание скорости
                _statistics.AverageDownloadSpeed = (_statistics.AverageDownloadSpeed * 0.8) + (currentDownloadSpeed * 0.2);
                _statistics.AverageUploadSpeed = (_statistics.AverageUploadSpeed * 0.8) + (currentUploadSpeed * 0.2);
                
                _connectionStatistics.InBytesPerSecond = currentDownloadSpeed;
                _connectionStatistics.OutBytesPerSecond = currentUploadSpeed;
            }
        }
        
        await Task.CompletedTask;
    }
    
    /// <summary>
    /// Получить статистику соединения
    /// </summary>
    public Task<ConnectionStatistics> GetConnectionStatisticsAsync(CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            return Task.FromResult(new ConnectionStatistics
            {
                At = _connectionStatistics.At,
                InBytesTotal = _connectionStatistics.InBytesTotal,
                OutBytesTotal = _connectionStatistics.OutBytesTotal,
                StartedAt = _connectionStatistics.StartedAt,
                InBytesPerSecond = _connectionStatistics.InBytesPerSecond,
                OutBytesPerSecond = _connectionStatistics.OutBytesPerSecond,
                ConnectionType = _connectionStatistics.ConnectionType,
                Address = _connectionStatistics.Address,
                IsConnected = _connectionStatistics.IsConnected
            });
        }
    }
    
    /// <summary>
    /// Обновить статистику соединения
    /// </summary>
    public async Task UpdateConnectionStatisticsAsync(long inBytes, long outBytes, double inBytesPerSecond, 
        double outBytesPerSecond, string connectionType, string address, bool isConnected, CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            _connectionStatistics.InBytesTotal = inBytes;
            _connectionStatistics.OutBytesTotal = outBytes;
            _connectionStatistics.InBytesPerSecond = inBytesPerSecond;
            _connectionStatistics.OutBytesPerSecond = outBytesPerSecond;
            _connectionStatistics.ConnectionType = connectionType;
            _connectionStatistics.Address = address;
            _connectionStatistics.IsConnected = isConnected;
            _connectionStatistics.At = DateTime.UtcNow;
        }
        
        await Task.CompletedTask;
    }
    
    /// <summary>
    /// Сбросить статистику
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            _statistics = new OldDeviceStatistics();
            _connectionStatistics = new ConnectionStatistics { StartedAt = DateTime.UtcNow };
            _trafficSamples.Clear();
        }
        
        await SaveStatisticsAsync(cancellationToken);
        _logger.LogDebug("Reset statistics for device {DeviceId}", _deviceId);
    }
    
    private async Task LoadStatisticsAsync(CancellationToken cancellationToken)
    {
        try
        {
            // В реальной реализации здесь будет загрузка из базы данных
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading statistics for device {DeviceId}: {Message}", _deviceId, ex.Message);
        }
    }
    
    private async Task SaveStatisticsAsync(CancellationToken cancellationToken)
    {
        try
        {
            // В реальной реализации здесь будет сохранение в базу данных
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving statistics for device {DeviceId}: {Message}", _deviceId, ex.Message);
        }
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        // Сохраняем статистику при освобождении ресурсов
        _ = Task.Run(async () => await SaveStatisticsAsync(CancellationToken.None));
        
        _logger.LogTrace("Disposed DeviceStatisticsReference for {DeviceId}", _deviceId);
    }
}