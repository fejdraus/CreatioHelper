namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Repository for global state management - similar to Syncthing's global state tracking
/// </summary>
public interface IGlobalStateRepository : IDisposable
{
    /// <summary>
    /// Get value by key
    /// </summary>
    Task<string?> GetValueAsync(string key);
    
    /// <summary>
    /// Set value by key
    /// </summary>
    Task SetValueAsync(string key, string value);
    
    /// <summary>
    /// Delete value by key
    /// </summary>
    Task DeleteValueAsync(string key);
    
    /// <summary>
    /// Get all values with key prefix
    /// </summary>
    Task<Dictionary<string, string>> GetValuesByPrefixAsync(string keyPrefix);
    
    /// <summary>
    /// Increment counter and return new value
    /// </summary>
    Task<long> IncrementCounterAsync(string key, long increment = 1);
    
    /// <summary>
    /// Get database schema version
    /// </summary>
    Task<int> GetSchemaVersionAsync();
    
    /// <summary>
    /// Set database schema version
    /// </summary>
    Task SetSchemaVersionAsync(int version);
}