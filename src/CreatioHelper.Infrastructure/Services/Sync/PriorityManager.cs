#pragma warning disable CS8603 // Possible null reference return
#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
using System.Collections.Concurrent;
using System.Diagnostics;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Queued operation with priority information
/// </summary>
public class QueuedOperation<T>
{
    public Func<Task<T>> Operation { get; }
    public int Priority { get; }
    public OperationType OperationType { get; }
    public DateTime QueuedAt { get; }
    public TaskCompletionSource<T> CompletionSource { get; }

    public QueuedOperation(Func<Task<T>> operation, int priority, OperationType operationType)
    {
        Operation = operation;
        Priority = priority;
        OperationType = operationType;
        QueuedAt = DateTime.UtcNow;
        CompletionSource = new TaskCompletionSource<T>();
    }
}

/// <summary>
/// Queued operation without return value
/// </summary>
public class QueuedOperation : QueuedOperation<bool>
{
    public QueuedOperation(Func<Task> operation, int priority, OperationType operationType) 
        : base(async () => { await operation(); return true; }, priority, operationType)
    {
    }
}

/// <summary>
/// Priority queue for operations per device
/// </summary>
public class OperationQueue
{
    private readonly PriorityQueue<QueuedOperation<object>, int> _queue = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly ConcurrentDictionary<OperationType, long> _processedCounts = new();
    private readonly ConcurrentDictionary<OperationType, List<TimeSpan>> _waitTimes = new();
    private readonly ILogger _logger;
    private readonly Timer _processTimer;

    public OperationQueue(ILogger logger)
    {
        _logger = logger;
        _processTimer = new Timer(ProcessQueue, null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }

    public async Task<T> EnqueueAsync<T>(QueuedOperation<T> operation, CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            // Convert to object-based operation for queue
            var objectOperation = new QueuedOperation<object>(
                async () => (object?)await operation.Operation(),
                operation.Priority,
                operation.OperationType
            );

            // Higher priority values should be processed first (max heap behavior)
            _queue.Enqueue(objectOperation, -operation.Priority);
            
            _logger.LogTrace("Queued {OperationType} operation with priority {Priority}", 
                operation.OperationType, operation.Priority);
        }
        finally
        {
            _semaphore.Release();
        }

        return await operation.CompletionSource.Task;
    }

    private async void ProcessQueue(object? state)
    {
        if (!_semaphore.Wait(10)) return; // Don't block if busy
        
        try
        {
            while (_queue.Count > 0)
            {
                var operation = _queue.Dequeue();
                var waitTime = DateTime.UtcNow - operation.QueuedAt;
                
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    var result = await operation.Operation();
                    stopwatch.Stop();
                    
                    operation.CompletionSource.SetResult(result);
                    
                    // Update statistics
                    _processedCounts.AddOrUpdate(operation.OperationType, 1, (_, count) => count + 1);
                    
                    var waitTimesList = _waitTimes.GetOrAdd(operation.OperationType, _ => new List<TimeSpan>());
                    lock (waitTimesList)
                    {
                        waitTimesList.Add(waitTime);
                        if (waitTimesList.Count > 100) // Keep only last 100 entries
                        {
                            waitTimesList.RemoveAt(0);
                        }
                    }
                    
                    _logger.LogTrace("Processed {OperationType} operation in {Ms}ms (waited {WaitMs}ms)", 
                        operation.OperationType, stopwatch.ElapsedMilliseconds, waitTime.TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    operation.CompletionSource.SetException(ex);
                    _logger.LogWarning(ex, "Error processing {OperationType} operation", operation.OperationType);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in queue processing");
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public PriorityStats GetStats(string deviceId)
    {
        var queueCounts = new Dictionary<OperationType, int>();
        var averageWaitTimes = new Dictionary<OperationType, double>();

        // Count items in queue by type
        // Note: This is approximate since PriorityQueue doesn't provide easy enumeration
        
        foreach (var operationType in Enum.GetValues<OperationType>())
        {
            queueCounts[operationType] = 0; // Would need to iterate queue to get exact counts
            
            if (_waitTimes.TryGetValue(operationType, out var waitTimes))
            {
                lock (waitTimes)
                {
                    averageWaitTimes[operationType] = waitTimes.Count > 0 
                        ? waitTimes.Average(wt => wt.TotalMilliseconds) 
                        : 0;
                }
            }
            else
            {
                averageWaitTimes[operationType] = 0;
            }
        }

        return new PriorityStats
        {
            DeviceId = deviceId,
            QueueCounts = queueCounts,
            ProcessedCounts = _processedCounts.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            AverageWaitTimes = averageWaitTimes,
            LastUpdate = DateTime.UtcNow
        };
    }

    public void Dispose()
    {
        _processTimer?.Dispose();
        _semaphore?.Dispose();
    }
}

/// <summary>
/// Priority manager for traffic shaping and operation queuing
/// </summary>
public class PriorityManager : IPriorityManager
{
    private readonly ILogger<PriorityManager> _logger;
    private TrafficShapingConfiguration _configuration;
    private readonly ConcurrentDictionary<string, OperationQueue> _operationQueues = new();

    public PriorityManager(ILogger<PriorityManager> logger, TrafficShapingConfiguration? configuration = null)
    {
        _logger = logger;
        _configuration = configuration ?? new TrafficShapingConfiguration();
    }

    public async Task<T> ExecuteWithPriorityAsync<T>(string deviceId, OperationType operationType, 
        Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        if (!_configuration.Enabled)
        {
            return await operation();
        }
        
        var priority = GetOperationPriority(operationType);
        var queue = _operationQueues.GetOrAdd(deviceId, _ => new OperationQueue(_logger));
        var queuedOperation = new QueuedOperation<T>(operation, priority, operationType);
        
        return await queue.EnqueueAsync(queuedOperation, cancellationToken);
    }

    public async Task ExecuteWithPriorityAsync(string deviceId, OperationType operationType, 
        Func<Task> operation, CancellationToken cancellationToken = default)
    {
        await ExecuteWithPriorityAsync<bool>(deviceId, operationType, async () =>
        {
            await operation();
            return true;
        }, cancellationToken);
    }

    public int GetOperationPriority(OperationType operationType)
    {
        var key = operationType.ToString();
        return _configuration.Priorities.TryGetValue(key, out var priority) ? priority : 5; // Default priority
    }

    public void UpdateConfiguration(TrafficShapingConfiguration configuration)
    {
        _configuration = configuration;
        _logger.LogInformation("Updated traffic shaping configuration: Enabled={Enabled}", 
            configuration.Enabled);
    }

    public Task<PriorityStats> GetPriorityStatsAsync(string deviceId)
    {
        if (_operationQueues.TryGetValue(deviceId, out var queue))
        {
            return Task.FromResult(queue.GetStats(deviceId));
        }

        return Task.FromResult(new PriorityStats
        {
            DeviceId = deviceId,
            QueueCounts = new Dictionary<OperationType, int>(),
            ProcessedCounts = new Dictionary<OperationType, long>(),
            AverageWaitTimes = new Dictionary<OperationType, double>(),
            LastUpdate = DateTime.UtcNow
        });
    }

    public void Dispose()
    {
        foreach (var queue in _operationQueues.Values)
        {
            queue.Dispose();
        }
        _operationQueues.Clear();
    }
}