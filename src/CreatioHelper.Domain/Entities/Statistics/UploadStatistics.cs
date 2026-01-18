using System.Text.Json.Serialization;

namespace CreatioHelper.Domain.Entities.Statistics;

/// <summary>
/// Cumulative upload statistics for sync operations
/// Tracks overall upload performance and delta sync efficiency
/// </summary>
public class UploadStatistics
{
    /// <summary>
    /// Device ID these statistics belong to (optional - null for global stats)
    /// </summary>
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; set; }

    /// <summary>
    /// Folder ID these statistics belong to (optional - null for global stats)
    /// </summary>
    [JsonPropertyName("folderId")]
    public string? FolderId { get; set; }

    /// <summary>
    /// Total number of upload operations
    /// </summary>
    [JsonPropertyName("totalUploads")]
    public long TotalUploads { get; set; }

    /// <summary>
    /// Number of successful upload operations
    /// </summary>
    [JsonPropertyName("successfulUploads")]
    public long SuccessfulUploads { get; set; }

    /// <summary>
    /// Number of failed upload operations
    /// </summary>
    [JsonPropertyName("failedUploads")]
    public long FailedUploads { get; set; }

    /// <summary>
    /// Total bytes uploaded across all operations
    /// </summary>
    [JsonPropertyName("totalBytesUploaded")]
    public long TotalBytesUploaded { get; set; }

    /// <summary>
    /// Total file size that would have been uploaded without delta sync
    /// </summary>
    [JsonPropertyName("totalBytesWithoutDelta")]
    public long TotalBytesWithoutDelta { get; set; }

    /// <summary>
    /// Bytes saved by delta sync (difference between full and delta uploads)
    /// </summary>
    [JsonPropertyName("bytesSavedByDelta")]
    public long BytesSavedByDelta { get; set; }

    /// <summary>
    /// Number of full (non-delta) uploads
    /// </summary>
    [JsonPropertyName("fullUploads")]
    public long FullUploads { get; set; }

    /// <summary>
    /// Number of delta uploads (optimized transfers)
    /// </summary>
    [JsonPropertyName("deltaUploads")]
    public long DeltaUploads { get; set; }

    /// <summary>
    /// Total blocks transferred
    /// </summary>
    [JsonPropertyName("totalBlocksTransferred")]
    public long TotalBlocksTransferred { get; set; }

    /// <summary>
    /// Blocks skipped due to delta sync (already existed on remote)
    /// </summary>
    [JsonPropertyName("blocksSkippedByDelta")]
    public long BlocksSkippedByDelta { get; set; }

    /// <summary>
    /// Total time spent uploading (in milliseconds)
    /// </summary>
    [JsonPropertyName("totalUploadTimeMs")]
    public long TotalUploadTimeMs { get; set; }

    /// <summary>
    /// Fastest upload time (in milliseconds)
    /// </summary>
    [JsonPropertyName("fastestUploadMs")]
    public long FastestUploadMs { get; set; } = long.MaxValue;

    /// <summary>
    /// Slowest upload time (in milliseconds)
    /// </summary>
    [JsonPropertyName("slowestUploadMs")]
    public long SlowestUploadMs { get; set; }

    /// <summary>
    /// First upload timestamp
    /// </summary>
    [JsonPropertyName("firstUploadAt")]
    public DateTime? FirstUploadAt { get; set; }

    /// <summary>
    /// Last upload timestamp
    /// </summary>
    [JsonPropertyName("lastUploadAt")]
    public DateTime? LastUploadAt { get; set; }

    /// <summary>
    /// Last uploaded file name
    /// </summary>
    [JsonPropertyName("lastFileName")]
    public string? LastFileName { get; set; }

    /// <summary>
    /// Last upload error (if any)
    /// </summary>
    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    /// <summary>
    /// Last error timestamp
    /// </summary>
    [JsonPropertyName("lastErrorAt")]
    public DateTime? LastErrorAt { get; set; }

    /// <summary>
    /// Statistics tracking start time
    /// </summary>
    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Success rate (percentage 0-100)
    /// </summary>
    [JsonIgnore]
    public double SuccessRate => TotalUploads > 0 ? (SuccessfulUploads * 100.0 / TotalUploads) : 100.0;

    /// <summary>
    /// Delta upload ratio (percentage 0-100)
    /// </summary>
    [JsonIgnore]
    public double DeltaUploadRatio => SuccessfulUploads > 0 ? (DeltaUploads * 100.0 / SuccessfulUploads) : 0.0;

    /// <summary>
    /// Average upload time in milliseconds
    /// </summary>
    [JsonIgnore]
    public double AverageUploadTimeMs => SuccessfulUploads > 0 ? (double)TotalUploadTimeMs / SuccessfulUploads : 0.0;

    /// <summary>
    /// Average upload speed in bytes per second
    /// </summary>
    [JsonIgnore]
    public double AverageUploadSpeedBytesPerSecond => TotalUploadTimeMs > 0
        ? (TotalBytesUploaded * 1000.0 / TotalUploadTimeMs)
        : 0.0;

    /// <summary>
    /// Delta efficiency (percentage of bytes saved by delta sync)
    /// </summary>
    [JsonIgnore]
    public double DeltaEfficiency => TotalBytesWithoutDelta > 0
        ? (BytesSavedByDelta * 100.0 / TotalBytesWithoutDelta)
        : 0.0;

    /// <summary>
    /// Record a successful upload
    /// </summary>
    public void RecordUpload(
        string fileName,
        long bytesUploaded,
        long totalFileSize,
        int blocksTransferred,
        int blocksSkipped,
        bool isDeltaUpload,
        TimeSpan duration)
    {
        TotalUploads++;
        SuccessfulUploads++;
        TotalBytesUploaded += bytesUploaded;
        TotalBytesWithoutDelta += totalFileSize;
        BytesSavedByDelta += (totalFileSize - bytesUploaded);
        TotalBlocksTransferred += blocksTransferred;
        BlocksSkippedByDelta += blocksSkipped;

        var durationMs = (long)duration.TotalMilliseconds;
        TotalUploadTimeMs += durationMs;

        if (durationMs < FastestUploadMs)
            FastestUploadMs = durationMs;
        if (durationMs > SlowestUploadMs)
            SlowestUploadMs = durationMs;

        if (isDeltaUpload)
            DeltaUploads++;
        else
            FullUploads++;

        var now = DateTime.UtcNow;
        FirstUploadAt ??= now;
        LastUploadAt = now;
        LastFileName = fileName;
    }

    /// <summary>
    /// Record a failed upload
    /// </summary>
    public void RecordFailure(string fileName, string error)
    {
        TotalUploads++;
        FailedUploads++;

        var now = DateTime.UtcNow;
        FirstUploadAt ??= now;
        LastUploadAt = now;
        LastFileName = fileName;
        LastError = error;
        LastErrorAt = now;
    }

    /// <summary>
    /// Reset all statistics
    /// </summary>
    public void Reset()
    {
        TotalUploads = 0;
        SuccessfulUploads = 0;
        FailedUploads = 0;
        TotalBytesUploaded = 0;
        TotalBytesWithoutDelta = 0;
        BytesSavedByDelta = 0;
        FullUploads = 0;
        DeltaUploads = 0;
        TotalBlocksTransferred = 0;
        BlocksSkippedByDelta = 0;
        TotalUploadTimeMs = 0;
        FastestUploadMs = long.MaxValue;
        SlowestUploadMs = 0;
        FirstUploadAt = null;
        LastUploadAt = null;
        LastFileName = null;
        LastError = null;
        LastErrorAt = null;
        StartedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Get formatted total bytes uploaded
    /// </summary>
    public string GetFormattedBytesUploaded() => FormatBytes(TotalBytesUploaded);

    /// <summary>
    /// Get formatted bytes saved by delta
    /// </summary>
    public string GetFormattedBytesSaved() => FormatBytes(BytesSavedByDelta);

    /// <summary>
    /// Get formatted average speed
    /// </summary>
    public string GetFormattedAverageSpeed() => FormatBytesPerSecond(AverageUploadSpeedBytesPerSecond);

    /// <summary>
    /// Get summary statistics as dictionary (for API responses)
    /// </summary>
    public Dictionary<string, object> ToSummary()
    {
        return new Dictionary<string, object>
        {
            ["totalUploads"] = TotalUploads,
            ["successfulUploads"] = SuccessfulUploads,
            ["failedUploads"] = FailedUploads,
            ["successRate"] = Math.Round(SuccessRate, 2),
            ["totalBytesUploaded"] = TotalBytesUploaded,
            ["totalBytesUploadedFormatted"] = GetFormattedBytesUploaded(),
            ["bytesSavedByDelta"] = BytesSavedByDelta,
            ["bytesSavedFormatted"] = GetFormattedBytesSaved(),
            ["deltaEfficiency"] = Math.Round(DeltaEfficiency, 2),
            ["fullUploads"] = FullUploads,
            ["deltaUploads"] = DeltaUploads,
            ["deltaUploadRatio"] = Math.Round(DeltaUploadRatio, 2),
            ["averageUploadTimeMs"] = Math.Round(AverageUploadTimeMs, 2),
            ["averageSpeedFormatted"] = GetFormattedAverageSpeed(),
            ["totalBlocksTransferred"] = TotalBlocksTransferred,
            ["blocksSkippedByDelta"] = BlocksSkippedByDelta,
            ["lastUploadAt"] = (object?)LastUploadAt ?? DBNull.Value,
            ["lastFileName"] = (object?)LastFileName ?? string.Empty
        };
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_000_000_000_000)
            return $"{bytes / 1_000_000_000_000.0:F1} TB";
        if (bytes >= 1_000_000_000)
            return $"{bytes / 1_000_000_000.0:F1} GB";
        if (bytes >= 1_000_000)
            return $"{bytes / 1_000_000.0:F1} MB";
        if (bytes >= 1_000)
            return $"{bytes / 1_000.0:F1} KB";
        return $"{bytes} B";
    }

    private static string FormatBytesPerSecond(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1_000_000_000)
            return $"{bytesPerSecond / 1_000_000_000.0:F1} GB/s";
        if (bytesPerSecond >= 1_000_000)
            return $"{bytesPerSecond / 1_000_000.0:F1} MB/s";
        if (bytesPerSecond >= 1_000)
            return $"{bytesPerSecond / 1_000.0:F1} KB/s";
        return $"{bytesPerSecond:F1} B/s";
    }
}
