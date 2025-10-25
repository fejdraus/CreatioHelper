using CreatioHelper.Application.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Database;

/// <summary>
/// SQLite implementation of global state repository for key-value storage
/// </summary>
public class GlobalStateRepository : IGlobalStateRepository
{
    private readonly Func<SqliteConnection> _getConnection;
    private readonly ILogger _logger;

    public GlobalStateRepository(Func<SqliteConnection> getConnection, ILogger logger)
    {
        _getConnection = getConnection;
        _logger = logger;
    }

    public async Task<string?> GetValueAsync(string key)
    {
        const string sql = "SELECT value FROM global_state WHERE key = @key";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@key", key);

        var result = await command.ExecuteScalarAsync();
        return result?.ToString();
    }

    public async Task SetValueAsync(string key, string value)
    {
        const string sql = @"
            INSERT OR REPLACE INTO global_state (key, value, updated_at) 
            VALUES (@key, @value, @updatedAt)";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteValueAsync(string key)
    {
        const string sql = "DELETE FROM global_state WHERE key = @key";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@key", key);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<Dictionary<string, string>> GetValuesByPrefixAsync(string keyPrefix)
    {
        const string sql = "SELECT key, value FROM global_state WHERE key LIKE @keyPrefix";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@keyPrefix", keyPrefix + "%");

        var results = new Dictionary<string, string>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results[reader.GetString(reader.GetOrdinal("key"))] = reader.GetString(reader.GetOrdinal("value"));
        }

        return results;
    }

    public async Task<long> IncrementCounterAsync(string key, long increment = 1)
    {
        // Use the same connection for the entire transaction
        using var connection = _getConnection();
        using var transaction = await connection.BeginTransactionAsync();
        try
        {
            // Get current value
            const string selectSql = "SELECT value FROM global_state WHERE key = @key";
            using var selectCommand = connection.CreateCommand();
            selectCommand.CommandText = selectSql;
            selectCommand.Parameters.AddWithValue("@key", key);
            selectCommand.Transaction = (SqliteTransaction)transaction;
            
            var currentValueObj = await selectCommand.ExecuteScalarAsync();
            var currentValue = currentValueObj == null ? 0 : long.Parse(currentValueObj.ToString()!);
            
            // Increment and update
            var newValue = currentValue + increment;
            const string updateSql = @"
                INSERT OR REPLACE INTO global_state (key, value, updated_at) 
                VALUES (@key, @value, @updatedAt)";
            
            using var updateCommand = connection.CreateCommand();
            updateCommand.CommandText = updateSql;
            updateCommand.Parameters.AddWithValue("@key", key);
            updateCommand.Parameters.AddWithValue("@value", newValue.ToString());
            updateCommand.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("O"));
            updateCommand.Transaction = (SqliteTransaction)transaction;
            
            await updateCommand.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
            
            return newValue;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<int> GetSchemaVersionAsync()
    {
        var version = await GetValueAsync("schema_version");
        return version == null ? 0 : int.Parse(version);
    }

    public async Task SetSchemaVersionAsync(int version)
    {
        await SetValueAsync("schema_version", version.ToString());
    }

    public async Task<bool> TryLockAsync(string lockKey, TimeSpan timeout)
    {
        var lockExpiration = DateTime.UtcNow.Add(timeout);
        var lockValue = $"{Environment.MachineName}:{Environment.ProcessId}:{DateTime.UtcNow:O}";
        
        try
        {
            // Try to acquire lock by setting a value with expiration
            const string sql = @"
                INSERT INTO global_state (key, value, updated_at) 
                SELECT @lockKey, @lockValue, @lockExpiration
                WHERE NOT EXISTS (
                    SELECT 1 FROM global_state 
                    WHERE key = @lockKey AND datetime(value) > datetime('now')
                )";

            using var command = _getConnection().CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@lockKey", $"lock_{lockKey}");
            command.Parameters.AddWithValue("@lockValue", lockValue);
            command.Parameters.AddWithValue("@lockExpiration", lockExpiration.ToString("O"));

            var rowsAffected = await command.ExecuteNonQueryAsync();
            return rowsAffected > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to acquire lock {LockKey}", lockKey);
            return false;
        }
    }

    public async Task ReleaseLockAsync(string lockKey)
    {
        try
        {
            const string sql = "DELETE FROM global_state WHERE key = @lockKey";

            using var command = _getConnection().CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@lockKey", $"lock_{lockKey}");

            await command.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to release lock {LockKey}", lockKey);
        }
    }
    
    public void Dispose()
    {
        // No resources to dispose for this implementation
        _logger.LogDebug("GlobalStateRepository disposed");
    }
}