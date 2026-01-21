using CreatioHelper.Agent.Authorization;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities.Statistics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Concurrent;
using DeviceStatistics = CreatioHelper.Domain.Entities.Statistics.DeviceStatistics;
using FolderStatistics = CreatioHelper.Domain.Entities.Statistics.FolderStatistics;
using SyncthingDeviceStatistics = CreatioHelper.Domain.Entities.Statistics.SyncthingDeviceStatistics;
using SyncthingFolderStatistics = CreatioHelper.Domain.Entities.Statistics.SyncthingFolderStatistics;
using DeviceConnectionStatus = CreatioHelper.Domain.Entities.Statistics.DeviceConnectionStatus;

namespace CreatioHelper.Agent.Controllers;

/// <summary>
/// Statistics controller with 100% Syncthing REST API compatibility
/// Implements all endpoints from syncthing/lib/api/api.go
/// </summary>
[ApiController]
[Route("rest/stats")]
[Authorize(Roles = Roles.ReadRoles)]
public class SyncthingStatisticsController : ControllerBase
{
    private readonly IStatisticsCollector _statisticsCollector;
    private readonly ISyncEngine _syncEngine;
    private readonly ILogger<SyncthingStatisticsController> _logger;
    
    // In-memory storage for statistics (in production this would be database-backed)
    private static readonly ConcurrentDictionary<string, DeviceStatistics> DeviceStats = new();
    private static readonly ConcurrentDictionary<string, FolderStatistics> FolderStats = new();
    
    public SyncthingStatisticsController(
        IStatisticsCollector statisticsCollector,
        ISyncEngine syncEngine,
        ILogger<SyncthingStatisticsController> logger)
    {
        _statisticsCollector = statisticsCollector;
        _syncEngine = syncEngine;
        _logger = logger;
    }
    
