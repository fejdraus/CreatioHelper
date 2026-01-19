using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Transfer;

/// <summary>
/// Service to limit the maximum number of concurrent write operations per file.
/// Based on Syncthing's maxConcurrentWrites folder configuration.
/// </summary>
public interface IMaxConcurrentWritesService
{
    /// <summary>
    /// Acquire a write slot for a file. Returns a disposable that releases the slot.
    /// </summary>
    Task<IDisposable> AcquireWriteSlotAsync(string folderId, string filePath, CancellationToken ct = default);

    /// <summary>
    /// Try to acquire a write slot without waiting.
    /// </summary>
    bool TryAcquireWriteSlot(string folderId, string filePath, out IDisposable? slot);

    /// <summary>
    /// Get the number of active writers for a file.
    /// </summary>
    int GetActiveWriters(string folderId, string filePath);

    /// <summary>
    /// Get the configured maximum concurrent writes for a folder.
    /// </summary>
    int GetMaxConcurrentWrites(string folderId);

    /// <summary>
    /// Set the maximum concurrent writes for a folder.
    /// </summary>
    void SetMaxConcurrentWrites(string folderId, int maxWrites);

    /// <summary>
    /// Get statistics for concurrent writes.
    /// </summary>
    ConcurrentWriteStats GetStats(string folderId);
}

/// <summary>
/// Statistics for concurrent write operations.
/// </summary>
public class ConcurrentWriteStats
{
    public string FolderId { get; init; } = string.Empty;
    public int MaxConcurrentWrites { get; init; }
    public int CurrentActiveWrites { get; init; }
    public int TotalFilesWithActiveWrites { get; init; }

    internal long _totalWritesAcquired;
    internal long _totalWritesReleased;
    internal long _totalWaitsForSlot;

    public long TotalWritesAcquired => Interlocked.Read(ref _totalWritesAcquired);
    public long TotalWritesReleased => Interlocked.Read(ref _totalWritesReleased);
    public long TotalWaitsForSlot => Interlocked.Read(ref _totalWaitsForSlot);
}

/// <summary>
/// Configuration for max concurrent writes.
/// </summary>
public class MaxConcurrentWritesConfiguration
{
    /// <summary>
    /// Default maximum concurrent writes per file. 0 means unlimited.
    /// Syncthing default is 2.
    /// </summary>
    public int DefaultMaxConcurrentWrites { get; set; } = 2;

    /// <summary>
    /// Per-folder overrides.
    /// </summary>
    public Dictionary<string, int> FolderMaxWrites { get; } = new();

    /// <summary>
    /// Timeout for acquiring a write slot.
    /// </summary>
    public TimeSpan AcquireTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Get effective max concurrent writes for a folder.
    /// </summary>
    public int GetEffectiveMaxWrites(string folderId)
    {
        if (FolderMaxWrites.TryGetValue(folderId, out var maxWrites))
        {
            return maxWrites;
        }
        return DefaultMaxConcurrentWrites;
    }
}

/// <summary>
/// Implementation of max concurrent writes service.
/// </summary>
public class MaxConcurrentWritesService : IMaxConcurrentWritesService
{
    private readonly ILogger<MaxConcurrentWritesService> _logger;
    private readonly MaxConcurrentWritesConfiguration _config;
    private readonly ConcurrentDictionary<string, FolderWriteState> _folderStates = new();

    public MaxConcurrentWritesService(
        ILogger<MaxConcurrentWritesService> logger,
        MaxConcurrentWritesConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new MaxConcurrentWritesConfiguration();
    }

    /// <inheritdoc />
    public async Task<IDisposable> AcquireWriteSlotAsync(string folderId, string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(filePath);

        var state = GetOrCreateFolderState(folderId);
        var fileKey = NormalizeFilePath(filePath);
        var fileState = state.GetOrCreateFileState(fileKey);

        var maxWrites = GetMaxConcurrentWrites(folderId);

        // If unlimited, just track and return
        if (maxWrites <= 0)
        {
            fileState.IncrementWriters();
            Interlocked.Increment(ref state.Stats._totalWritesAcquired);
            return new WriteSlotHandle(fileState, state.Stats);
        }

        // Wait for a slot
        using var timeoutCts = new CancellationTokenSource(_config.AcquireTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (fileState.TryAcquire(maxWrites))
            {
                Interlocked.Increment(ref state.Stats._totalWritesAcquired);
                _logger.LogDebug("Acquired write slot for {FilePath} in folder {FolderId}", filePath, folderId);
                return new WriteSlotHandle(fileState, state.Stats);
            }

            Interlocked.Increment(ref state.Stats._totalWaitsForSlot);

            try
            {
                await fileState.WaitForSlotAsync(linkedCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException($"Timeout waiting for write slot on {filePath}");
            }
        }
    }

    /// <inheritdoc />
    public bool TryAcquireWriteSlot(string folderId, string filePath, out IDisposable? slot)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(filePath);

        slot = null;
        var state = GetOrCreateFolderState(folderId);
        var fileKey = NormalizeFilePath(filePath);
        var fileState = state.GetOrCreateFileState(fileKey);

        var maxWrites = GetMaxConcurrentWrites(folderId);

        // If unlimited, just track and return
        if (maxWrites <= 0)
        {
            fileState.IncrementWriters();
            Interlocked.Increment(ref state.Stats._totalWritesAcquired);
            slot = new WriteSlotHandle(fileState, state.Stats);
            return true;
        }

        if (fileState.TryAcquire(maxWrites))
        {
            Interlocked.Increment(ref state.Stats._totalWritesAcquired);
            slot = new WriteSlotHandle(fileState, state.Stats);
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public int GetActiveWriters(string folderId, string filePath)
    {
        ArgumentNullException.ThrowIfNull(folderId);
        ArgumentNullException.ThrowIfNull(filePath);

        if (!_folderStates.TryGetValue(folderId, out var state))
        {
            return 0;
        }

        var fileKey = NormalizeFilePath(filePath);
        return state.GetActiveWriters(fileKey);
    }

    /// <inheritdoc />
    public int GetMaxConcurrentWrites(string folderId)
    {
        return _config.GetEffectiveMaxWrites(folderId);
    }

    /// <inheritdoc />
    public void SetMaxConcurrentWrites(string folderId, int maxWrites)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (maxWrites < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWrites), "Max writes cannot be negative");
        }

