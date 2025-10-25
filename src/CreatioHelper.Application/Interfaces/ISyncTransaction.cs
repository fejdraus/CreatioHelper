namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Database transaction interface for atomic operations
/// </summary>
public interface ISyncTransaction : IDisposable
{
    /// <summary>
    /// Commit transaction changes
    /// </summary>
    Task CommitAsync();
    
    /// <summary>
    /// Rollback transaction changes
    /// </summary>
    Task RollbackAsync();
}