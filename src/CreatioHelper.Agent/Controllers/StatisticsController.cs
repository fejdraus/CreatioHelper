using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;

namespace CreatioHelper.Agent.Controllers;

/// <summary>
/// REST API контроллер для статистики синхронизации (на основе Syncthing stats API)
/// </summary>
[ApiController]
[Route("rest/stats")]
public class StatisticsController : ControllerBase
{
    private readonly IStatisticsCollector _statisticsCollector;
    private readonly ILogger<StatisticsController> _logger;

    public StatisticsController(IStatisticsCollector statisticsCollector, ILogger<StatisticsController> logger)
    {
        _statisticsCollector = statisticsCollector;
        _logger = logger;
    }

    /// <summary>
    /// Получить статистику устройства (аналог GET /rest/stats/device)
    /// </summary>
    [HttpGet("device")]
    public async Task<IActionResult> GetDeviceStatistics([FromQuery] string? deviceId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                // Возвращаем статистику всех устройств
                var allDeviceStats = await _statisticsCollector.GetAllDeviceStatisticsAsync(cancellationToken);
                return Ok(allDeviceStats);
            }
            else
            {
                // Возвращаем статистику конкретного устройства
                var deviceStats = await _statisticsCollector.GetDeviceStatisticsAsync(deviceId, cancellationToken);
                return Ok(deviceStats);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting device statistics for {DeviceId}: {Message}", deviceId, ex.Message);
            return StatusCode(500, new { error = "Internal server error", message = ex.Message });
        }
    }

    /// <summary>
    /// Получить статистику папки (аналог GET /rest/stats/folder)
    /// </summary>
    [HttpGet("folder")]
    public async Task<IActionResult> GetFolderStatistics([FromQuery] string? folderId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(folderId))
            {
                // Возвращаем статистику всех папок
                var allFolderStats = await _statisticsCollector.GetAllFolderStatisticsAsync(cancellationToken);
                return Ok(allFolderStats);
            }
            else
            {
                // Возвращаем статистику конкретной папки
                var folderStats = await _statisticsCollector.GetFolderStatisticsAsync(folderId, cancellationToken);
                return Ok(folderStats);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting folder statistics for {FolderId}: {Message}", folderId, ex.Message);
            return StatusCode(500, new { error = "Internal server error", message = ex.Message });
        }
    }

    /// <summary>
    /// Получить статистику соединений
    /// </summary>
    [HttpGet("connection")]
    public async Task<IActionResult> GetConnectionStatistics([FromQuery] string? deviceId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                // Возвращаем статистику соединений всех устройств
                var allDeviceStats = await _statisticsCollector.GetAllDeviceStatisticsAsync(cancellationToken);
                var connectionStats = new Dictionary<string, object>();
                
                foreach (var kvp in allDeviceStats)
                {
                    var connStats = await _statisticsCollector.GetConnectionStatisticsAsync(kvp.Key, cancellationToken);
                    connectionStats[kvp.Key] = connStats;
                }
                
                return Ok(connectionStats);
            }
            else
            {
                // Возвращаем статистику соединения конкретного устройства
                var connectionStats = await _statisticsCollector.GetConnectionStatisticsAsync(deviceId, cancellationToken);
                return Ok(connectionStats);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting connection statistics for {DeviceId}: {Message}", deviceId, ex.Message);
            return StatusCode(500, new { error = "Internal server error", message = ex.Message });
        }
    }

    /// <summary>
    /// Получить системную статистику
    /// </summary>
    [HttpGet("system")]
    public async Task<IActionResult> GetSystemStatistics(CancellationToken cancellationToken = default)
    {
        try
        {
            var systemStats = await _statisticsCollector.GetSystemStatisticsAsync(cancellationToken);
            return Ok(systemStats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting system statistics: {Message}", ex.Message);
            return StatusCode(500, new { error = "Internal server error", message = ex.Message });
        }
    }

    /// <summary>
    /// Получить статистику производительности
    /// </summary>
    [HttpGet("performance")]
    public async Task<IActionResult> GetPerformanceStatistics(CancellationToken cancellationToken = default)
    {
        try
        {
            var performanceStats = await _statisticsCollector.GetPerformanceStatisticsAsync(cancellationToken);
            return Ok(performanceStats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting performance statistics: {Message}", ex.Message);
            return StatusCode(500, new { error = "Internal server error", message = ex.Message });
        }
    }

    /// <summary>
    /// Получить полную сводку статистики
    /// </summary>
    [HttpGet("summary")]
    public async Task<IActionResult> GetStatisticsSummary(CancellationToken cancellationToken = default)
    {
        try
        {
            var systemStats = await _statisticsCollector.GetSystemStatisticsAsync(cancellationToken);
            var performanceStats = await _statisticsCollector.GetPerformanceStatisticsAsync(cancellationToken);
            var deviceStats = await _statisticsCollector.GetAllDeviceStatisticsAsync(cancellationToken);
            var folderStats = await _statisticsCollector.GetAllFolderStatisticsAsync(cancellationToken);

            var summary = new
            {
                System = systemStats,
                Performance = performanceStats,
                Devices = new
                {
                    Count = deviceStats.Count,
                    Connected = deviceStats.Values.Count(d => d.LastSeen > DateTime.UtcNow.AddMinutes(-5)),
                    TotalTraffic = new
                    {
                        BytesIn = deviceStats.Values.Sum(d => d.BytesReceived),
                        BytesOut = deviceStats.Values.Sum(d => d.BytesSent)
                    }
                },
                Folders = new
                {
                    Count = folderStats.Count,
                    TotalFiles = folderStats.Values.Sum(f => f.TotalFiles),
                    TotalSize = folderStats.Values.Sum(f => f.TotalSize),
                    AverageCompletion = folderStats.Values.Any() ? folderStats.Values.Average(f => f.SyncProgress) : 0.0
                },
                LastUpdated = DateTime.UtcNow
            };

            return Ok(summary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting statistics summary: {Message}", ex.Message);
            return StatusCode(500, new { error = "Internal server error", message = ex.Message });
        }
    }

    /// <summary>
    /// Экспортировать всю статистику в JSON
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> ExportStatistics([FromQuery] DateTime? since = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var exportData = await _statisticsCollector.ExportStatisticsAsync(since, cancellationToken);
            
            return Content(exportData, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error exporting statistics: {Message}", ex.Message);
            return StatusCode(500, new { error = "Internal server error", message = ex.Message });
        }
    }

    /// <summary>
    /// Записать статистику устройства (для тестирования)
    /// </summary>
    [HttpPost("device/{deviceId}/traffic")]
    public async Task<IActionResult> RecordDeviceTraffic(
        string deviceId,
        [FromBody] DeviceTrafficRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _statisticsCollector.RecordDeviceTrafficAsync(deviceId, request.BytesIn, request.BytesOut, cancellationToken);
            
            return Ok(new { success = true, message = "Device traffic recorded" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording device traffic for {DeviceId}: {Message}", deviceId, ex.Message);
            return StatusCode(500, new { error = "Internal server error", message = ex.Message });
        }
    }

    /// <summary>
    /// Записать обработку файла (для тестирования)
    /// </summary>
    [HttpPost("folder/{folderId}/file")]
    public async Task<IActionResult> RecordFileProcessed(
        string folderId,
        [FromBody] FileProcessedRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _statisticsCollector.RecordFileProcessedAsync(folderId, request.FileName, request.Deleted, request.Size, request.Action, cancellationToken);
            
            return Ok(new { success = true, message = "File processing recorded" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording file processing for {FolderId}: {Message}", folderId, ex.Message);
            return StatusCode(500, new { error = "Internal server error", message = ex.Message });
        }
    }

    /// <summary>
    /// Записать метрики производительности (для тестирования)
    /// </summary>
    [HttpPost("performance")]
    public async Task<IActionResult> RecordPerformanceMetrics(
        [FromBody] PerformanceMetricsRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _statisticsCollector.RecordPerformanceMetricsAsync(
                request.FileScanRate,
                request.IndexingRate,
                request.NetworkLatencyMs,
                request.DiskThroughput,
                request.BuffersUsed,
                request.MaxBuffers,
                request.ActiveConnections,
                request.SyncQueueLength,
                cancellationToken);
            
            return Ok(new { success = true, message = "Performance metrics recorded" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error recording performance metrics: {Message}", ex.Message);
            return StatusCode(500, new { error = "Internal server error", message = ex.Message });
        }
    }

    /// <summary>
    /// Сбросить статистику устройства
    /// </summary>
    [HttpDelete("device/{deviceId}")]
    public async Task<IActionResult> ResetDeviceStatistics(string deviceId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _statisticsCollector.ResetDeviceStatisticsAsync(deviceId, cancellationToken);
            
            return Ok(new { success = true, message = $"Device statistics reset for {deviceId}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting device statistics for {DeviceId}: {Message}", deviceId, ex.Message);
            return StatusCode(500, new { error = "Internal server error", message = ex.Message });
        }
    }

    /// <summary>
    /// Сбросить статистику папки
    /// </summary>
    [HttpDelete("folder/{folderId}")]
    public async Task<IActionResult> ResetFolderStatistics(string folderId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _statisticsCollector.ResetFolderStatisticsAsync(folderId, cancellationToken);
            
            return Ok(new { success = true, message = $"Folder statistics reset for {folderId}" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resetting folder statistics for {FolderId}: {Message}", folderId, ex.Message);
            return StatusCode(500, new { error = "Internal server error", message = ex.Message });
        }
    }
}

/// <summary>
/// Модель запроса для записи трафика устройства
/// </summary>
public class DeviceTrafficRequest
{
    /// <summary>
    /// Количество полученных байт
    /// </summary>
    [Range(0, long.MaxValue)]
    public long BytesIn { get; set; }

    /// <summary>
    /// Количество отправленных байт
    /// </summary>
    [Range(0, long.MaxValue)]
    public long BytesOut { get; set; }
}

/// <summary>
/// Модель запроса для записи обработки файла
/// </summary>
public class FileProcessedRequest
{
    /// <summary>
    /// Имя файла
    /// </summary>
    [Required]
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Был ли файл удален
    /// </summary>
    public bool Deleted { get; set; }

    /// <summary>
    /// Размер файла в байтах
    /// </summary>
    [Range(0, long.MaxValue)]
    public long Size { get; set; }

    /// <summary>
    /// Тип операции
    /// </summary>
    [Required]
    public string Action { get; set; } = string.Empty; // Created, Modified, Deleted, Downloaded, Uploaded
}

/// <summary>
/// Модель запроса для записи метрик производительности
/// </summary>
public class PerformanceMetricsRequest
{
    /// <summary>
    /// Скорость сканирования файлов (файлов/сек)
    /// </summary>
    [Range(0, double.MaxValue)]
    public double FileScanRate { get; set; }

    /// <summary>
    /// Скорость индексации (файлов/сек)
    /// </summary>
    [Range(0, double.MaxValue)]
    public double IndexingRate { get; set; }

    /// <summary>
    /// Задержка сети в миллисекундах
    /// </summary>
    [Range(0, double.MaxValue)]
    public double NetworkLatencyMs { get; set; }

    /// <summary>
    /// Пропускная способность диска (байт/сек)
    /// </summary>
    [Range(0, double.MaxValue)]
    public double DiskThroughput { get; set; }

    /// <summary>
    /// Количество используемых буферов
    /// </summary>
    [Range(0, long.MaxValue)]
    public long BuffersUsed { get; set; }

    /// <summary>
    /// Максимальное количество буферов
    /// </summary>
    [Range(1, long.MaxValue)]
    public long MaxBuffers { get; set; }

    /// <summary>
    /// Количество активных подключений
    /// </summary>
    [Range(0, int.MaxValue)]
    public int ActiveConnections { get; set; }

    /// <summary>
    /// Длина очереди синхронизации
    /// </summary>
    [Range(0, int.MaxValue)]
    public int SyncQueueLength { get; set; }
}