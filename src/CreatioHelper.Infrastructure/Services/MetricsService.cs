using System.Collections.Concurrent;
using System.Diagnostics;
using CreatioHelper.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services;

public class MetricsService : IMetricsService
{
    private readonly ILogger<MetricsService> _logger;
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentDictionary<string, List<double>> _durations = new();
    private readonly ConcurrentDictionary<string, double> _gauges = new();

    public MetricsService(ILogger<MetricsService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<T> MeasureAsync<T>(string operationName, Func<Task<T>> operation, Dictionary<string, string>? tags = null)
    {
        if (string.IsNullOrEmpty(operationName))
            throw new ArgumentException("Operation name cannot be null or empty", nameof(operationName));

        var stopwatch = Stopwatch.StartNew();
        var tagsString = tags != null ? string.Join(",", tags.Select(kv => $"{kv.Key}={kv.Value}")) : "";
        var metricKey = string.IsNullOrEmpty(tagsString) ? operationName : $"{operationName}[{tagsString}]";

        try
        {
            var result = await operation();
            stopwatch.Stop();
            
            RecordDuration(metricKey, stopwatch.Elapsed, tags);
            IncrementCounter($"{operationName}_success", tags);
            
            _logger.LogDebug("Operation {OperationName} completed in {Duration}ms", 
                operationName, stopwatch.ElapsedMilliseconds);
            
            return result;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            
            RecordDuration($"{metricKey}_error", stopwatch.Elapsed, tags);
            IncrementCounter($"{operationName}_error", tags);
            
            _logger.LogWarning(ex, "Operation {OperationName} failed after {Duration}ms", 
                operationName, stopwatch.ElapsedMilliseconds);
            
            throw;
        }
    }

    public void IncrementCounter(string counterName, Dictionary<string, string>? tags = null)
    {
        if (string.IsNullOrEmpty(counterName))
            throw new ArgumentException("Counter name cannot be null or empty", nameof(counterName));

        var key = BuildMetricKey(counterName, tags);
        _counters.AddOrUpdate(key, 1, (_, current) => current + 1);
    }

    public void RecordDuration(string metricName, TimeSpan duration, Dictionary<string, string>? tags = null)
    {
        if (string.IsNullOrEmpty(metricName))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(metricName));

        var key = BuildMetricKey(metricName, tags);
        _durations.AddOrUpdate(key, 
            new List<double> { duration.TotalMilliseconds },
            (_, current) => 
            {
                lock (current)
                {
                    current.Add(duration.TotalMilliseconds);
                    // Ограничиваем количество записей для экономии памяти
                    if (current.Count > 1000)
                    {
                        current.RemoveRange(0, 500);
                    }
                    return current;
                }
            });
    }

    public void SetGauge(string gaugeName, double value, Dictionary<string, string>? tags = null)
    {
        if (string.IsNullOrEmpty(gaugeName))
            throw new ArgumentException("Gauge name cannot be null or empty", nameof(gaugeName));

        var key = BuildMetricKey(gaugeName, tags);
        _gauges.AddOrUpdate(key, value, (_, _) => value);
    }

    public Task<Dictionary<string, object>> GetMetricsAsync()
    {
        var metrics = new Dictionary<string, object>();

        // Счетчики
        var counters = new Dictionary<string, long>();
        foreach (var counter in _counters)
        {
            counters[counter.Key] = counter.Value;
        }
        metrics["counters"] = counters;

        // Длительности со статистикой
        var durations = new Dictionary<string, object>();
        foreach (var duration in _durations)
        {
            lock (duration.Value)
            {
                if (duration.Value.Count > 0)
                {
                    var values = duration.Value.ToArray();
                    durations[duration.Key] = new
                    {
                        count = values.Length,
                        min = values.Min(),
                        max = values.Max(),
                        avg = values.Average(),
                        p50 = CalculatePercentile(values, 50),
                        p95 = CalculatePercentile(values, 95),
                        p99 = CalculatePercentile(values, 99)
                    };
                }
            }
        }
        metrics["durations"] = durations;

        // Gauge метрики
        var gauges = new Dictionary<string, double>();
        foreach (var gauge in _gauges)
        {
            gauges[gauge.Key] = gauge.Value;
        }
        metrics["gauges"] = gauges;

        // Системная информация
        metrics["system"] = new
        {
            timestamp = DateTimeOffset.UtcNow,
            uptime = Environment.TickCount64,
            memory_mb = GC.GetTotalMemory(false) / 1024 / 1024,
            processor_count = Environment.ProcessorCount
        };

        return Task.FromResult(metrics);
    }

    private static string BuildMetricKey(string name, Dictionary<string, string>? tags)
    {
        if (tags == null || tags.Count == 0)
            return name;

        var tagsString = string.Join(",", tags.Select(kv => $"{kv.Key}={kv.Value}"));
        return $"{name}[{tagsString}]";
    }

    private static double CalculatePercentile(double[] values, int percentile)
    {
        if (values.Length == 0) return 0;
        
        Array.Sort(values);
        var index = (percentile / 100.0) * (values.Length - 1);
        
        if (index == (int)index)
        {
            return values[(int)index];
        }
        
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        var weight = index - lower;
        
        return values[lower] * (1 - weight) + values[upper] * weight;
    }
}
