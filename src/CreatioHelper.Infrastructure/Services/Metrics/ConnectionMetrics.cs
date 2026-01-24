using Prometheus;

namespace CreatioHelper.Infrastructure.Services.Metrics;

/// <summary>
/// Prometheus metrics for device connections.
/// </summary>
public static class ConnectionMetrics
{
    private static readonly Gauge ActiveConnections = Prometheus.Metrics.CreateGauge(
        "creatiohelper_connections_active",
        "Active device connections",
        new GaugeConfiguration
        {
            LabelNames = new[] { "device" }
        });

    private static readonly Gauge TotalConnections = Prometheus.Metrics.CreateGauge(
        "creatiohelper_connections_total",
        "Total number of active connections");

    private static readonly Counter ConnectionsEstablished = Prometheus.Metrics.CreateCounter(
        "creatiohelper_connections_established_total",
        "Total connections established",
        new CounterConfiguration
        {
            LabelNames = new[] { "device", "protocol" }
        });

    private static readonly Counter ConnectionsFailed = Prometheus.Metrics.CreateCounter(
        "creatiohelper_connections_failed_total",
        "Total connections failed",
        new CounterConfiguration
        {
            LabelNames = new[] { "device", "reason" }
        });

    private static readonly Counter ConnectionsClosed = Prometheus.Metrics.CreateCounter(
        "creatiohelper_connections_closed_total",
        "Total connections closed",
        new CounterConfiguration
        {
            LabelNames = new[] { "device", "reason" }
        });

    private static readonly Counter ConnectionStateTransitions = Prometheus.Metrics.CreateCounter(
        "creatiohelper_connection_state_transitions_total",
        "Total connection state transitions",
        new CounterConfiguration
        {
            LabelNames = new[] { "from_state", "to_state" }
        });

    private static readonly Gauge ConnectionHealthScore = Prometheus.Metrics.CreateGauge(
        "creatiohelper_connection_health_score",
        "Connection health score (0-100)",
        new GaugeConfiguration
        {
            LabelNames = new[] { "device_id" }
        });

    private static readonly Counter BytesSent = Prometheus.Metrics.CreateCounter(
        "creatiohelper_connection_bytes_sent_total",
        "Total bytes sent",
        new CounterConfiguration
        {
            LabelNames = new[] { "device" }
        });

    private static readonly Counter BytesReceived = Prometheus.Metrics.CreateCounter(
        "creatiohelper_connection_bytes_received_total",
        "Total bytes received",
        new CounterConfiguration
        {
            LabelNames = new[] { "device" }
        });

    private static readonly Histogram ConnectionLatency = Prometheus.Metrics.CreateHistogram(
        "creatiohelper_connection_latency_seconds",
        "Connection latency in seconds",
        new HistogramConfiguration
        {
            LabelNames = new[] { "device" },
            Buckets = Histogram.ExponentialBuckets(0.001, 2, 15) // 1ms to ~16s
        });

    private static readonly Gauge ConnectionUptime = Prometheus.Metrics.CreateGauge(
        "creatiohelper_connection_uptime_seconds",
        "Connection uptime in seconds",
        new GaugeConfiguration
        {
            LabelNames = new[] { "device" }
        });

    /// <summary>
    /// Set whether a device is connected.
    /// </summary>
    public static void SetConnected(string deviceId, bool connected)
    {
        ActiveConnections.WithLabels(deviceId).Set(connected ? 1 : 0);
    }

    /// <summary>
    /// Set the total number of active connections.
    /// </summary>
    public static void SetTotalConnections(int count)
    {
        TotalConnections.Set(count);
    }

    /// <summary>
    /// Record a connection establishment.
    /// </summary>
    public static void RecordConnectionEstablished(string deviceId, string protocol)
    {
        ConnectionsEstablished.WithLabels(deviceId, protocol).Inc();
        ActiveConnections.WithLabels(deviceId).Set(1);
    }

    /// <summary>
    /// Record a connection failure.
    /// </summary>
    public static void RecordConnectionFailed(string deviceId, string reason)
    {
        ConnectionsFailed.WithLabels(deviceId, reason).Inc();
    }

    /// <summary>
    /// Record a connection closure.
    /// </summary>
    public static void RecordConnectionClosed(string deviceId, string reason)
    {
        ConnectionsClosed.WithLabels(deviceId, reason).Inc();
        ActiveConnections.WithLabels(deviceId).Set(0);
    }

    /// <summary>
    /// Record bytes sent to a device.
    /// </summary>
    public static void RecordBytesSent(string deviceId, long bytes)
    {
        BytesSent.WithLabels(deviceId).Inc(bytes);
    }

    /// <summary>
    /// Record bytes received from a device.
    /// </summary>
    public static void RecordBytesReceived(string deviceId, long bytes)
    {
        BytesReceived.WithLabels(deviceId).Inc(bytes);
    }

    /// <summary>
    /// Record connection latency.
    /// </summary>
    public static void RecordLatency(string deviceId, double seconds)
    {
        ConnectionLatency.WithLabels(deviceId).Observe(seconds);
    }

    /// <summary>
    /// Update connection uptime.
    /// </summary>
    public static void SetUptime(string deviceId, double seconds)
    {
        ConnectionUptime.WithLabels(deviceId).Set(seconds);
    }

    /// <summary>
    /// Record a connection state transition.
    /// </summary>
    public static void RecordStateTransition(string fromState, string toState)
    {
        ConnectionStateTransitions.WithLabels(fromState, toState).Inc();
    }

    /// <summary>
    /// Update the health score for a connection.
    /// </summary>
    public static void SetHealthScore(string deviceId, double score)
    {
        ConnectionHealthScore.WithLabels(deviceId).Set(score);
    }
}
