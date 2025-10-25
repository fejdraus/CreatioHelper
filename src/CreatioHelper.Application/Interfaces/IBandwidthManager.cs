namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Interface for bandwidth management and throttling
/// </summary>
public interface IBandwidthManager
{
    /// <summary>
    /// Throttle outgoing data transmission for a specific device
    /// </summary>
    Task ThrottleSendAsync(string deviceId, int bytes, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Throttle incoming data reception for a specific device
    /// </summary>
    Task ThrottleReceiveAsync(string deviceId, int bytes, CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Get current bandwidth statistics for a device
    /// </summary>
    Task<BandwidthStats> GetBandwidthStatsAsync(string deviceId);
    
    /// <summary>
    /// Update bandwidth configuration
    /// </summary>
    void UpdateConfiguration(Domain.Entities.BandwidthConfiguration configuration);
    
    /// <summary>
    /// Check if bandwidth limiting should be applied for a device
    /// </summary>
    bool ShouldApplyBandwidthLimits(string deviceId, bool isLanConnection = false);
}

/// <summary>
/// Bandwidth statistics for monitoring
/// </summary>
public class BandwidthStats
{
    public string DeviceId { get; set; } = string.Empty;
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }
    public double CurrentSendRate { get; set; } // bytes/sec
    public double CurrentReceiveRate { get; set; } // bytes/sec
    public double AverageSendRate { get; set; } // bytes/sec
    public double AverageReceiveRate { get; set; } // bytes/sec
    public DateTime LastUpdate { get; set; }
}