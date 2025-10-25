using CreatioHelper.Application.Interfaces;
using Microsoft.Data.Sqlite;

namespace CreatioHelper.Infrastructure.Services.Sync.Database;

/// <summary>
/// SQLite transaction wrapper for atomic database operations
/// </summary>
public class SyncTransaction : ISyncTransaction
{
    private readonly SqliteTransaction _transaction;
    private bool _disposed;

    public SyncTransaction(SqliteTransaction transaction)
    {
        _transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
    }

    public async Task CommitAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SyncTransaction));
            
        await _transaction.CommitAsync();
    }

    public async Task RollbackAsync()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SyncTransaction));
            
        await _transaction.RollbackAsync();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _transaction?.Dispose();
            _disposed = true;
        }
    }
}