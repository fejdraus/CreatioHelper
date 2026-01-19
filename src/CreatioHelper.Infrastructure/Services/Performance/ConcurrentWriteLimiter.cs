using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Performance;

/// <summary>
/// Implementation of concurrent write limiting service.
/// Based on Syncthing's maxConcurrentWrites from folderconfiguration.go
/// </summary>
public class ConcurrentWriteLimiter : IConcurrentWriteLimiter, IDisposable
{
    private readonly ILogger<ConcurrentWriteLimiter> _logger;
    private readonly ConcurrentDictionary<string, FolderWriteTracker> _folderTrackers = new();
    private readonly ConcurrentDictionary<Guid, WriteSlot> _activeSlots = new();
    private readonly SemaphoreSlim _globalSemaphore;
    private readonly object _statsLock = new();

    private ConcurrentWriteLimiterConfiguration _config;
    private int _peakWrites;
    private long _totalCompleted;
    private long _totalRejected;
    private long _totalBytesWritten;
    private long _totalDurationTicks;
    private int _waitingRequests;
    private bool _disposed;

    public ConcurrentWriteLimiter(
        ILogger<ConcurrentWriteLimiter> logger,
        ConcurrentWriteLimiterConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new ConcurrentWriteLimiterConfiguration();

        // Initialize global semaphore (0 = unlimited, use large number)
        var maxTotal = _config.MaxConcurrentWrites > 0 ? _config.MaxConcurrentWrites : int.MaxValue;
        _globalSemaphore = new SemaphoreSlim(maxTotal, maxTotal);

        _logger.LogInformation(
            "Concurrent write limiter initialized: max total = {MaxTotal}, max per folder = {MaxPerFolder}",
            _config.MaxConcurrentWrites > 0 ? _config.MaxConcurrentWrites.ToString() : "unlimited",
            _config.MaxWritesPerFolder > 0 ? _config.MaxWritesPerFolder.ToString() : "unlimited");
    }

    /// <inheritdoc />
    public int TotalWriteCount => _activeSlots.Count;

    /// <inheritdoc />
    public IWriteSlot? TryAcquire(string filePath, string? folderId = null)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentNullException(nameof(filePath));

        var effectiveFolderId = folderId ?? GetFolderIdFromPath(filePath);
        var tracker = GetOrCreateTracker(effectiveFolderId);
        var maxPerFolder = GetMaxWritesForFolder(effectiveFolderId);

        // Check folder limit
        if (maxPerFolder > 0 && tracker.ActiveCount >= maxPerFolder)
        {
            Interlocked.Increment(ref _totalRejected);
            _logger.LogDebug("Write rejected for {Path}: folder limit reached ({Count}/{Max})",
                filePath, tracker.ActiveCount, maxPerFolder);
            return null;
        }

        // Try to acquire global slot
        if (!_globalSemaphore.Wait(0))
        {
            Interlocked.Increment(ref _totalRejected);
            _logger.LogDebug("Write rejected for {Path}: global limit reached", filePath);
            return null;
        }

        // Create and register slot
        var slot = new WriteSlot(filePath, effectiveFolderId, this);

        if (!tracker.TryAdd(slot))
        {
            _globalSemaphore.Release();
            Interlocked.Increment(ref _totalRejected);
            return null;
        }

        _activeSlots[slot.Id] = slot;
        UpdatePeakWrites();

        _logger.LogDebug("Write slot acquired for {Path} (total: {Total})", filePath, TotalWriteCount);

