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

        var tagsString = tags != null ? string.Join(",", tags.Select(kv => $"{kv.Key}={kv.Value}")) : "";
        var key = string.IsNullOrEmpty(tagsString) ? counterName : $"{counterName}[{tagsString}]";
        
        _counters.AddOrUpdate(key, 1, (_, current) => current + 1);
    }

    public void RecordDuration(string metricName, TimeSpan duration, Dictionary<string, string>? tags = null)
    {
        if (string.IsNullOrEmpty(metricName))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(metricName));

        var tagsString = tags != null ? string.Join(",", tags.Select(kv => $"{kv.Key}={kv.Value}")) : "";
        var key = string.IsNullOrEmpty(tagsString) ? metricName : $"{metricName}[{tagsString}]";
        
        _durations.AddOrUpdate(key, 
            new List<double> { duration.TotalMilliseconds },
            (_, existing) =>
            {
                lock (existing)
                {
                    existing.Add(duration.TotalMilliseconds);
                    if (existing.Count > 1000)
                    {
                        existing.RemoveRange(0, 500);
                    }
                    return existing;
                }
            });
    }

    public void SetGauge(string gaugeName, double value, Dictionary<string, string>? tags = null)
    {
        if (string.IsNullOrEmpty(gaugeName))
            throw new ArgumentException("Gauge name cannot be null or empty", nameof(gaugeName));

        var tagsString = tags != null ? string.Join(",", tags.Select(kv => $"{kv.Key}={kv.Value}")) : "";
        var key = string.IsNullOrEmpty(tagsString) ? gaugeName : $"{gaugeName}[{tagsString}]";
        
        _gauges[key] = value;
    }

    public Task<Dictionary<string, object>> GetMetricsAsync()
    {
        var result = new Dictionary<string, object>();
        if (_counters.Any())
        {
            result["counters"] = _counters.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        }
        if (_durations.Any())
        {
            var durationStats = new Dictionary<string, object>();
            foreach (var kvp in _durations)
            {
                lock (kvp.Value)
                {
                    if (kvp.Value.Any())
                    {
                        durationStats[kvp.Key] = new
                        {
                            kvp.Value.Count,
                            Average = kvp.Value.Average(),
                            Min = kvp.Value.Min(),
                            Max = kvp.Value.Max(),
                            P95 = GetPercentile(kvp.Value, 0.95),
                            P99 = GetPercentile(kvp.Value, 0.99)
                        };
                    }
                }
            }
            result["durations"] = durationStats;
        }
        if (_gauges.Any())
        {
            result["gauges"] = _gauges.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        }

        return Task.FromResult(result);
    }

    private static double GetPercentile(List<double> values, double percentile)
    {
        if (!values.Any()) return 0;
        
        var sorted = values.OrderBy(x => x).ToList();
        var index = (int)Math.Ceiling(sorted.Count * percentile) - 1;
        index = Math.Max(0, Math.Min(sorted.Count - 1, index));
        return sorted[index];
    }
}
