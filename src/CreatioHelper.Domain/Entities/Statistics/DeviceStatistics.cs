using System.Text.Json.Serialization;

namespace CreatioHelper.Domain.Entities.Statistics;

/// <summary>
/// Syncthing-compatible device statistics structure
/// Exact match to syncthing/lib/stats/device.go DeviceStatistics
/// </summary>
public class SyncthingDeviceStatistics
{
    [JsonPropertyName("lastSeen")]
    public DateTime LastSeen { get; set; }
    
    [JsonPropertyName("lastConnectionDurationS")]
    public double LastConnectionDurationS { get; set; }
}

/// <summary>
/// Extended device statistics (based on Syncthing DeviceStatistics)
/// Tracks connection and activity information for sync devices
/// </summary>
public class DeviceStatistics
{
    /// <summary>
    /// Device ID
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Device name (friendly name)
    /// </summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// Last time this device was seen online
    /// </summary>
    public DateTime LastSeen { get; set; }

    /// <summary>
    /// Duration of the last connection in seconds
    /// </summary>
    public double LastConnectionDurationS { get; set; }

    /// <summary>
    /// Total number of connections made
    /// </summary>
    public long TotalConnections { get; set; }

    /// <summary>
    /// Total time connected in seconds
    /// </summary>
    public double TotalConnectionTimeS { get; set; }

    /// <summary>
    /// Current connection status
    /// </summary>
    public DeviceConnectionStatus Status { get; set; } = DeviceConnectionStatus.Disconnected;

    /// <summary>
    /// Current connection start time (if connected)
    /// </summary>
    public DateTime? CurrentConnectionStart { get; set; }

    /// <summary>
    /// Remote address of the device
    /// </summary>
    public string? RemoteAddress { get; set; }

    /// <summary>
    /// Connection type (TCP, QUIC, Relay, etc.)
    /// </summary>
    public string? ConnectionType { get; set; }

    /// <summary>
    /// Client version information
    /// </summary>
    public string? ClientVersion { get; set; }

    /// <summary>
    /// Total bytes sent to this device
    /// </summary>
    public long BytesSent { get; set; }

    /// <summary>
    /// Total bytes received from this device
    /// </summary>
    public long BytesReceived { get; set; }

    /// <summary>
    /// Number of files sent to this device
    /// </summary>
    public long FilesSent { get; set; }

    /// <summary>
    /// Number of files received from this device
    /// </summary>
    public long FilesReceived { get; set; }

    /// <summary>
    /// Current download progress (0-100)
    /// </summary>
    public double DownloadProgress { get; set; }

    /// <summary>
    /// Current upload progress (0-100)
    /// </summary>
    public double UploadProgress { get; set; }

    /// <summary>
    /// Number of folders shared with this device
    /// </summary>
    public int SharedFolders { get; set; }

    /// <summary>
    /// Last error message (if any)
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Last error time
    /// </summary>
    public DateTime? LastErrorTime { get; set; }

    /// <summary>
    /// Device certificate fingerprint
    /// </summary>
    public string? CertificateFingerprint { get; set; }

    /// <summary>
    /// Compression enabled
    /// </summary>
    public bool CompressionEnabled { get; set; }

    /// <summary>
    /// Paused status
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Get current connection duration (if connected)
    /// </summary>
    public TimeSpan? GetCurrentConnectionDuration()
    {
        if (Status == DeviceConnectionStatus.Connected && CurrentConnectionStart.HasValue)
        {
            return DateTime.UtcNow - CurrentConnectionStart.Value;
        }
        return null;
    }

    /// <summary>
    /// Get average connection duration
    /// </summary>
    public double GetAverageConnectionDurationS()
    {
        return TotalConnections > 0 ? TotalConnectionTimeS / TotalConnections : 0;
    }

    /// <summary>
    /// Get transfer rate information
    /// </summary>
    public TransferRates GetTransferRates()
    {
        var currentDuration = GetCurrentConnectionDuration();
        if (currentDuration == null || currentDuration.Value.TotalSeconds == 0)
        {
            return new TransferRates();
        }

        var totalSeconds = currentDuration.Value.TotalSeconds;
        return new TransferRates
        {
            UploadRateBytesPerSecond = (long)(BytesSent / totalSeconds),
            DownloadRateBytesPerSecond = (long)(BytesReceived / totalSeconds)
        };
    }

    /// <summary>
    /// Update connection statistics when device connects
    /// </summary>
    public void OnConnected(DateTime connectionTime, string? remoteAddress = null, string? connectionType = null)
    {
        Status = DeviceConnectionStatus.Connected;
        CurrentConnectionStart = connectionTime;
        LastSeen = connectionTime;
        RemoteAddress = remoteAddress;
        ConnectionType = connectionType;
        TotalConnections++;
        LastError = null;
        LastErrorTime = null;
    }

    /// <summary>
    /// Update connection statistics when device disconnects
    /// </summary>
    public void OnDisconnected(DateTime disconnectionTime)
    {
        if (Status == DeviceConnectionStatus.Connected && CurrentConnectionStart.HasValue)
        {
            var connectionDuration = disconnectionTime - CurrentConnectionStart.Value;
            LastConnectionDurationS = connectionDuration.TotalSeconds;
            TotalConnectionTimeS += connectionDuration.TotalSeconds;
        }

        Status = DeviceConnectionStatus.Disconnected;
        CurrentConnectionStart = null;
        DownloadProgress = 0;
        UploadProgress = 0;
    }

    /// <summary>
    /// Record error for this device
    /// </summary>
    public void OnError(string error, DateTime errorTime)
    {
        LastError = error;
        LastErrorTime = errorTime;
    }

    /// <summary>
    /// Update transfer statistics
    /// </summary>
    public void UpdateTransferStats(long bytesSent = 0, long bytesReceived = 0, 
        long filesSent = 0, long filesReceived = 0)
    {
        BytesSent += bytesSent;
        BytesReceived += bytesReceived;
        FilesSent += filesSent;
        FilesReceived += filesReceived;
        LastSeen = DateTime.UtcNow;
    }
    
    /// <summary>
    /// Convert to Syncthing-compatible format
    /// </summary>
    public SyncthingDeviceStatistics ToSyncthingFormat()
    {
        return new SyncthingDeviceStatistics
        {
            LastSeen = LastSeen,
            LastConnectionDurationS = LastConnectionDurationS
        };
    }
}

/// <summary>
/// Device connection status
/// </summary>
public enum DeviceConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Error,
    Paused
}

/// <summary>
/// Transfer rate information
/// </summary>
public class TransferRates
{
    public long UploadRateBytesPerSecond { get; set; }
    public long DownloadRateBytesPerSecond { get; set; }

    /// <summary>
    /// Get formatted upload rate
    /// </summary>
    public string GetFormattedUploadRate()
    {
        return FormatBytesPerSecond(UploadRateBytesPerSecond);
    }

    /// <summary>
    /// Get formatted download rate
    /// </summary>
    public string GetFormattedDownloadRate()
    {
        return FormatBytesPerSecond(DownloadRateBytesPerSecond);
    }

    private static string FormatBytesPerSecond(long bytesPerSecond)
    {
        if (bytesPerSecond >= 1_000_000_000)
            return $"{bytesPerSecond / 1_000_000_000.0:F1} GB/s";
        if (bytesPerSecond >= 1_000_000)
            return $"{bytesPerSecond / 1_000_000.0:F1} MB/s";
        if (bytesPerSecond >= 1_000)
            return $"{bytesPerSecond / 1_000.0:F1} KB/s";
        return $"{bytesPerSecond} B/s";
    }
}