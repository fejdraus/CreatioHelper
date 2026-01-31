using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Main interface for sync database operations - inspired by Syncthing's database layer
/// </summary>
public interface ISyncDatabase : IDisposable
{
    /// <summary>
    /// Initialize database with schema migrations
    /// </summary>
    Task InitializeAsync();
    
    /// <summary>
    /// File metadata repository
    /// </summary>
    IFileMetadataRepository FileMetadata { get; }
    
    /// <summary>
    /// Block information repository
    /// </summary>
    IBlockInfoRepository BlockInfo { get; }

    /// <summary>
    /// Global state repository for sequence numbers and vector clocks
    /// </summary>
    IGlobalStateRepository GlobalState { get; }

    /// <summary>
    /// Event log repository for persisting sync events
    /// </summary>
    IEventLogRepository EventLog { get; }

    /// <summary>
    /// Begin database transaction
    /// </summary>
    Task<ISyncTransaction> BeginTransactionAsync();
    
    /// <summary>
    /// Compact database (similar to Syncthing's compaction)
    /// </summary>
    Task CompactAsync();

    /// <summary>
    /// Get database size in bytes using PRAGMA page_count * page_size
    /// </summary>
    Task<long> GetDatabaseSizeAsync();
}