using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Transfer;

/// <summary>
/// Priority for pull items.
/// Higher priority items are processed first.
/// </summary>
public enum PullPriority
{
    /// <summary>Lowest priority - background sync.</summary>
    Low = 0,

    /// <summary>Normal priority - regular sync.</summary>
    Normal = 50,

    /// <summary>High priority - user requested.</summary>
    High = 100,

    /// <summary>Critical priority - conflict resolution.</summary>
    Critical = 200
}

/// <summary>
/// Represents an item to be pulled (downloaded).
/// </summary>
public class PullItem
{
    /// <summary>
    /// Unique identifier for this pull item.
    /// </summary>
    public string Id { get; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Folder ID containing the file.
    /// </summary>
    public string FolderId { get; set; } = string.Empty;

    /// <summary>
    /// Relative file path.
    /// </summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Total file size in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Block hashes to download.
    /// </summary>
    public List<string> BlockHashes { get; set; } = new();

    /// <summary>
    /// Block size in bytes.
    /// </summary>
    public int BlockSize { get; set; }

    /// <summary>
    /// Devices that have this file.
    /// </summary>
    public List<string> AvailableDevices { get; set; } = new();

    /// <summary>
    /// Priority for this item.
    /// </summary>
    public PullPriority Priority { get; set; } = PullPriority.Normal;

    /// <summary>
    /// When this item was added to the queue.
    /// </summary>
    public DateTime QueuedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Number of retry attempts.
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Maximum retry attempts.
    /// </summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// Last error message if any.
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Progress tracking: blocks completed.
    /// </summary>
    public int BlocksCompleted { get; set; }

    /// <summary>
    /// Progress tracking: bytes completed.
    /// </summary>
    public long BytesCompleted { get; set; }

    /// <summary>
    /// When this item started downloading.
    /// </summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>
    /// Version vector for this file.
    /// </summary>
    public string VersionVector { get; set; } = string.Empty;

    /// <summary>
    /// Modified timestamp of the file.
    /// </summary>
    public DateTime ModifiedTime { get; set; }

    /// <summary>
    /// Gets the completion percentage.
    /// </summary>
    public double CompletionPercent => Size > 0 ? (double)BytesCompleted / Size * 100 : 0;

    /// <summary>
    /// Gets whether this item can be retried.
    /// </summary>
    public bool CanRetry => RetryCount < MaxRetries;
}

/// <summary>
/// State of a pull item in the queue.
/// </summary>
public enum PullItemState
{
    /// <summary>Waiting in queue.</summary>
    Pending,

    /// <summary>Currently downloading.</summary>
    InProgress,

    /// <summary>Download completed.</summary>
    Completed,

    /// <summary>Download failed.</summary>
    Failed,

    /// <summary>Download cancelled.</summary>
    Cancelled
}

/// <summary>
/// Prioritized queue for file downloads with resume support.
/// Compatible with Syncthing's puller implementation.
/// </summary>
public class PullQueue : IDisposable
{
    private readonly ILogger<PullQueue> _logger;
    private readonly PriorityQueue<PullItem, int> _queue;
    private readonly ConcurrentDictionary<string, PullItem> _items;
    private readonly ConcurrentDictionary<string, PullItem> _inProgress;
    private readonly SemaphoreSlim _queueLock;
    private readonly int _maxConcurrentPulls;
    private readonly BandwidthLimiter _bandwidthLimiter;

    private long _totalBytesQueued;
    private long _totalBytesCompleted;
    private long _bytesTransferredInWindow;
    private long _windowStartTicks;
    private int _totalItemsQueued;
    private int _totalItemsCompleted;
    private volatile bool _disposed;

    /// <summary>
    /// Event fired when an item starts downloading.
    /// </summary>
    public event Func<PullItem, Task>? ItemStarted;

    /// <summary>
    /// Event fired when an item completes or fails.
    /// </summary>
    public event Func<PullItem, PullItemState, Task>? ItemCompleted;

    /// <summary>
    /// Event fired when download progress is updated.
    /// </summary>
    public event Func<PullItem, int, long, Task>? ProgressUpdated;

    /// <summary>
    /// Creates a new pull queue.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="maxConcurrentPulls">Maximum concurrent downloads (default: 16).</param>
    /// <param name="maxBytesPerSecond">Maximum download rate in bytes/sec (0 = unlimited).</param>
    public PullQueue(
        ILogger<PullQueue> logger,
        int maxConcurrentPulls = 16,
        long maxBytesPerSecond = 0)
    {
        _logger = logger;
        _maxConcurrentPulls = maxConcurrentPulls;
        _bandwidthLimiter = new BandwidthLimiter(maxBytesPerSecond);
        _queue = new PriorityQueue<PullItem, int>();
        _items = new ConcurrentDictionary<string, PullItem>();
        _inProgress = new ConcurrentDictionary<string, PullItem>();
        _queueLock = new SemaphoreSlim(1, 1);
        _windowStartTicks = Environment.TickCount64;
    }

