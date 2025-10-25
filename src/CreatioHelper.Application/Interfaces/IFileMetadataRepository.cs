using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Repository for file metadata operations - similar to Syncthing's FileInfoTruncated
/// </summary>
public interface IFileMetadataRepository : IDisposable
{
    /// <summary>
    /// Get file metadata by folder and name
    /// </summary>
    Task<FileMetadata?> GetAsync(string folderId, string fileName);
    
    /// <summary>
    /// Get all file metadata for folder
    /// </summary>
    Task<IEnumerable<FileMetadata>> GetAllAsync(string folderId);
    
    /// <summary>
    /// Save or update file metadata
    /// </summary>
    Task UpsertAsync(FileMetadata metadata);
    
    /// <summary>
    /// Delete file metadata
    /// </summary>
    Task DeleteAsync(string folderId, string fileName);
    
    /// <summary>
    /// Get files by sequence number (for sending changes to remote devices)
    /// </summary>
    Task<IEnumerable<FileMetadata>> GetBySequenceAsync(string folderId, long fromSequence, int limit = 1000);
    
    /// <summary>
    /// Get global sequence number for folder
    /// </summary>
    Task<long> GetGlobalSequenceAsync(string folderId);
    
    /// <summary>
    /// Update global sequence number for folder
    /// </summary>
    Task UpdateGlobalSequenceAsync(string folderId, long sequence);
    
    /// <summary>
    /// Get files that need to be downloaded (similar to Syncthing's need list)
    /// </summary>
    Task<IEnumerable<FileMetadata>> GetNeededFilesAsync(string folderId);
    
    /// <summary>
    /// Mark file as locally changed with new sequence number
    /// </summary>
    Task MarkLocallyChangedAsync(string folderId, string fileName, long sequence);
}