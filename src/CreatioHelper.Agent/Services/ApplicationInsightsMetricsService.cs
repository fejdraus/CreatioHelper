using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using System.Diagnostics;

namespace CreatioHelper.Agent.Services;

public class ApplicationInsightsMetricsService : IMetricsService
{
    private readonly TelemetryClient _telemetryClient;
    private readonly IMetricsService _baseMetricsService;
    private readonly ILogger<ApplicationInsightsMetricsService> _logger;
    private readonly bool _applicationInsightsEnabled;

    public ApplicationInsightsMetricsService(
        TelemetryClient telemetryClient,
        IMetricsService baseMetricsService,
        IConfiguration configuration,
        ILogger<ApplicationInsightsMetricsService> logger)
    {
        _telemetryClient = telemetryClient ?? throw new ArgumentNullException(nameof(telemetryClient));
        _baseMetricsService = baseMetricsService ?? throw new ArgumentNullException(nameof(baseMetricsService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        _applicationInsightsEnabled = !string.IsNullOrEmpty(configuration["ApplicationInsights:ConnectionString"]) ||
                                     !string.IsNullOrEmpty(configuration["ApplicationInsights:InstrumentationKey"]);
    }

    public async Task<T> MeasureAsync<T>(string operationName, Func<Task<T>> operation)
    {
        return await MeasureAsync(operationName, operation, null);
    }

    public async Task<T> MeasureAsync<T>(string operationName, Func<Task<T>> operation, Dictionary<string, string>? tags = null)
    {
        // Всегда используем базовый сервис для локальных метрик
        var result = await _baseMetricsService.MeasureAsync(operationName, operation, tags);

        // Отправляем в Application Insights если настроен
        if (_applicationInsightsEnabled)
        {
            try
            {
                using var telemetryOperation = _telemetryClient.StartOperation<DependencyTelemetry>(operationName);
                telemetryOperation.Telemetry.Type = "CreatioHelper";
                telemetryOperation.Telemetry.Target = Environment.MachineName;
                
                if (tags != null)
                {
                    foreach (var tag in tags)
                    {
                        telemetryOperation.Telemetry.Properties[tag.Key] = tag.Value;
                    }
                }

                // Результат уже получен из базового сервиса, просто завершаем телеметрию
                telemetryOperation.Telemetry.Success = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send telemetry to Application Insights for operation {OperationName}", operationName);
            }
        }

        return result;
    }

    public void Measure(string operationName, Action operation)
    {
        Measure(operationName, operation, null);
    }

    public void Measure(string operationName, Action operation, Dictionary<string, string>? tags = null)
    {
        _baseMetricsService.Measure(operationName, operation, tags);

        if (_applicationInsightsEnabled)
        {
            try
            {
                var stopwatch = Stopwatch.StartNew();
                operation();
                stopwatch.Stop();

                var telemetry = new EventTelemetry(operationName);
                telemetry.Metrics["duration_ms"] = stopwatch.ElapsedMilliseconds;
                
                if (tags != null)
                {
                    foreach (var tag in tags)
                    {
                        telemetry.Properties[tag.Key] = tag.Value;
                    }
                }

                _telemetryClient.TrackEvent(telemetry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send telemetry to Application Insights for operation {OperationName}", operationName);
            }
        }
    }

    public T Measure<T>(string operationName, Func<T> operation)
    {
        return Measure(operationName, operation, null);
    }

    public T Measure<T>(string operationName, Func<T> operation, Dictionary<string, string>? tags = null)
    {
        var result = _baseMetricsService.Measure(operationName, operation, tags);

        if (_applicationInsightsEnabled)
        {
            try
            {
                var telemetry = new EventTelemetry($"{operationName}_sync");
                if (tags != null)
                {
                    foreach (var tag in tags)
                    {
                        telemetry.Properties[tag.Key] = tag.Value;
                    }
                }
                _telemetryClient.TrackEvent(telemetry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send telemetry to Application Insights for operation {OperationName}", operationName);
            }
        }

        return result;
    }

    public void IncrementCounter(string counterName, Dictionary<string, string>? tags = null)
    {
        _baseMetricsService.IncrementCounter(counterName, tags);

        if (_applicationInsightsEnabled)
        {
            try
            {
                var telemetry = new EventTelemetry($"counter_{counterName}");
                if (tags != null)
                {
                    foreach (var tag in tags)
                    {
                        telemetry.Properties[tag.Key] = tag.Value;
                    }
                }
                _telemetryClient.TrackEvent(telemetry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send counter telemetry to Application Insights for {CounterName}", counterName);
            }
        }
    }

    public void RecordHistogram(string metricName, double value, Dictionary<string, string>? tags = null)
    {
        _baseMetricsService.RecordHistogram(metricName, value, tags);

        if (_applicationInsightsEnabled)
        {
            try
            {
                var telemetry = new EventTelemetry($"histogram_{metricName}");
                telemetry.Metrics["value"] = value;
                
                if (tags != null)
                {
                    foreach (var tag in tags)
                    {
                        telemetry.Properties[tag.Key] = tag.Value;
                    }
                }
                
                _telemetryClient.TrackEvent(telemetry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send histogram telemetry to Application Insights for {MetricName}", metricName);
            }
        }
    }

    public void SetGauge(string gaugeName, double value, Dictionary<string, string>? tags = null)
    {
        _baseMetricsService.SetGauge(gaugeName, value, tags);

        if (_applicationInsightsEnabled)
        {
            try
            {
                var telemetry = new EventTelemetry($"gauge_{gaugeName}");
                telemetry.Metrics["value"] = value;
                
                if (tags != null)
                {
                    foreach (var tag in tags)
                    {
                        telemetry.Properties[tag.Key] = tag.Value;
                    }
                }
                
                _telemetryClient.TrackEvent(telemetry);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send gauge telemetry to Application Insights for {GaugeName}", gaugeName);
            }
        }
    }

    public Task<double> GetAverageAsync(string metricName)
    {
        return _baseMetricsService.GetAverageAsync(metricName);
    }

    public Task<long> GetCounterAsync(string counterName)
    {
        return _baseMetricsService.GetCounterAsync(counterName);
    }

    public Task<double> GetRateAsync(string metricName)
    {
        return _baseMetricsService.GetRateAsync(metricName);
    }

    public Task<Dictionary<string, object>> GetMetricsAsync()
    {
        return _baseMetricsService.GetMetricsAsync();
    }
}
