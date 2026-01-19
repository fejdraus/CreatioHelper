namespace CreatioHelper.Infrastructure.Services.Performance;

/// <summary>
/// Service for limiting and managing concurrent connections.
/// Based on Syncthing's connection limiting from lib/connections/service.go
///
/// Key behaviors:
/// - Limit total concurrent connections
/// - Limit connections per device
/// - Track active connections
/// - Provide connection slots with acquire/release semantics
/// </summary>
public interface IConnectionLimiter
{
    /// <summary>
    /// Try to acquire a connection slot for a device.
    /// </summary>
    /// <param name="deviceId">Device identifier</param>
    /// <returns>Connection slot if acquired, null otherwise</returns>
    IConnectionSlot? TryAcquire(string deviceId);

    /// <summary>
    /// Acquire a connection slot, waiting if necessary.
    /// </summary>
    /// <param name="deviceId">Device identifier</param>
    /// <param name="timeout">Maximum wait time</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Connection slot</returns>
    Task<IConnectionSlot?> AcquireAsync(string deviceId, TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get current connection count for a device.
    /// </summary>
    int GetDeviceConnectionCount(string deviceId);

    /// <summary>
    /// Get total active connection count.
    /// </summary>
    int TotalConnectionCount { get; }

    /// <summary>
    /// Check if a device can accept more connections.
    /// </summary>
    bool CanConnect(string deviceId);

    /// <summary>
    /// Update configuration.
    /// </summary>
    void UpdateConfiguration(ConnectionLimiterConfiguration configuration);

    /// <summary>
    /// Get current statistics.
    /// </summary>
    ConnectionLimiterStatistics GetStatistics();
}

/// <summary>
/// Represents an acquired connection slot that must be disposed when done.
/// </summary>
public interface IConnectionSlot : IDisposable
{
    /// <summary>
    /// Device this slot is for.
    /// </summary>
    string DeviceId { get; }

    /// <summary>
    /// When the slot was acquired.
    /// </summary>
    DateTime AcquiredAt { get; }

    /// <summary>
    /// Whether the slot is still valid.
    /// </summary>
    bool IsValid { get; }
}

/// <summary>
/// Configuration for connection limiting.
/// </summary>
public class ConnectionLimiterConfiguration
{
    /// <summary>
    /// Maximum total concurrent connections (0 = unlimited).
    /// </summary>
    public int MaxTotalConnections { get; set; } = 0;

    /// <summary>
    /// Maximum connections per device (0 = unlimited).
    /// </summary>
    public int MaxConnectionsPerDevice { get; set; } = 1;

    /// <summary>
    /// Connection timeout for acquiring slots.
    /// </summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Per-device overrides for max connections.
    /// </summary>
    public Dictionary<string, int> DeviceOverrides { get; set; } = new();

    /// <summary>
    /// Whether to track connection duration statistics.
    /// </summary>
    public bool TrackStatistics { get; set; } = true;
}

/// <summary>
/// Statistics for connection limiting.
/// </summary>
public class ConnectionLimiterStatistics
{
    /// <summary>
    /// Current active connections.
    /// </summary>
    public int ActiveConnections { get; set; }

    /// <summary>
    /// Peak concurrent connections.
    /// </summary>
    public int PeakConnections { get; set; }

    /// <summary>
    /// Total connections acquired.
    /// </summary>
    public long TotalConnectionsAcquired { get; set; }

    /// <summary>
    /// Total connections rejected (limit exceeded).
    /// </summary>
    public long TotalConnectionsRejected { get; set; }

    /// <summary>
    /// Connections by device.
    /// </summary>
    public Dictionary<string, int> ConnectionsByDevice { get; set; } = new();

    /// <summary>
    /// Average connection duration.
    /// </summary>
    public TimeSpan AverageConnectionDuration { get; set; }

    /// <summary>
    /// Current waiting requests.
    /// </summary>
    public int WaitingRequests { get; set; }
}