    /// <summary>
    /// Gets the bandwidth limiter for controlling download rates.
    /// </summary>
    public BandwidthLimiter BandwidthLimiter => _bandwidthLimiter;

    /// <summary>
    /// Gets whether bandwidth limiting is enabled.
    /// </summary>
    public bool IsBandwidthLimited => _bandwidthLimiter.IsLimited;

    /// <summary>
    /// Gets the configured download limit in bytes per second.
    /// </summary>
    public long MaxBytesPerSecond => _bandwidthLimiter.BytesPerSecond;

    /// <summary>
    /// Adds an item to the pull queue.
    /// </summary>
    public async Task EnqueueAsync(PullItem item, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PullQueue));

        await _queueLock.WaitAsync(cancellationToken);
        try
        {
            // Check for duplicates
            if (_items.ContainsKey(item.Id))
            {
                _logger.LogDebug("Item already in queue: {FilePath}", item.FilePath);
                return;
            }

            // Check for same file in different state
            var existingKey = $"{item.FolderId}:{item.FilePath}";
            var existing = _items.Values.FirstOrDefault(i =>
                i.FolderId == item.FolderId && i.FilePath == item.FilePath);

            if (existing != null)
            {
                _logger.LogDebug("File already queued, updating priority: {FilePath}", item.FilePath);
                if (item.Priority > existing.Priority)
                {
                    existing.Priority = item.Priority;
                    // Re-queue with new priority
                    RequeueItem(existing);
                }
                return;
            }

            _items[item.Id] = item;
            _queue.Enqueue(item, GetPriorityValue(item));

            Interlocked.Add(ref _totalBytesQueued, item.Size);
            Interlocked.Increment(ref _totalItemsQueued);

            _logger.LogDebug("Item enqueued: {FilePath}, Priority={Priority}, Size={Size}",
                item.FilePath, item.Priority, item.Size);
        }
        finally
        {
            _queueLock.Release();
        }
    }

    /// <summary>
    /// Tries to dequeue the next item for processing.
    /// </summary>
    public async Task<PullItem?> TryDequeueAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return null;

        // Check concurrent limit
        if (_inProgress.Count >= _maxConcurrentPulls)
        {
            return null;
        }

        await _queueLock.WaitAsync(cancellationToken);
        try
        {
            if (_queue.Count == 0)
            {
                return null;
            }

            var item = _queue.Dequeue();
            item.StartedAt = DateTime.UtcNow;

            _inProgress[item.Id] = item;

            _logger.LogDebug("Item dequeued: {FilePath}", item.FilePath);

            // Fire event
            if (ItemStarted != null)
            {
                await ItemStarted.Invoke(item);
            }

            return item;
        }
        finally
        {
            _queueLock.Release();
        }
    }

    /// <summary>
    /// Reports progress for an in-progress item.
    /// </summary>
    public async Task ReportProgressAsync(
        string itemId,
        int blocksCompleted,
        long bytesCompleted,
        CancellationToken cancellationToken = default)
    {
        if (_inProgress.TryGetValue(itemId, out var item))
        {
            item.BlocksCompleted = blocksCompleted;
            item.BytesCompleted = bytesCompleted;

            if (ProgressUpdated != null)
            {
                await ProgressUpdated.Invoke(item, blocksCompleted, bytesCompleted);
            }
        }
    }

    /// <summary>
    /// Acquires bandwidth for downloading the specified number of bytes.
    /// Blocks until bandwidth is available if rate limiting is enabled.
    /// </summary>
    /// <param name="bytes">Number of bytes to download.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask AcquireBandwidthAsync(long bytes, CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PullQueue));
        await _bandwidthLimiter.AcquireAsync(bytes, cancellationToken);
        Interlocked.Add(ref _bytesTransferredInWindow, bytes);
    }

    /// <summary>
    /// Tries to acquire bandwidth without waiting.
    /// </summary>
    /// <param name="bytes">Number of bytes to download.</param>
    /// <returns>True if bandwidth was acquired, false if rate limited.</returns>
    public bool TryAcquireBandwidth(long bytes)
    {
        if (_disposed) return false;
        var acquired = _bandwidthLimiter.TryAcquire(bytes);
        if (acquired)
        {
            Interlocked.Add(ref _bytesTransferredInWindow, bytes);
        }
        return acquired;
    }

    /// <summary>
    /// Acquires as many bytes as possible up to the requested amount.
    /// Useful for streaming downloads.
    /// </summary>
    /// <param name="maxBytes">Maximum bytes to acquire.</param>
    /// <returns>Number of bytes actually acquired.</returns>
    public long AcquireBandwidthPartial(long maxBytes)
    {
        if (_disposed) return 0;
        var acquired = _bandwidthLimiter.AcquirePartial(maxBytes);
        if (acquired > 0)
        {
            Interlocked.Add(ref _bytesTransferredInWindow, acquired);
        }
        return acquired;
    }

    /// <summary>
    /// Returns unused bandwidth back to the limiter.
    /// </summary>
    /// <param name="bytes">Number of bytes to return.</param>
    public void ReturnBandwidth(long bytes)
    {
        if (bytes > 0)
        {
            _bandwidthLimiter.Return(bytes);
            Interlocked.Add(ref _bytesTransferredInWindow, -bytes);
        }
    }

    /// <summary>
    /// Gets the current transfer rate in bytes per second.
    /// </summary>
    public long GetCurrentTransferRate()
    {
        var currentTicks = Environment.TickCount64;
        var windowTicks = currentTicks - Interlocked.Read(ref _windowStartTicks);

        if (windowTicks <= 0)
        {
            return 0;
        }

        var bytesInWindow = Interlocked.Read(ref _bytesTransferredInWindow);

        // Reset window if it's been more than 5 seconds
        if (windowTicks > 5000)
        {
            Interlocked.Exchange(ref _windowStartTicks, currentTicks);
            Interlocked.Exchange(ref _bytesTransferredInWindow, 0);
            return bytesInWindow * 1000 / windowTicks;
        }

        return bytesInWindow * 1000 / windowTicks;
    }

    /// <summary>
    /// Estimates wait time before the specified bytes can be downloaded.
    /// </summary>
    /// <param name="bytes">Number of bytes needed.</param>
    /// <returns>Estimated wait time.</returns>
    public TimeSpan EstimateBandwidthWaitTime(long bytes)
    {
        return _bandwidthLimiter.EstimateWaitTime(bytes);
    }

    /// <summary>
    /// Marks an item as completed.
    /// </summary>
    public async Task CompleteAsync(string itemId, CancellationToken cancellationToken = default)
    {
        if (_inProgress.TryRemove(itemId, out var item))
        {
            _items.TryRemove(itemId, out _);

            Interlocked.Add(ref _totalBytesCompleted, item.Size);
            Interlocked.Increment(ref _totalItemsCompleted);

            _logger.LogInformation("Item completed: {FilePath}, Size={Size}",
                item.FilePath, item.Size);

            if (ItemCompleted != null)
            {
                await ItemCompleted.Invoke(item, PullItemState.Completed);
            }
        }
    }

    /// <summary>
    /// Marks an item as failed and optionally retries.
    /// </summary>
    public async Task FailAsync(string itemId, string error, bool retry = true, CancellationToken cancellationToken = default)
    {
        if (_inProgress.TryRemove(itemId, out var item))
        {
            item.LastError = error;
            item.RetryCount++;

            if (retry && item.CanRetry)
            {
                _logger.LogWarning("Item failed, retrying ({Retry}/{MaxRetry}): {FilePath}, Error={Error}",
                    item.RetryCount, item.MaxRetries, item.FilePath, error);

                // Re-queue with exponential backoff delay
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, item.RetryCount)), cancellationToken);
                await EnqueueAsync(item, cancellationToken);
            }
            else
            {
                _items.TryRemove(itemId, out _);

                _logger.LogError("Item failed permanently: {FilePath}, Error={Error}",
                    item.FilePath, error);

                if (ItemCompleted != null)
                {
                    await ItemCompleted.Invoke(item, PullItemState.Failed);
                }
            }
        }
    }

    /// <summary>
    /// Cancels an item.
    /// </summary>
    public async Task CancelAsync(string itemId, CancellationToken cancellationToken = default)
    {
        await _queueLock.WaitAsync(cancellationToken);
        try
        {
            if (_inProgress.TryRemove(itemId, out var item) || _items.TryRemove(itemId, out item))
            {
                _logger.LogInformation("Item cancelled: {FilePath}", item.FilePath);

                if (ItemCompleted != null)
                {
                    await ItemCompleted.Invoke(item, PullItemState.Cancelled);
                }
            }
        }
        finally
        {
            _queueLock.Release();
        }
    }

    /// <summary>
    /// Cancels all items for a folder.
    /// </summary>
    public async Task CancelFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var itemsToCancel = _items.Values
            .Where(i => i.FolderId == folderId)
            .Select(i => i.Id)
            .ToList();

        foreach (var itemId in itemsToCancel)
        {
            await CancelAsync(itemId, cancellationToken);
        }

        _logger.LogInformation("Cancelled {Count} items for folder {FolderId}",
            itemsToCancel.Count, folderId);
    }

    /// <summary>
    /// Gets queue statistics.
    /// </summary>
    public PullQueueStats GetStats()
    {
        _queueLock.Wait();
        try
        {
            return new PullQueueStats
            {
                ItemsQueued = _queue.Count,
                ItemsInProgress = _inProgress.Count,
                TotalItemsQueued = _totalItemsQueued,
                TotalItemsCompleted = _totalItemsCompleted,
                BytesQueued = _items.Values.Sum(i => i.Size - i.BytesCompleted),
                BytesInProgress = _inProgress.Values.Sum(i => i.Size - i.BytesCompleted),
                TotalBytesQueued = _totalBytesQueued,
                TotalBytesCompleted = _totalBytesCompleted,
                CurrentTransferRate = GetCurrentTransferRate(),
                MaxTransferRate = _bandwidthLimiter.BytesPerSecond,
                IsBandwidthLimited = _bandwidthLimiter.IsLimited,
                AvailableBandwidth = _bandwidthLimiter.AvailableTokens
            };
        }
        finally
        {
            _queueLock.Release();
        }
    }

    /// <summary>
    /// Gets items currently in progress.
    /// </summary>
    public IReadOnlyList<PullItem> GetInProgressItems()
    {
        return _inProgress.Values.ToList();
    }

    /// <summary>
    /// Gets items waiting in queue.
    /// </summary>
    public IReadOnlyList<PullItem> GetQueuedItems()
    {
        return _items.Values
            .Where(i => !_inProgress.ContainsKey(i.Id))
            .OrderBy(i => GetPriorityValue(i))
            .ToList();
    }

    /// <summary>
    /// Gets all pending items for a folder.
    /// </summary>
    public IReadOnlyList<PullItem> GetFolderItems(string folderId)
    {
        return _items.Values
            .Where(i => i.FolderId == folderId)
            .ToList();
    }

    private void RequeueItem(PullItem item)
    {
        // Create new queue without the item
        var items = new List<(PullItem Item, int Priority)>();
        while (_queue.Count > 0)
        {
            var dequeued = _queue.Dequeue();
            if (dequeued.Id != item.Id)
            {
                items.Add((dequeued, GetPriorityValue(dequeued)));
            }
        }

        // Add all items back including the updated one
        items.Add((item, GetPriorityValue(item)));
        foreach (var (i, p) in items)
        {
            _queue.Enqueue(i, p);
        }
    }

    private static int GetPriorityValue(PullItem item)
    {
        // Lower value = higher priority
        // Combine priority enum with queue time for FIFO within same priority
        var priorityBase = 1000 - (int)item.Priority;
        var ageBonus = (int)(DateTime.UtcNow - item.QueuedAt).TotalSeconds / 60; // Age bonus per minute
        return priorityBase - Math.Min(ageBonus, 100);
    }

    public void Dispose()
    {
        _disposed = true;
        _queueLock.Dispose();
    }
}

/// <summary>
/// Statistics for the pull queue.
/// </summary>
public class PullQueueStats
{
    public int ItemsQueued { get; set; }
    public int ItemsInProgress { get; set; }
    public int TotalItemsQueued { get; set; }
    public int TotalItemsCompleted { get; set; }
    public long BytesQueued { get; set; }
    public long BytesInProgress { get; set; }
    public long TotalBytesQueued { get; set; }
    public long TotalBytesCompleted { get; set; }

    /// <summary>
    /// Current transfer rate in bytes per second.
    /// </summary>
    public long CurrentTransferRate { get; set; }

    /// <summary>
    /// Maximum configured transfer rate in bytes per second (0 = unlimited).
    /// </summary>
    public long MaxTransferRate { get; set; }

    /// <summary>
    /// Whether bandwidth limiting is enabled.
    /// </summary>
    public bool IsBandwidthLimited { get; set; }

    /// <summary>
    /// Available bandwidth tokens in bytes.
    /// </summary>
    public long AvailableBandwidth { get; set; }
}