        _config.FolderMaxWrites[folderId] = maxWrites;
        _logger.LogInformation("Set max concurrent writes for folder {FolderId} to {MaxWrites}", folderId, maxWrites);
    }

    /// <inheritdoc />
    public ConcurrentWriteStats GetStats(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        var state = GetOrCreateFolderState(folderId);
        var stats = state.Stats;

        return new ConcurrentWriteStats
        {
            FolderId = folderId,
            MaxConcurrentWrites = GetMaxConcurrentWrites(folderId),
            CurrentActiveWrites = state.GetTotalActiveWrites(),
            TotalFilesWithActiveWrites = state.GetFilesWithActiveWrites(),
            _totalWritesAcquired = Interlocked.Read(ref stats._totalWritesAcquired),
            _totalWritesReleased = Interlocked.Read(ref stats._totalWritesReleased),
            _totalWaitsForSlot = Interlocked.Read(ref stats._totalWaitsForSlot)
        };
    }

    private FolderWriteState GetOrCreateFolderState(string folderId)
    {
        return _folderStates.GetOrAdd(folderId, id => new FolderWriteState(id));
    }

    private static string NormalizeFilePath(string filePath)
    {
        return filePath.Replace('\\', '/').ToLowerInvariant();
    }

    private class FolderWriteState
    {
        public string FolderId { get; }
        public ConcurrentWriteStats Stats { get; }
        private readonly ConcurrentDictionary<string, FileWriteState> _fileStates = new();

        public FolderWriteState(string folderId)
        {
            FolderId = folderId;
            Stats = new ConcurrentWriteStats { FolderId = folderId };
        }

        public FileWriteState GetOrCreateFileState(string fileKey)
        {
            return _fileStates.GetOrAdd(fileKey, _ => new FileWriteState());
        }

        public int GetActiveWriters(string fileKey)
        {
            if (_fileStates.TryGetValue(fileKey, out var state))
            {
                return state.ActiveWriters;
            }
            return 0;
        }

        public int GetTotalActiveWrites()
        {
            int total = 0;
            foreach (var state in _fileStates.Values)
            {
                total += state.ActiveWriters;
            }
            return total;
        }

        public int GetFilesWithActiveWrites()
        {
            int count = 0;
            foreach (var state in _fileStates.Values)
            {
                if (state.ActiveWriters > 0)
                {
                    count++;
                }
            }
            return count;
        }
    }

    private class FileWriteState
    {
        private int _activeWriters;
        private readonly SemaphoreSlim _slotAvailable = new(0);

        public int ActiveWriters => _activeWriters;

        public bool TryAcquire(int maxWrites)
        {
            while (true)
            {
                var current = _activeWriters;
                if (current >= maxWrites)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _activeWriters, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        public void IncrementWriters()
        {
            Interlocked.Increment(ref _activeWriters);
        }

        public void Release()
        {
            var newValue = Interlocked.Decrement(ref _activeWriters);
            if (newValue >= 0)
            {
                // Signal that a slot is available
                try
                {
                    _slotAvailable.Release();
                }
                catch (SemaphoreFullException)
                {
                    // Ignore - no one is waiting
                }
            }
        }

        public async Task WaitForSlotAsync(CancellationToken ct)
        {
            await _slotAvailable.WaitAsync(ct);
        }
    }

    private class WriteSlotHandle : IDisposable
    {
        private readonly FileWriteState _fileState;
        private readonly ConcurrentWriteStats _stats;
        private bool _disposed;

        public WriteSlotHandle(FileWriteState fileState, ConcurrentWriteStats stats)
        {
            _fileState = fileState;
            _stats = stats;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _fileState.Release();
                Interlocked.Increment(ref _stats._totalWritesReleased);
            }
        }
    }
}