    /// <summary>
    /// GET /rest/stats/device - Get device statistics
    /// Exact match to Syncthing's getDeviceStats endpoint
    /// Supports optional ?device= parameter to get single device stats
    /// </summary>
    [HttpGet("device")]
    public async Task<IActionResult> GetDeviceStats([FromQuery] string? device = null)
    {
        try
        {
            var deviceStatistics = await GetDeviceStatisticsAsync();

            // If device parameter specified, return only that device's stats
            if (!string.IsNullOrEmpty(device))
            {
                if (deviceStatistics.TryGetValue(device, out var stats))
                {
                    return Ok(stats);
                }
                // Return empty stats for unknown device
                return Ok(new SyncthingDeviceStatistics());
            }

            return Ok(deviceStatistics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting device statistics");
            return StatusCode(500, "Internal Server Error");
        }
    }
    
    /// <summary>
    /// GET /rest/stats/folder - Get folder statistics
    /// Exact match to Syncthing's getFolderStats endpoint
    /// </summary>
    [HttpGet("folder")]
    public async Task<IActionResult> GetFolderStats()
    {
        try
        {
            var folderStatistics = await GetFolderStatisticsAsync();
            return Ok(folderStatistics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting folder statistics");
            return StatusCode(500, "Internal Server Error");
        }
    }
    
    private async Task<Dictionary<string, SyncthingDeviceStatistics>> GetDeviceStatisticsAsync()
    {
        var result = new Dictionary<string, SyncthingDeviceStatistics>();
        
        // Get current device statistics from sync engine
        var currentStats = await _statisticsCollector.GetDeviceStatisticsAsync();
        
        foreach (var stat in currentStats)
        {
            // Update our in-memory storage
            DeviceStats.AddOrUpdate(stat.Key, stat.Value, (key, existing) => 
            {
                // Merge with existing stats to maintain history
                existing.LastSeen = stat.Value.LastSeen;
                existing.LastConnectionDurationS = stat.Value.LastConnectionDurationS;
                existing.Status = stat.Value.Status;
                existing.RemoteAddress = stat.Value.RemoteAddress;
                existing.ConnectionType = stat.Value.ConnectionType;
                return existing;
            });
            
            // Convert to Syncthing format for API response
            result[stat.Key] = stat.Value.ToSyncthingFormat();
        }
        
        return result;
    }
    
    private async Task<Dictionary<string, SyncthingFolderStatistics>> GetFolderStatisticsAsync()
    {
        var result = new Dictionary<string, SyncthingFolderStatistics>();
        
        // Get current folder statistics from sync engine
        var currentStats = await _statisticsCollector.GetFolderStatisticsAsync();
        
        foreach (var stat in currentStats)
        {
            // Update our in-memory storage
            FolderStats.AddOrUpdate(stat.Key, stat.Value, (key, existing) => 
            {
                // Merge with existing stats
                existing.LastScan = stat.Value.LastScan;
                existing.LastFile = stat.Value.LastFile;
                existing.TotalFiles = stat.Value.TotalFiles;
                existing.TotalSize = stat.Value.TotalSize;
                return existing;
            });
            
            // Convert to Syncthing format for API response
            result[stat.Key] = stat.Value.ToSyncthingFormat();
        }
        
        return result;
    }
    
    /// <summary>
    /// Update device statistics (internal method for sync engine)
    /// </summary>
    public static void UpdateDeviceStatistics(string deviceId, DeviceStatistics stats)
    {
        DeviceStats.AddOrUpdate(deviceId, stats, (key, existing) => stats);
    }
    
    /// <summary>
    /// Update folder statistics (internal method for sync engine)
    /// </summary>
    public static void UpdateFolderStatistics(string folderId, FolderStatistics stats)
    {
        FolderStats.AddOrUpdate(folderId, stats, (key, existing) => stats);
    }
    
    /// <summary>
    /// Record device connection event
    /// </summary>
    public static void RecordDeviceConnection(string deviceId, string? remoteAddress = null, string? connectionType = null)
    {
        DeviceStats.AddOrUpdate(deviceId, 
            deviceId =>
            {
                var newStats = new DeviceStatistics
                {
                    DeviceId = deviceId,
                    LastSeen = DateTime.UtcNow,
                    Status = DeviceConnectionStatus.Connected,
                    RemoteAddress = remoteAddress,
                    ConnectionType = connectionType
                };
                newStats.OnConnected(DateTime.UtcNow, remoteAddress, connectionType);
                return newStats;
            },
            (key, existing) => 
            {
                existing.OnConnected(DateTime.UtcNow, remoteAddress, connectionType);
                return existing;
            });
    }
    
    /// <summary>
    /// Record device disconnection event
    /// </summary>
    public static void RecordDeviceDisconnection(string deviceId)
    {
        DeviceStats.AddOrUpdate(deviceId,
            deviceId =>
            {
                var newStats = new DeviceStatistics
                {
                    DeviceId = deviceId,
                    LastSeen = DateTime.UtcNow,
                    Status = DeviceConnectionStatus.Disconnected
                };
                newStats.OnDisconnected(DateTime.UtcNow);
                return newStats;
            },
            (key, existing) => 
            {
                existing.OnDisconnected(DateTime.UtcNow);
                return existing;
            });
    }
    
    /// <summary>
    /// Record file sync event
    /// </summary>
    public static void RecordFileSynced(string folderId, string fileName, bool wasDeleted = false, long fileSize = 0)
    {
        FolderStats.AddOrUpdate(folderId,
            folderId =>
            {
                var newStats = new FolderStatistics
                {
                    FolderId = folderId,
                    LastScan = DateTime.UtcNow
                };
                newStats.OnFileSynced(fileName, wasDeleted, fileSize, DateTime.UtcNow);
                return newStats;
            },
            (key, existing) => 
            {
                existing.OnFileSynced(fileName, wasDeleted, fileSize, DateTime.UtcNow);
                return existing;
            });
    }
    
    /// <summary>
    /// Record folder scan completion
    /// </summary>
    public static void RecordFolderScanCompleted(string folderId, long totalFiles, long totalSize)
    {
        FolderStats.AddOrUpdate(folderId,
            new FolderStatistics
            {
                FolderId = folderId,
                TotalFiles = totalFiles,
                TotalSize = totalSize,
                LastScan = DateTime.UtcNow.Truncate(TimeSpan.FromSeconds(1))
            },
            (key, existing) => 
            {
                existing.OnScanCompleted(DateTime.UtcNow, TimeSpan.Zero, totalFiles, totalSize);
                return existing;
            });
    }
}

// Extension methods for DeviceStatistics
public static class DeviceStatisticsExtensions
{
    public static DeviceStatistics OnConnected(this DeviceStatistics stats, DateTime connectionTime, string? remoteAddress = null, string? connectionType = null)
    {
        stats.OnConnected(connectionTime, remoteAddress, connectionType);
        return stats;
    }
    
    public static DeviceStatistics OnDisconnected(this DeviceStatistics stats, DateTime disconnectionTime)
    {
        stats.OnDisconnected(disconnectionTime);
        return stats;
    }
}

// Extension methods for FolderStatistics
public static class FolderStatisticsExtensions
{
    public static FolderStatistics OnFileSynced(this FolderStatistics stats, string fileName, bool wasDeleted, long fileSize, DateTime syncTime)
    {
        stats.OnFileSynced(fileName, wasDeleted, fileSize, syncTime);
        return stats;
    }
}