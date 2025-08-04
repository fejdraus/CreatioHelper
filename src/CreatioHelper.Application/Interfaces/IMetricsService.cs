namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Service for collecting and sending performance metrics.
/// </summary>
public interface IMetricsService
{
    /// <summary>
    /// Measures execution time of an operation with optional tags.
    /// </summary>
    void Measure(string operationName, Action operation, Dictionary<string, string>? tags = null);
    
    /// <summary>
    /// Measures execution time of an operation that returns a value.
    /// </summary>
    T Measure<T>(string operationName, Func<T> operation, Dictionary<string, string>? tags = null);
    
    /// <summary>
    /// Measures execution time of an asynchronous operation.
    /// </summary>
    Task<T> MeasureAsync<T>(string operationName, Func<Task<T>> operation, Dictionary<string, string>? tags = null);
    
    /// <summary>
    /// Increments an operation counter.
    /// </summary>
    void IncrementCounter(string counterName, Dictionary<string, string>? tags = null);
    
    /// <summary>
    /// Records a value into a histogram metric.
    /// </summary>
    void RecordHistogram(string metricName, double value, Dictionary<string, string>? tags = null);
    
    /// <summary>
    /// Sets a value for a gauge metric.
    /// </summary>
    void SetGauge(string gaugeName, double value, Dictionary<string, string>? tags = null);
    
    /// <summary>
    /// Retrieves the average value of a metric.
    /// </summary>
    Task<double> GetAverageAsync(string metricName);
    
    /// <summary>
    /// Retrieves the value of a counter metric.
    /// </summary>
    Task<long> GetCounterAsync(string counterName);
    
    /// <summary>
    /// Retrieves a metric rate value.
    /// </summary>
    Task<double> GetRateAsync(string metricName);
    
    /// <summary>
    /// Retrieves all metrics collected by the system.
    /// </summary>
    Task<Dictionary<string, object>> GetMetricsAsync();
}
