using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Performance;

/// <summary>
/// Сервис детальной диагностики операций для troubleshooting
/// </summary>
public class DiagnosticsService
{
    private readonly ILogger<DiagnosticsService> _logger;
    private readonly Dictionary<string, List<OperationTrace>> _operationHistory = new();
    private readonly object _lock = new();

    public DiagnosticsService(ILogger<DiagnosticsService> logger)
    {
        _logger = logger;
    }

    public DiagnosticContext StartOperation(string operationName, Dictionary<string, object>? context = null)
    {
        var trace = new OperationTrace
        {
            OperationId = Guid.NewGuid(),
            OperationName = operationName,
            StartTime = DateTime.UtcNow,
            Context = context ?? new Dictionary<string, object>(),
            Stopwatch = Stopwatch.StartNew()
        };

        _logger.LogDebug("🔍 Starting operation {OperationName} [{OperationId}]", 
            operationName, trace.OperationId);

        return new DiagnosticContext(this, trace);
    }

    internal void CompleteOperation(OperationTrace trace, bool success, string? errorMessage = null)
    {
        trace.Stopwatch.Stop();
        trace.EndTime = DateTime.UtcNow;
        trace.Success = success;
        trace.ErrorMessage = errorMessage;

        lock (_lock)
        {
            if (!_operationHistory.ContainsKey(trace.OperationName))
                _operationHistory[trace.OperationName] = new List<OperationTrace>();
            
            _operationHistory[trace.OperationName].Add(trace);
            
            // Сохраняем только последние 100 операций каждого типа
            if (_operationHistory[trace.OperationName].Count > 100)
                _operationHistory[trace.OperationName].RemoveAt(0);
        }

        if (success)
        {
            _logger.LogInformation("✅ Operation {OperationName} completed in {DurationMs}ms [{OperationId}]",
                trace.OperationName, trace.Stopwatch.ElapsedMilliseconds, trace.OperationId);
        }
        else
        {
            _logger.LogError("❌ Operation {OperationName} failed after {DurationMs}ms: {Error} [{OperationId}]",
                trace.OperationName, trace.Stopwatch.ElapsedMilliseconds, errorMessage, trace.OperationId);
        }
    }

    public Dictionary<string, object> GetDiagnosticsSummary()
    {
        lock (_lock)
        {
            var summary = new Dictionary<string, object>();
            
            foreach (var operation in _operationHistory)
            {
                var traces = operation.Value;
                var recent = traces.Where(t => t.EndTime > DateTime.UtcNow.AddHours(-1)).ToList();
                
                summary[operation.Key] = new
                {
                    TotalOperations = traces.Count,
                    RecentOperations = recent.Count,
                    SuccessRate = traces.Count > 0 ? (double)traces.Count(t => t.Success) / traces.Count * 100 : 0,
                    AverageDurationMs = traces.Count > 0 ? traces.Average(t => t.Stopwatch.ElapsedMilliseconds) : 0,
                    RecentFailures = recent.Where(t => !t.Success).Select(t => new
                    {
                        t.OperationId,
                        t.StartTime,
                        t.ErrorMessage,
                        DurationMs = t.Stopwatch.ElapsedMilliseconds
                    }).ToList()
                };
            }
            
            return summary;
        }
    }
}

public class OperationTrace
{
    public Guid OperationId { get; set; }
    public string OperationName { get; set; } = string.Empty;
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public Dictionary<string, object> Context { get; set; } = new();
    public Stopwatch Stopwatch { get; set; } = new();
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
}

public class DiagnosticContext : IDisposable
{
    private readonly DiagnosticsService _service;
    private readonly OperationTrace _trace;
    private bool _disposed;

    internal DiagnosticContext(DiagnosticsService service, OperationTrace trace)
    {
        _service = service;
        _trace = trace;
    }

    public void AddContext(string key, object value)
    {
        _trace.Context[key] = value;
    }

    public void MarkSuccess()
    {
        if (!_disposed)
            _service.CompleteOperation(_trace, true);
        _disposed = true;
    }

    public void MarkFailure(string errorMessage)
    {
        if (!_disposed)
            _service.CompleteOperation(_trace, false, errorMessage);
        _disposed = true;
    }

    public void Dispose()
    {
        if (!_disposed)
            _service.CompleteOperation(_trace, true);
        _disposed = true;
    }
}