        return slot;
    }

    /// <inheritdoc />
    public async Task<IWriteSlot?> AcquireAsync(string filePath, string? folderId = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentNullException(nameof(filePath));

        var effectiveFolderId = folderId ?? GetFolderIdFromPath(filePath);
        var tracker = GetOrCreateTracker(effectiveFolderId);
        var maxPerFolder = GetMaxWritesForFolder(effectiveFolderId);
        var effectiveTimeout = timeout ?? _config.DefaultTimeout;

        // Try immediate acquire first
        var slot = TryAcquire(filePath, folderId);
        if (slot != null)
            return slot;

        // Wait for availability
        Interlocked.Increment(ref _waitingRequests);
        try
        {
            using var timeoutCts = new CancellationTokenSource(effectiveTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            while (!linkedCts.Token.IsCancellationRequested)
            {
                // Wait for global semaphore
                var acquired = await _globalSemaphore.WaitAsync(100, linkedCts.Token);
                if (!acquired)
                    continue;

                // Check folder limit
                if (maxPerFolder > 0 && tracker.ActiveCount >= maxPerFolder)
                {
                    _globalSemaphore.Release();
                    await Task.Delay(50, linkedCts.Token);
                    continue;
                }

                // Create and register slot
                var newSlot = new WriteSlot(filePath, effectiveFolderId, this);

                if (!tracker.TryAdd(newSlot))
                {
                    _globalSemaphore.Release();
                    await Task.Delay(50, linkedCts.Token);
                    continue;
                }

                _activeSlots[newSlot.Id] = newSlot;
                UpdatePeakWrites();

                _logger.LogDebug("Write slot acquired (async) for {Path} (total: {Total})",
                    filePath, TotalWriteCount);

                return newSlot;
            }
        }
        catch (OperationCanceledException)
        {
            // Timeout or cancellation
        }
        finally
        {
            Interlocked.Decrement(ref _waitingRequests);
        }

        Interlocked.Increment(ref _totalRejected);
        _logger.LogDebug("Write acquire timed out for {Path}", filePath);
        return null;
    }

    /// <inheritdoc />
    public int GetFolderWriteCount(string folderId)
    {
        if (_folderTrackers.TryGetValue(folderId, out var tracker))
        {
            return tracker.ActiveCount;
        }
        return 0;
    }

    /// <inheritdoc />
    public bool CanWrite(string? folderId = null)
    {
        if (folderId != null)
        {
            var maxPerFolder = GetMaxWritesForFolder(folderId);
            var currentCount = GetFolderWriteCount(folderId);

            if (maxPerFolder > 0 && currentCount >= maxPerFolder)
                return false;
        }

        if (_config.MaxConcurrentWrites > 0 && TotalWriteCount >= _config.MaxConcurrentWrites)
            return false;

        return true;
    }

    /// <inheritdoc />
    public void UpdateConfiguration(ConcurrentWriteLimiterConfiguration configuration)
    {
        _config = configuration;

        _logger.LogInformation(
            "Concurrent write limiter configuration updated: max total = {MaxTotal}, max per folder = {MaxPerFolder}",
            configuration.MaxConcurrentWrites > 0 ? configuration.MaxConcurrentWrites.ToString() : "unlimited",
            configuration.MaxWritesPerFolder > 0 ? configuration.MaxWritesPerFolder.ToString() : "unlimited");
    }

    /// <inheritdoc />
    public void SetFolderLimit(string folderId, int maxWrites)
    {
        _config.FolderOverrides[folderId] = maxWrites;
        _logger.LogDebug("Set folder limit: {FolderId} = {MaxWrites}", folderId, maxWrites);
    }

    /// <inheritdoc />
    public void RemoveFolderLimit(string folderId)
    {
        _config.FolderOverrides.Remove(folderId);
        _logger.LogDebug("Removed folder limit: {FolderId}", folderId);
    }

    /// <inheritdoc />
    public ConcurrentWriteLimiterStatistics GetStatistics()
    {
        var writesByFolder = _folderTrackers.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.ActiveCount);

        var activeFiles = _activeSlots.Values
            .Select(s => s.FilePath)
            .ToList();

        TimeSpan avgDuration = TimeSpan.Zero;
        if (_totalCompleted > 0)
        {
            avgDuration = TimeSpan.FromTicks(Interlocked.Read(ref _totalDurationTicks) / _totalCompleted);
        }

        return new ConcurrentWriteLimiterStatistics
        {
            ActiveWrites = TotalWriteCount,
            PeakWrites = _peakWrites,
            TotalWritesCompleted = Interlocked.Read(ref _totalCompleted),
            TotalWritesRejected = Interlocked.Read(ref _totalRejected),
            TotalBytesWritten = Interlocked.Read(ref _totalBytesWritten),
            WritesByFolder = writesByFolder,
            AverageWriteDuration = avgDuration,
            WaitingRequests = _waitingRequests,
            ActiveFiles = activeFiles
        };
    }

    internal void ReleaseSlot(WriteSlot slot)
    {
        if (_activeSlots.TryRemove(slot.Id, out _))
        {
            if (slot.FolderId != null && _folderTrackers.TryGetValue(slot.FolderId, out var tracker))
            {
                tracker.Remove(slot);
            }

            _globalSemaphore.Release();

            // Track statistics
            if (_config.TrackStatistics)
            {
                var duration = DateTime.UtcNow - slot.AcquiredAt;
                Interlocked.Add(ref _totalDurationTicks, duration.Ticks);
                Interlocked.Add(ref _totalBytesWritten, slot.BytesWritten);
                Interlocked.Increment(ref _totalCompleted);
            }

            _logger.LogDebug("Write slot released for {Path} (total: {Total}, bytes: {Bytes})",
                slot.FilePath, TotalWriteCount, slot.BytesWritten);
        }
    }

    private FolderWriteTracker GetOrCreateTracker(string folderId)
    {
        return _folderTrackers.GetOrAdd(folderId, _ => new FolderWriteTracker(folderId));
    }

    private int GetMaxWritesForFolder(string folderId)
    {
        if (_config.FolderOverrides.TryGetValue(folderId, out var maxWrites))
        {
            return maxWrites;
        }
        return _config.MaxWritesPerFolder;
    }

    private static string GetFolderIdFromPath(string filePath)
    {
        // Extract folder from path - use parent directory name
        var directory = Path.GetDirectoryName(filePath);
        if (string.IsNullOrEmpty(directory))
            return "default";

        // Get the last directory name as folder ID
        return Path.GetFileName(directory) ?? "default";
    }

    private void UpdatePeakWrites()
    {
        var current = TotalWriteCount;
        lock (_statsLock)
        {
            if (current > _peakWrites)
            {
                _peakWrites = current;
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _globalSemaphore.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// Tracks writes for a specific folder.
/// </summary>
internal class FolderWriteTracker
{
    private readonly string _folderId;
    private readonly ConcurrentDictionary<Guid, WriteSlot> _slots = new();

    public FolderWriteTracker(string folderId)
    {
        _folderId = folderId;
    }

    public string FolderId => _folderId;
    public int ActiveCount => _slots.Count;

    public bool TryAdd(WriteSlot slot)
    {
        return _slots.TryAdd(slot.Id, slot);
    }

    public void Remove(WriteSlot slot)
    {
        _slots.TryRemove(slot.Id, out _);
    }
}

/// <summary>
/// Represents an acquired write slot.
/// </summary>
internal class WriteSlot : IWriteSlot
{
    private readonly ConcurrentWriteLimiter _limiter;
    private bool _disposed;

    public WriteSlot(string filePath, string? folderId, ConcurrentWriteLimiter limiter)
    {
        Id = Guid.NewGuid();
        FilePath = filePath;
        FolderId = folderId;
        AcquiredAt = DateTime.UtcNow;
        _limiter = limiter;
    }

    public Guid Id { get; }
    public string FilePath { get; }
    public string? FolderId { get; }
    public DateTime AcquiredAt { get; }
    public long BytesWritten { get; set; }
    public bool IsValid => !_disposed;

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _limiter.ReleaseSlot(this);
        }
    }
}
