namespace CreatioHelper.Application.Interfaces;
public interface IMetricsService
{
    Task<T> MeasureAsync<T>(string operationName, Func<Task<T>> operation, Dictionary<string, string>? tags = null);
    void IncrementCounter(string counterName, Dictionary<string, string>? tags = null);
    void RecordDuration(string metricName, TimeSpan duration, Dictionary<string, string>? tags = null);
    void SetGauge(string gaugeName, double value, Dictionary<string, string>? tags = null);
    Task<Dictionary<string, object>> GetMetricsAsync();
}
