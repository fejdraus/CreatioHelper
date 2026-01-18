using Prometheus;

namespace CreatioHelper.Infrastructure.Services.Metrics;

/// <summary>
/// Prometheus metrics for database operations.
/// </summary>
public static class DatabaseMetrics
{
    private static readonly Gauge DatabaseSize = Prometheus.Metrics.CreateGauge(
        "creatiohelper_database_size_bytes",
        "Database size in bytes");

    private static readonly Gauge DatabaseFreeSpace = Prometheus.Metrics.CreateGauge(
        "creatiohelper_database_free_space_bytes",
        "Free space in database");

    private static readonly Gauge FileMetadataCount = Prometheus.Metrics.CreateGauge(
        "creatiohelper_database_file_metadata_count",
        "Number of file metadata records");

    private static readonly Gauge EventLogCount = Prometheus.Metrics.CreateGauge(
        "creatiohelper_database_event_log_count",
        "Number of event log records");

    private static readonly Counter MaintenanceRuns = Prometheus.Metrics.CreateCounter(
        "creatiohelper_database_maintenance_runs_total",
        "Total maintenance runs");

    private static readonly Histogram MaintenanceDuration = Prometheus.Metrics.CreateHistogram(
        "creatiohelper_database_maintenance_duration_seconds",
        "Duration of maintenance operations",
        new HistogramConfiguration
        {
            Buckets = Histogram.ExponentialBuckets(0.1, 2, 10)
        });

    private static readonly Counter RecordsCleaned = Prometheus.Metrics.CreateCounter(
        "creatiohelper_database_records_cleaned_total",
        "Total records cleaned during maintenance",
        new CounterConfiguration
        {
            LabelNames = new[] { "type" }
        });

    private static readonly Histogram QueryDuration = Prometheus.Metrics.CreateHistogram(
        "creatiohelper_database_query_duration_seconds",
        "Database query duration",
        new HistogramConfiguration
        {
            LabelNames = new[] { "operation" },
            Buckets = Histogram.ExponentialBuckets(0.0001, 2, 15)
        });

    private static readonly Gauge LastMaintenanceTime = Prometheus.Metrics.CreateGauge(
        "creatiohelper_database_last_maintenance_timestamp_seconds",
        "Timestamp of the last maintenance run");

    /// <summary>
    /// Set database size.
    /// </summary>
    public static void SetDatabaseSize(long bytes)
    {
        DatabaseSize.Set(bytes);
    }

    /// <summary>
    /// Set database free space.
    /// </summary>
    public static void SetFreeSpace(long bytes)
    {
        DatabaseFreeSpace.Set(bytes);
    }

    /// <summary>
    /// Set file metadata count.
    /// </summary>
    public static void SetFileMetadataCount(long count)
    {
        FileMetadataCount.Set(count);
    }

    /// <summary>
    /// Set event log count.
    /// </summary>
    public static void SetEventLogCount(long count)
    {
        EventLogCount.Set(count);
    }

    /// <summary>
    /// Record a maintenance run.
    /// </summary>
    public static void RecordMaintenanceRun()
    {
        MaintenanceRuns.Inc();
        LastMaintenanceTime.SetToCurrentTimeUtc();
    }

    /// <summary>
    /// Start timing a maintenance operation.
    /// </summary>
    public static Prometheus.ITimer StartMaintenance()
    {
        return MaintenanceDuration.NewTimer();
    }

    /// <summary>
    /// Record records cleaned during maintenance.
    /// </summary>
    public static void RecordRecordsCleaned(string type, int count)
    {
        RecordsCleaned.WithLabels(type).Inc(count);
    }

    /// <summary>
    /// Start timing a database query.
    /// </summary>
    public static Prometheus.ITimer StartQuery(string operation)
    {
        return QueryDuration.WithLabels(operation).NewTimer();
    }

    /// <summary>
    /// Record a database query duration.
    /// </summary>
    public static void RecordQueryDuration(string operation, double seconds)
    {
        QueryDuration.WithLabels(operation).Observe(seconds);
    }
}
