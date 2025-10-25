using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Repository for device information - similar to Syncthing's device tracking
/// </summary>
public interface IDeviceInfoRepository : IDisposable
{
    /// <summary>
    /// Get device by ID
    /// </summary>
    Task<SyncDevice?> GetAsync(string deviceId);
    
    /// <summary>
    /// Get all known devices
    /// </summary>
    Task<IEnumerable<SyncDevice>> GetAllAsync();
    
    /// <summary>
    /// Save or update device information
    /// </summary>
    Task UpsertAsync(SyncDevice device);
    
    /// <summary>
    /// Delete device
    /// </summary>
    Task DeleteAsync(string deviceId);
    
    /// <summary>
    /// Update device last seen timestamp
    /// </summary>
    Task UpdateLastSeenAsync(string deviceId, DateTime lastSeen);
    
    /// <summary>
    /// Get devices sharing a specific folder
    /// </summary>
    Task<IEnumerable<SyncDevice>> GetDevicesForFolderAsync(string folderId);
    
    /// <summary>
    /// Update device statistics
    /// </summary>
    Task UpdateStatisticsAsync(string deviceId, long bytesReceived, long bytesSent, DateTime lastActivity);
}