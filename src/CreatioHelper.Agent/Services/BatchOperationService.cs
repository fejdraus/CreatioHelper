using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Common;
using CreatioHelper.Contracts.Requests;

namespace CreatioHelper.Agent.Services;

public class BatchOperationService
{
    private readonly IMetricsService _metrics;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<BatchOperationService> _logger;

    public BatchOperationService(
        IMetricsService metrics,
        IServiceProvider serviceProvider,
        ILogger<BatchOperationService> logger)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Execute batch operations grouped by servers
    /// </summary>
    public async Task<BatchOperationResult[]> ExecuteBatchAsync(BatchOperation[] operations, CancellationToken cancellationToken = default)
    {
        return await _metrics.MeasureAsync<BatchOperationResult[]>("batch_operations_total", async () =>
        {
            if (operations == null || operations.Length == 0)
            {
                return Array.Empty<BatchOperationResult>();
            }

            _logger.LogInformation("Starting batch execution of {Count} operations", operations.Length);

            // Group operations by server for optimization
            var serverGroups = operations
                .GroupBy(op => op.ServerId ?? "local")
                .ToArray();

            _metrics.SetGauge("batch_servers_count", serverGroups.Length);
            _metrics.SetGauge("batch_operations_count", operations.Length);

            try
            {
                // Parallel execution per server with a limit
                var semaphore = new SemaphoreSlim(Environment.ProcessorCount);
                var tasks = serverGroups.Select(async group =>
                {
                    await semaphore.WaitAsync(cancellationToken);
                    try
                    {
                        return await ExecuteServerBatchAsync(group.Key, group.ToArray(), cancellationToken);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                var serverResults = await Task.WhenAll(tasks);
                var allResults = serverResults.SelectMany(r => r).ToArray();

                var successCount = allResults.Count(r => r.IsSuccess);
                var errorCount = allResults.Length - successCount;

                _metrics.IncrementCounter("batch_operations_success", new() { ["count"] = successCount.ToString() });
                _metrics.IncrementCounter("batch_operations_error", new() { ["count"] = errorCount.ToString() });

                _logger.LogInformation("Batch execution completed: {Success} success, {Error} errors", 
                    successCount, errorCount);

                return allResults;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch execution failed");
                _metrics.IncrementCounter("batch_execution_error");
                throw;
            }
        }, new() { ["operation_count"] = operations.Length.ToString() });
    }

    /// <summary>
    /// Execute operations for a specific server
    /// </summary>
    private async Task<BatchOperationResult[]> ExecuteServerBatchAsync(string serverId, BatchOperation[] operations, CancellationToken cancellationToken)
    {
        return await _metrics.MeasureAsync<BatchOperationResult[]>($"batch_server_operations", async () =>
        {
            _logger.LogDebug("Executing {Count} operations for server {ServerId}", operations.Length, serverId);

            var results = new List<BatchOperationResult>();
            
            using var scope = _serviceProvider.CreateScope();

            foreach (var operation in operations)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    results.Add(new BatchOperationResult
                    {
                        OperationId = operation.Id,
                        IsSuccess = false,
                        ErrorMessage = "Operation was cancelled",
                        ServerId = serverId
                    });
                    continue;
                }

                try
                {
                    var result = await ExecuteSingleOperationAsync(scope, operation, cancellationToken);
                    results.Add(new BatchOperationResult
                    {
                        OperationId = operation.Id,
                        IsSuccess = result.IsSuccess,
                        ErrorMessage = result.ErrorMessage,
                        Data = result.Data,
                        ServerId = serverId,
                        Duration = result.Duration
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to execute operation {OperationId} on server {ServerId}", 
                        operation.Id, serverId);
                    
                    results.Add(new BatchOperationResult
                    {
                        OperationId = operation.Id,
                        IsSuccess = false,
                        ErrorMessage = ex.Message,
                        ServerId = serverId
                    });
                }
            }

            return results.ToArray();
        }, new() { ["server_id"] = serverId, ["operation_count"] = operations.Length.ToString() });
    }

    /// <summary>
    /// Execute a single operation
    /// </summary>
    private async Task<OperationResult> ExecuteSingleOperationAsync(IServiceScope scope, BatchOperation operation, CancellationToken cancellationToken)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var result = operation.Type switch
            {
                BatchOperationType.IisStart => await ExecuteIisOperation(scope, operation, "start", cancellationToken),
                BatchOperationType.IisStop => await ExecuteIisOperation(scope, operation, "stop", cancellationToken),
                BatchOperationType.FileSync => await ExecuteFileSyncOperation(scope, operation, cancellationToken),
                BatchOperationType.HealthCheck => await ExecuteHealthCheckOperation(scope, operation, cancellationToken),
                _ => Result.Failure($"Unknown operation type: {operation.Type}")
            };

            stopwatch.Stop();

            return new OperationResult
            {
                IsSuccess = result.IsSuccess,
                ErrorMessage = result.ErrorMessage,
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new OperationResult
            {
                IsSuccess = false,
                ErrorMessage = ex.Message,
                Duration = stopwatch.Elapsed
            };
        }
    }

    private async Task<Result> ExecuteIisOperation(IServiceScope scope, BatchOperation operation, string action, CancellationToken cancellationToken)
    {
        var iisManager = scope.ServiceProvider.GetRequiredService<IRemoteIisManager>();
        
        return action switch
        {
            "start" when operation.Parameters.ContainsKey("poolName") => 
                await iisManager.StartAppPoolAsync(operation.Parameters["poolName"], cancellationToken),
            "stop" when operation.Parameters.ContainsKey("poolName") => 
                await iisManager.StopAppPoolAsync(operation.Parameters["poolName"], cancellationToken),
            "start" when operation.Parameters.ContainsKey("siteName") => 
                await iisManager.StartWebsiteAsync(operation.Parameters["siteName"], cancellationToken),
            "stop" when operation.Parameters.ContainsKey("siteName") => 
                await iisManager.StopWebsiteAsync(operation.Parameters["siteName"], cancellationToken),
            _ => Result.Failure($"Invalid IIS operation parameters for action {action}")
        };
    }

    private async Task<Result> ExecuteFileSyncOperation(IServiceScope scope, BatchOperation operation, CancellationToken cancellationToken)
    {
        // File synchronization implementation
        await Task.Delay(100, cancellationToken); // Stub
        return Result.Success();
    }

    private async Task<Result> ExecuteHealthCheckOperation(IServiceScope scope, BatchOperation operation, CancellationToken cancellationToken)
    {
        // Health check implementation
        await Task.Delay(50, cancellationToken); // Stub
        return Result.Success();
    }
}

public class BatchOperation
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string? ServerId { get; set; }
    public BatchOperationType Type { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new();
    public int Priority { get; set; } = 0;
}

public enum BatchOperationType
{
    IisStart,
    IisStop,
    FileSync,
    HealthCheck
}

public class BatchOperationResult
{
    public string OperationId { get; set; } = string.Empty;
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public object? Data { get; set; }
    public string? ServerId { get; set; }
    public TimeSpan Duration { get; set; }
}

public class OperationResult
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public object? Data { get; set; }
    public TimeSpan Duration { get; set; }
}
