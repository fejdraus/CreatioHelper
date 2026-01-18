using CreatioHelper.Domain.Entities.Events;
using Prometheus;

namespace CreatioHelper.Infrastructure.Services.Metrics;

/// <summary>
/// Prometheus metrics for sync events.
/// </summary>
public static class SyncMetrics
{
    private static readonly Counter EventsTotal = Prometheus.Metrics.CreateCounter(
        "creatiohelper_sync_events_total",
        "Total number of sync events",
        new CounterConfiguration
        {
            LabelNames = new[] { "event", "state" }
        });

    private static readonly Counter EventsDropped = Prometheus.Metrics.CreateCounter(
        "creatiohelper_sync_events_dropped_total",
        "Total number of dropped sync events",
        new CounterConfiguration
        {
            LabelNames = new[] { "event" }
        });

    private static readonly Histogram EventProcessingDuration = Prometheus.Metrics.CreateHistogram(
        "creatiohelper_sync_event_processing_seconds",
        "Time spent processing events",
        new HistogramConfiguration
        {
            LabelNames = new[] { "event" },
            Buckets = Histogram.ExponentialBuckets(0.001, 2, 10)
        });

    private static readonly Gauge EventQueueSize = Prometheus.Metrics.CreateGauge(
        "creatiohelper_sync_event_queue_size",
        "Current size of the event queue");

    /// <summary>
    /// Record that an event was created.
    /// </summary>
    public static void EventCreated(SyncEventType type)
    {
        EventsTotal.WithLabels(type.ToString(), "created").Inc();
    }

    /// <summary>
    /// Record that an event was delivered to subscribers.
    /// </summary>
    public static void EventDelivered(SyncEventType type)
    {
        EventsTotal.WithLabels(type.ToString(), "delivered").Inc();
    }

    /// <summary>
    /// Record that an event was dropped due to queue overflow.
    /// </summary>
    public static void EventDropped(SyncEventType type)
    {
        EventsDropped.WithLabels(type.ToString()).Inc();
    }

    /// <summary>
    /// Record event processing duration.
    /// </summary>
    public static Prometheus.ITimer StartEventProcessing(SyncEventType type)
    {
        return EventProcessingDuration.WithLabels(type.ToString()).NewTimer();
    }

    /// <summary>
    /// Update the event queue size gauge.
    /// </summary>
    public static void SetQueueSize(int size)
    {
        EventQueueSize.Set(size);
    }
}
