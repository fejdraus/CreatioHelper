using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Repository for block information storage - Syncthing-compatible block storage system
/// Supports SHA-256 based block identification and deduplication
/// </summary>
public interface IBlockInfoRepository : IDisposable
{
    /// <summary>
    /// Get block metadata by SHA-256 hash
    /// </summary>
    Task<BlockMetadata?> GetAsync(byte[] hash);
    
    /// <summary>
    /// Get blocks for a specific file
    /// </summary>
    Task<IEnumerable<BlockMetadata>> GetByFileAsync(string folderId, string fileName);
    
    /// <summary>
    /// Save or update block metadata
    /// </summary>
    Task SaveAsync(BlockMetadata blockMetadata);
    
    /// <summary>
    /// Delete block by hash
    /// </summary>
    Task DeleteAsync(byte[] hash);
    
    /// <summary>
    /// Delete all blocks for a specific file
    /// </summary>
    Task DeleteByFileAsync(string folderId, string fileName);
    
    /// <summary>
    /// Find duplicate blocks with the same hash
    /// </summary>
    Task<IEnumerable<BlockMetadata>> FindDuplicateBlocksAsync(byte[] hash);
    
    /// <summary>
    /// Update last accessed time for a block (for LRU cache management)
    /// </summary>
    Task UpdateLastAccessedAsync(byte[] hash);
    
    /// <summary>
    /// Get total block count in repository
    /// </summary>
    Task<long> GetTotalBlockCountAsync();
    
    /// <summary>
    /// Get total size of all stored blocks
    /// </summary>
    Task<long> GetTotalBlockSizeAsync();
    
    /// <summary>
    /// Get block by hash (alias for GetAsync)
    /// </summary>
    Task<BlockMetadata?> GetByHashAsync(byte[] hash);
    
    /// <summary>
    /// Get blocks by strong hash for deduplication
    /// </summary>
    Task<IEnumerable<BlockMetadata>> GetBlocksByStrongHashAsync(byte[] hash);
}