using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Parallel block transfer engine for high-performance file synchronization
/// Implements Syncthing-compatible concurrent block requests with adaptive concurrency
/// </summary>
public class ParallelBlockTransfer
{
    private readonly ILogger<ParallelBlockTransfer> _logger;
    private readonly SemaphoreSlim _globalSemaphore;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceSemaphores = new();
    private readonly ConcurrentDictionary<string, BlockTransferStats> _deviceStats = new();
    
    // Configuration
    private const int DefaultMaxConcurrency = 8;
    private const int MaxConcurrencyPerDevice = 16;
    private const int MinConcurrency = 2;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AdaptationInterval = TimeSpan.FromSeconds(10);

    public ParallelBlockTransfer(ILogger<ParallelBlockTransfer> logger, int maxGlobalConcurrency = 32)
    {
        _logger = logger;
        _globalSemaphore = new SemaphoreSlim(maxGlobalConcurrency, maxGlobalConcurrency);
    }

    /// <summary>
    /// Request multiple blocks in parallel from a device
    /// </summary>
    /// <param name="deviceId">Target device ID</param>
    /// <param name="connection">BEP connection to the device</param>
    /// <param name="requests">Block requests to execute</param>
    /// <param name="maxConcurrency">Maximum concurrent requests (null for adaptive)</param>
    /// <returns>Block responses in the same order as requests</returns>
    public async Task<List<BepResponse>> RequestBlocksParallelAsync(
        string deviceId, 
        BepConnection connection,
        List<BepRequest> requests, 
        int? maxConcurrency = null)
    {
        if (requests == null || !requests.Any())
            return new List<BepResponse>();

        var deviceSemaphore = GetOrCreateDeviceSemaphore(deviceId, maxConcurrency ?? DefaultMaxConcurrency);
        var stats = GetOrCreateDeviceStats(deviceId);
        
        _logger.LogInformation("🚀 Starting parallel block transfer: {RequestCount} blocks from device {DeviceId} (concurrency: {Concurrency})",
            requests.Count, deviceId, deviceSemaphore.CurrentCount);

        var stopwatch = Stopwatch.StartNew();
        var results = new ConcurrentDictionary<int, BepResponse>();
        var exceptions = new ConcurrentBag<Exception>();

        try
        {
            // Execute requests in parallel with concurrency control
            var tasks = requests.Select(async (request, index) =>
            {
                await _globalSemaphore.WaitAsync();
                await deviceSemaphore.WaitAsync();
                
                try
                {
                    var requestStopwatch = Stopwatch.StartNew();
                    
                    _logger.LogDebug("📤 Requesting block {Index}/{Total}: {RequestId} for {FileName} (offset: {Offset}, size: {Size})",
                        index + 1, requests.Count, request.Id, request.Name, request.Offset, request.Size);
                    
                    using var cts = new CancellationTokenSource(RequestTimeout);
                    var response = await connection.RequestBlockAsync(request);
                    
                    requestStopwatch.Stop();
                    
                    results[index] = response;
                    
                    // Update statistics
                    stats.RecordRequest(requestStopwatch.Elapsed, request.Size, response.Data?.Length ?? 0);
                    
                    _logger.LogDebug("✅ Received block {Index}/{Total}: {RequestId} in {Elapsed:F2}ms ({Speed:F1} MB/s)",
                        index + 1, requests.Count, request.Id, requestStopwatch.Elapsed.TotalMilliseconds,
                        (response.Data?.Length ?? 0) / 1024.0 / 1024.0 / requestStopwatch.Elapsed.TotalSeconds);
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                    _logger.LogWarning(ex, "❌ Failed to request block {Index}/{Total}: {RequestId} from device {DeviceId}",
                        index + 1, requests.Count, request.Id, deviceId);
                }
                finally
                {
                    deviceSemaphore.Release();
                    _globalSemaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
            
            stopwatch.Stop();

            // Check for failures
            if (exceptions.Any())
            {
                _logger.LogError("⚠️ Parallel block transfer completed with {FailureCount} failures out of {TotalCount} requests",
                    exceptions.Count, requests.Count);
                
                if (exceptions.Count == requests.Count)
                {
                    throw new AggregateException("All block requests failed", exceptions);
                }
            }

            // Create ordered results (filling missing with error responses)
            var orderedResults = new List<BepResponse>();
            for (int i = 0; i < requests.Count; i++)
            {
                if (results.TryGetValue(i, out var response))
                {
                    orderedResults.Add(response);
                }
                else
                {
                    // Create error response for failed request
                    orderedResults.Add(new BepResponse
                    {
                        Id = requests[i].Id,
                        Code = BepErrorCode.Generic,
                        Data = []
                    });
                }
            }

            var totalBytes = orderedResults.Sum(r => r.Data?.Length ?? 0);
            var averageSpeed = totalBytes / 1024.0 / 1024.0 / stopwatch.Elapsed.TotalSeconds;

            _logger.LogInformation("✅ Parallel block transfer completed: {SuccessCount}/{TotalCount} blocks, {TotalBytes:N0} bytes in {Elapsed:F2}s ({Speed:F1} MB/s)",
                results.Count, requests.Count, totalBytes, stopwatch.Elapsed.TotalSeconds, averageSpeed);

            // Update device statistics
            stats.RecordBatch(stopwatch.Elapsed, requests.Count, results.Count, totalBytes);

            // Adapt concurrency based on performance
            await AdaptConcurrencyAsync(deviceId, stats);

            return orderedResults;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Critical error in parallel block transfer from device {DeviceId}", deviceId);
            throw;
        }
    }

    /// <summary>
    /// Request blocks for a complete file with automatic chunking
    /// </summary>
    /// <param name="deviceId">Target device ID</param>
    /// <param name="connection">BEP connection</param>
    /// <param name="folder">Folder ID</param>
    /// <param name="fileName">File name</param>
    /// <param name="fileSize">Total file size</param>
    /// <param name="blockSize">Block size for chunking</param>
    /// <returns>Complete file data</returns>
    public async Task<byte[]> RequestFileAsync(
        string deviceId,
        BepConnection connection,
        string folder,
        string fileName,
        long fileSize,
        int blockSize = 128 * 1024) // 128KB default
    {
        _logger.LogInformation("📁 Requesting complete file: {FileName} ({FileSize:N0} bytes) from device {DeviceId}",
            fileName, fileSize, deviceId);

        var requests = new List<BepRequest>();
        var requestId = Random.Shared.Next();

        // Create block requests
        for (long offset = 0; offset < fileSize; offset += blockSize)
        {
            var currentBlockSize = Math.Min(blockSize, fileSize - offset);
            
            requests.Add(new BepRequest
            {
                Id = requestId++,
                Folder = folder,
                Name = fileName,
                Offset = offset,
                Size = (int)currentBlockSize,
                Hash = [] // Will be filled by caller if available
            });
        }

        _logger.LogDebug("📦 Created {BlockCount} block requests for file {FileName}", requests.Count, fileName);

        // Request all blocks in parallel
        var responses = await RequestBlocksParallelAsync(deviceId, connection, requests);

        // Assemble file from blocks
        var fileData = new byte[fileSize];
        var assembledBytes = 0L;

        for (int i = 0; i < responses.Count; i++)
        {
            var response = responses[i];
            var request = requests[i];
            
            if (response.Code != BepErrorCode.NoError || response.Data == null || response.Data.Length == 0)
            {
                throw new InvalidOperationException($"Failed to receive block {i} for file {fileName}: {response.Code}");
            }

            var blockData = response.Data;
            Array.Copy(blockData, 0, fileData, request.Offset, blockData.Length);
            assembledBytes += blockData.Length;
        }

        _logger.LogInformation("✅ File assembled: {FileName} - {AssembledBytes:N0}/{ExpectedBytes:N0} bytes",
            fileName, assembledBytes, fileSize);

        if (assembledBytes != fileSize)
        {
            throw new InvalidOperationException($"File size mismatch: expected {fileSize}, got {assembledBytes}");
        }

        return fileData;
    }

    /// <summary>
    /// Get transfer statistics for a device
    /// </summary>
    /// <param name="deviceId">Device ID</param>
    /// <returns>Transfer statistics</returns>
    public BlockTransferStats? GetDeviceStats(string deviceId)
    {
        return _deviceStats.TryGetValue(deviceId, out var stats) ? stats : null;
    }

    /// <summary>
    /// Reset statistics for a device
    /// </summary>
    /// <param name="deviceId">Device ID</param>
    public void ResetDeviceStats(string deviceId)
    {
        _deviceStats.TryRemove(deviceId, out _);
        _logger.LogDebug("🔄 Reset statistics for device {DeviceId}", deviceId);
    }

    private SemaphoreSlim GetOrCreateDeviceSemaphore(string deviceId, int maxConcurrency)
    {
        return _deviceSemaphores.GetOrAdd(deviceId, _ => 
        {
            var concurrency = Math.Min(maxConcurrency, MaxConcurrencyPerDevice);
            _logger.LogDebug("🔧 Created semaphore for device {DeviceId} with concurrency {Concurrency}", deviceId, concurrency);
            return new SemaphoreSlim(concurrency, concurrency);
        });
    }

    private BlockTransferStats GetOrCreateDeviceStats(string deviceId)
    {
        return _deviceStats.GetOrAdd(deviceId, _ => new BlockTransferStats());
    }

    private Task AdaptConcurrencyAsync(string deviceId, BlockTransferStats stats)
    {
        // Only adapt if we have enough data
        if (stats.TotalRequests < 10 || DateTime.UtcNow - stats.LastAdaptation < AdaptationInterval)
            return Task.CompletedTask;

        var currentSemaphore = _deviceSemaphores.GetValueOrDefault(deviceId);
        if (currentSemaphore == null) return Task.CompletedTask;

        var currentConcurrency = currentSemaphore.CurrentCount;
        var averageSpeed = stats.AverageSpeedMBps;
        var successRate = stats.SuccessRate;

        // Adaptation logic
        int newConcurrency = currentConcurrency;

        if (successRate > 0.95 && averageSpeed > 1.0) // High success rate and good speed
        {
            newConcurrency = Math.Min(currentConcurrency + 2, MaxConcurrencyPerDevice);
        }
        else if (successRate < 0.8 || averageSpeed < 0.5) // Poor performance
        {
            newConcurrency = Math.Max(currentConcurrency - 1, MinConcurrency);
        }

        if (newConcurrency != currentConcurrency)
        {
            // Create new semaphore with adapted concurrency
            var newSemaphore = new SemaphoreSlim(newConcurrency, newConcurrency);
            _deviceSemaphores[deviceId] = newSemaphore;
            currentSemaphore.Dispose();

            stats.LastAdaptation = DateTime.UtcNow;

            _logger.LogInformation("🎯 Adapted concurrency for device {DeviceId}: {OldConcurrency} → {NewConcurrency} (success rate: {SuccessRate:P1}, speed: {Speed:F1} MB/s)",
                deviceId, currentConcurrency, newConcurrency, successRate, averageSpeed);
        }
        
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _globalSemaphore?.Dispose();
        
        foreach (var semaphore in _deviceSemaphores.Values)
        {
            semaphore.Dispose();
        }
        _deviceSemaphores.Clear();
    }
}

/// <summary>
/// Statistics for block transfer performance
/// </summary>
public class BlockTransferStats
{
    private readonly object _lock = new();
    private readonly Queue<RequestStat> _recentRequests = new();
    private const int MaxRecentRequests = 100;

    public long TotalRequests { get; private set; }
    public long SuccessfulRequests { get; private set; }
    public long TotalBytesTransferred { get; private set; }
    public TimeSpan TotalTransferTime { get; private set; }
    public DateTime LastAdaptation { get; set; } = DateTime.UtcNow;

    public double SuccessRate => TotalRequests > 0 ? (double)SuccessfulRequests / TotalRequests : 0.0;
    
    public double AverageSpeedMBps => TotalTransferTime.TotalSeconds > 0 
        ? TotalBytesTransferred / 1024.0 / 1024.0 / TotalTransferTime.TotalSeconds 
        : 0.0;

    public double RecentAverageSpeedMBps
    {
        get
        {
            lock (_lock)
            {
                if (!_recentRequests.Any()) return 0.0;
                
                var totalBytes = _recentRequests.Sum(r => r.BytesTransferred);
                var totalTime = _recentRequests.Sum(r => r.Duration.TotalSeconds);
                
                return totalTime > 0 ? totalBytes / 1024.0 / 1024.0 / totalTime : 0.0;
            }
        }
    }

    public void RecordRequest(TimeSpan duration, int requestBytes, int responseBytes)
    {
        lock (_lock)
        {
            TotalRequests++;
            if (responseBytes > 0)
            {
                SuccessfulRequests++;
                TotalBytesTransferred += responseBytes;
            }
            TotalTransferTime = TotalTransferTime.Add(duration);

            // Keep recent request stats for adaptive behavior
            _recentRequests.Enqueue(new RequestStat
            {
                Duration = duration,
                BytesTransferred = responseBytes,
                Timestamp = DateTime.UtcNow
            });

            // Remove old stats
            while (_recentRequests.Count > MaxRecentRequests)
            {
                _recentRequests.Dequeue();
            }
        }
    }

    public void RecordBatch(TimeSpan batchDuration, int totalRequests, int successfulRequests, long totalBytes)
    {
        // This is already handled by individual RecordRequest calls
        // But can be used for additional batch-level metrics if needed
    }

    private record RequestStat
    {
        public TimeSpan Duration { get; init; }
        public long BytesTransferred { get; init; }
        public DateTime Timestamp { get; init; }
    }
}