using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
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

    public async Task<T> MeasureAsync<T>(string operationName, Func<Task<T>> operation)
    {
        return await MeasureAsync(operationName, operation, null);
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

    public void Measure(string operationName, Action operation)
    {
        Measure(operationName, operation, null);
    }

    public void Measure(string operationName, Action operation, Dictionary<string, string>? tags = null)
    {
        if (string.IsNullOrEmpty(operationName))
            throw new ArgumentException("Operation name cannot be null or empty", nameof(operationName));

        var stopwatch = Stopwatch.StartNew();
        var metricKey = BuildMetricKey(operationName, tags);

        try
        {
            operation();
            stopwatch.Stop();
            
            RecordDuration(metricKey, stopwatch.Elapsed, tags);
            IncrementCounter($"{operationName}_success", tags);
            
            _logger.LogDebug("Operation {OperationName} completed in {Duration}ms", 
                operationName, stopwatch.ElapsedMilliseconds);
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

    public T Measure<T>(string operationName, Func<T> operation)
    {
        return Measure(operationName, operation, null);
    }

    public T Measure<T>(string operationName, Func<T> operation, Dictionary<string, string>? tags = null)
    {
        if (string.IsNullOrEmpty(operationName))
            throw new ArgumentException("Operation name cannot be null or empty", nameof(operationName));

        var stopwatch = Stopwatch.StartNew();
        var metricKey = BuildMetricKey(operationName, tags);

        try
        {
            var result = operation();
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

    public void RecordHistogram(string metricName, double value, Dictionary<string, string>? tags = null)
    {
        if (string.IsNullOrEmpty(metricName))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(metricName));

        var key = BuildMetricKey(metricName, tags);
        _durations.AddOrUpdate(key, 
            new List<double> { value },
            (_, current) => 
            {
                lock (current)
                {
                    current.Add(value);
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

    public Task<double> GetAverageAsync(string metricName)
    {
        if (_durations.TryGetValue(metricName, out var values))
        {
            lock (values)
            {
                return Task.FromResult(values.Count > 0 ? values.Average() : 0);
            }
        }
        return Task.FromResult(0.0);
    }

    public Task<long> GetCounterAsync(string counterName)
    {
        return Task.FromResult(_counters.TryGetValue(counterName, out var value) ? value : 0);
    }

    public async Task<double> GetRateAsync(string metricName)
    {
        var successCounter = await GetCounterAsync($"{metricName}_success");
        var errorCounter = await GetCounterAsync($"{metricName}_error");
        var total = successCounter + errorCounter;
        
        return total > 0 ? (double)errorCounter / total : 0;
    }

    public Task<Dictionary<string, object>> GetMetricsAsync()
    {
        var metrics = new Dictionary<string, object>();

        // Счетчики
        metrics["counters"] = _counters.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        
        // Gauges
        metrics["gauges"] = _gauges.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        
        // Агрегированные данные по длительности
        var durationStats = new Dictionary<string, object>();
        foreach (var kv in _durations)
        {
            lock (kv.Value)
            {
                if (kv.Value.Count > 0)
                {
                    durationStats[kv.Key] = new
                    {
                        Count = kv.Value.Count,
                        Average = kv.Value.Average(),
                        Min = kv.Value.Min(),
                        Max = kv.Value.Max(),
                        P95 = GetPercentile(kv.Value, 0.95),
                        P99 = GetPercentile(kv.Value, 0.99)
                    };
                }
            }
        }
        metrics["durations"] = durationStats;
        
        return Task.FromResult(metrics);
    }

    private void RecordDuration(string metricName, TimeSpan duration, Dictionary<string, string>? tags = null)
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
                    if (current.Count > 1000)
                    {
                        current.RemoveRange(0, 500);
                    }
                    return current;
                }
            });
    }

    private string BuildMetricKey(string metricName, Dictionary<string, string>? tags = null)
    {
        if (tags == null || tags.Count == 0)
            return metricName;
            
        var tagsString = string.Join(",", tags.Select(kv => $"{kv.Key}={kv.Value}"));
        return $"{metricName}[{tagsString}]";
    }

    private double GetPercentile(List<double> values, double percentile)
    {
        if (values.Count == 0) return 0;
        
        var sorted = values.OrderBy(x => x).ToList();
        var index = (int)Math.Ceiling(sorted.Count * percentile) - 1;
        return sorted[Math.Max(0, Math.Min(index, sorted.Count - 1))];
    }
}
