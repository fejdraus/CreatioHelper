namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Operation types for priority management
/// </summary>
public enum OperationType
{
    MetadataSync,    // Index updates, folder info
    BlockRequests,   // Block requests  
    FileTransfer,    // Actual file data transfer
    Discovery,       // Device discovery
    Heartbeat        // Keep-alive messages
}

/// <summary>
/// Interface for managing operation priorities and traffic shaping
/// </summary>
public interface IPriorityManager
{
    /// <summary>
    /// Execute an operation with the appropriate priority level
    /// </summary>
    Task<T> ExecuteWithPriorityAsync<T>(string deviceId, OperationType operationType, Func<Task<T>> operation, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Execute an operation with the appropriate priority level (no return value)
    /// </summary>
    Task ExecuteWithPriorityAsync(string deviceId, OperationType operationType, Func<Task> operation, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Get the priority level for an operation type (1-10, 10 = highest)
    /// </summary>
    int GetOperationPriority(OperationType operationType);
    
    /// <summary>
    /// Update traffic shaping configuration
    /// </summary>
    void UpdateConfiguration(Domain.Entities.TrafficShapingConfiguration configuration);
    
    /// <summary>
    /// Get current queue statistics
    /// </summary>
    Task<PriorityStats> GetPriorityStatsAsync(string deviceId);
}

/// <summary>
/// Priority queue statistics for monitoring
/// </summary>
public class PriorityStats
{
    public string DeviceId { get; set; } = string.Empty;
    public Dictionary<OperationType, int> QueueCounts { get; set; } = new();
    public Dictionary<OperationType, long> ProcessedCounts { get; set; } = new();
    public Dictionary<OperationType, double> AverageWaitTimes { get; set; } = new();
    public DateTime LastUpdate { get; set; }
}