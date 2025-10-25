using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Repository for folder configuration - similar to Syncthing's folder config
/// </summary>
public interface IFolderConfigRepository : IDisposable
{
    /// <summary>
    /// Get folder configuration by ID
    /// </summary>
    Task<SyncFolder?> GetAsync(string folderId);
    
    /// <summary>
    /// Get all folder configurations
    /// </summary>
    Task<IEnumerable<SyncFolder>> GetAllAsync();
    
    /// <summary>
    /// Save or update folder configuration
    /// </summary>
    Task UpsertAsync(SyncFolder folder);
    
    /// <summary>
    /// Delete folder configuration
    /// </summary>
    Task DeleteAsync(string folderId);
    
    /// <summary>
    /// Get folders shared with a specific device
    /// </summary>
    Task<IEnumerable<SyncFolder>> GetFoldersSharedWithDeviceAsync(string deviceId);
    
    /// <summary>
    /// Update folder statistics
    /// </summary>
    Task UpdateStatisticsAsync(string folderId, long fileCount, long totalSize, DateTime lastScan);
}