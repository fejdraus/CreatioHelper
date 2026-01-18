using Prometheus;

namespace CreatioHelper.Infrastructure.Services.Metrics;

/// <summary>
/// Prometheus metrics for folder synchronization.
/// </summary>
public static class FolderMetrics
{
    private static readonly Gauge FolderState = Prometheus.Metrics.CreateGauge(
        "creatiohelper_folder_state",
        "Folder sync state (0=idle, 1=scanning, 2=syncing, 3=paused, 4=error)",
        new GaugeConfiguration
        {
            LabelNames = new[] { "folder" }
        });

    private static readonly Gauge FolderFiles = Prometheus.Metrics.CreateGauge(
        "creatiohelper_folder_files",
        "Number of files in folder",
        new GaugeConfiguration
        {
            LabelNames = new[] { "folder", "scope" }
        });

    private static readonly Gauge FolderBytes = Prometheus.Metrics.CreateGauge(
        "creatiohelper_folder_bytes",
        "Total bytes in folder",
        new GaugeConfiguration
        {
            LabelNames = new[] { "folder", "scope" }
        });

    private static readonly Counter PullsTotal = Prometheus.Metrics.CreateCounter(
        "creatiohelper_folder_pulls_total",
        "Total file pulls",
        new CounterConfiguration
        {
            LabelNames = new[] { "folder" }
        });

    private static readonly Counter PullSecondsTotal = Prometheus.Metrics.CreateCounter(
        "creatiohelper_folder_pull_seconds_total",
        "Total time spent pulling files",
        new CounterConfiguration
        {
            LabelNames = new[] { "folder" }
        });

    private static readonly Counter PushesTotal = Prometheus.Metrics.CreateCounter(
        "creatiohelper_folder_pushes_total",
        "Total file pushes",
        new CounterConfiguration
        {
            LabelNames = new[] { "folder" }
        });

    private static readonly Counter PushSecondsTotal = Prometheus.Metrics.CreateCounter(
        "creatiohelper_folder_push_seconds_total",
        "Total time spent pushing files",
        new CounterConfiguration
        {
            LabelNames = new[] { "folder" }
        });

    private static readonly Counter BytesTransferred = Prometheus.Metrics.CreateCounter(
        "creatiohelper_folder_bytes_total",
        "Total bytes transferred",
        new CounterConfiguration
        {
            LabelNames = new[] { "folder", "direction" }
        });

    private static readonly Counter ConflictsTotal = Prometheus.Metrics.CreateCounter(
        "creatiohelper_folder_conflicts_total",
        "Total conflicts detected",
        new CounterConfiguration
        {
            LabelNames = new[] { "folder" }
        });

    private static readonly Counter ErrorsTotal = Prometheus.Metrics.CreateCounter(
        "creatiohelper_folder_errors_total",
        "Total sync errors",
        new CounterConfiguration
        {
            LabelNames = new[] { "folder", "error_type" }
        });

    private static readonly Gauge LastScanTime = Prometheus.Metrics.CreateGauge(
        "creatiohelper_folder_last_scan_timestamp_seconds",
        "Timestamp of the last folder scan",
        new GaugeConfiguration
        {
            LabelNames = new[] { "folder" }
        });

    private static readonly Histogram ScanDuration = Prometheus.Metrics.CreateHistogram(
        "creatiohelper_folder_scan_duration_seconds",
        "Duration of folder scans",
        new HistogramConfiguration
        {
            LabelNames = new[] { "folder" },
            Buckets = Histogram.ExponentialBuckets(0.1, 2, 12) // 0.1s to ~400s
        });

    /// <summary>
    /// Folder state values.
    /// </summary>
    public enum State
    {
        Idle = 0,
        Scanning = 1,
        Syncing = 2,
        Paused = 3,
        Error = 4
    }

    /// <summary>
    /// Set the current state of a folder.
    /// </summary>
    public static void SetState(string folder, State state)
    {
        FolderState.WithLabels(folder).Set((int)state);
    }

    /// <summary>
    /// Set the file count for a folder.
    /// </summary>
    public static void SetFileCount(string folder, string scope, long count)
    {
        FolderFiles.WithLabels(folder, scope).Set(count);
    }

    /// <summary>
    /// Set the total bytes for a folder.
    /// </summary>
    public static void SetTotalBytes(string folder, string scope, long bytes)
    {
        FolderBytes.WithLabels(folder, scope).Set(bytes);
    }

    /// <summary>
    /// Record a file pull operation.
    /// </summary>
    public static void RecordPull(string folder, double seconds, long bytes)
    {
        PullsTotal.WithLabels(folder).Inc();
        PullSecondsTotal.WithLabels(folder).Inc(seconds);
        BytesTransferred.WithLabels(folder, "download").Inc(bytes);
    }

    /// <summary>
    /// Record a file push operation.
    /// </summary>
    public static void RecordPush(string folder, double seconds, long bytes)
    {
        PushesTotal.WithLabels(folder).Inc();
        PushSecondsTotal.WithLabels(folder).Inc(seconds);
        BytesTransferred.WithLabels(folder, "upload").Inc(bytes);
    }

    /// <summary>
    /// Record a conflict.
    /// </summary>
    public static void RecordConflict(string folder)
    {
        ConflictsTotal.WithLabels(folder).Inc();
    }

    /// <summary>
    /// Record a sync error.
    /// </summary>
    public static void RecordError(string folder, string errorType)
    {
        ErrorsTotal.WithLabels(folder, errorType).Inc();
    }

    /// <summary>
    /// Record the last scan time.
    /// </summary>
    public static void RecordLastScan(string folder)
    {
        LastScanTime.WithLabels(folder).SetToCurrentTimeUtc();
    }

    /// <summary>
    /// Start timing a folder scan.
    /// </summary>
    public static Prometheus.ITimer StartScan(string folder)
    {
        return ScanDuration.WithLabels(folder).NewTimer();
    }
}
