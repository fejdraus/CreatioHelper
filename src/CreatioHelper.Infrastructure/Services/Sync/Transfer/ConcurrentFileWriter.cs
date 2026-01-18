using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Transfer;

/// <summary>
/// Information about a pending block write
/// </summary>
public record PendingBlock
{
    public long Offset { get; init; }
    public byte[] Data { get; init; } = Array.Empty<byte>();
    public byte[] Hash { get; init; } = Array.Empty<byte>();
    public DateTime ReceivedAt { get; init; }
    public string SourceDevice { get; init; } = string.Empty;
}

/// <summary>
/// Status of a concurrent file write operation
/// </summary>
public record ConcurrentWriteStatus
{
    public string FilePath { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public long BytesWritten { get; init; }
    public int BlocksReceived { get; init; }
    public int BlocksWritten { get; init; }
    public int BlocksPending { get; init; }
    public DateTime StartedAt { get; init; }
    public double ProgressPercent => FileSize > 0 ? (double)BytesWritten / FileSize * 100 : 0;
    public bool IsComplete => BytesWritten >= FileSize;
}

/// <summary>
/// Provides concurrent block writes to a single file from multiple sources
/// Based on Syncthing sharedPullerState (folder_recvenc.go)
/// </summary>
public interface IConcurrentFileWriter : IAsyncDisposable
{
    /// <summary>
    /// File path being written
    /// </summary>
    string FilePath { get; }

    /// <summary>
    /// Total expected file size
    /// </summary>
    long FileSize { get; }

    /// <summary>
    /// Write a block at the specified offset
    /// </summary>
    Task<bool> WriteBlockAsync(long offset, byte[] data, byte[]? hash = null, string? sourceDevice = null, CancellationToken ct = default);

    /// <summary>
    /// Check if a block at the offset has already been written
    /// </summary>
    bool IsBlockWritten(long offset);

    /// <summary>
    /// Get the list of missing block offsets
    /// </summary>
    IReadOnlyList<long> GetMissingBlockOffsets(int blockSize);

    /// <summary>
    /// Get current write status
    /// </summary>
    ConcurrentWriteStatus GetStatus();

    /// <summary>
    /// Finalize the file (flush, set attributes, etc.)
    /// </summary>
    Task<bool> FinalizeAsync(CancellationToken ct = default);
}

/// <summary>
/// Implementation of concurrent file writer (based on Syncthing sharedPullerState)
/// Allows multiple goroutines/tasks to write different blocks of the same file concurrently
/// </summary>
public class ConcurrentFileWriter : IConcurrentFileWriter
{
    private readonly ILogger<ConcurrentFileWriter> _logger;
    private readonly FileStream _fileStream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, bool> _writtenBlocks = new();
    private readonly ConcurrentQueue<PendingBlock> _pendingBlocks = new();
    private readonly ConcurrentFileWriterOptions _options;

    private long _bytesWritten;
    private int _blocksReceived;
    private int _blocksWritten;
    private readonly DateTime _startedAt;
    private bool _disposed;
    private bool _finalized;

    public string FilePath { get; }
    public long FileSize { get; }

    public ConcurrentFileWriter(
        ILogger<ConcurrentFileWriter> logger,
        string filePath,
        long fileSize,
        ConcurrentFileWriterOptions? options = null)
    {
        _logger = logger;
        FilePath = filePath;
        FileSize = fileSize;
        _options = options ?? new ConcurrentFileWriterOptions();
        _startedAt = DateTime.UtcNow;

        // Ensure directory exists
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Open file for concurrent writes
        _fileStream = new FileStream(
            filePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: _options.BufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        // Pre-allocate file size for better performance
        if (_options.PreAllocate && fileSize > 0)
        {
            try
            {
                _fileStream.SetLength(fileSize);
                _logger.LogDebug("Pre-allocated file: {Path}, size={Size}", filePath, fileSize);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to pre-allocate file: {Path}", filePath);
            }
        }

        _logger.LogDebug("Opened concurrent file writer: {Path}, size={Size}", filePath, fileSize);
    }

    /// <summary>
    /// Write a block at the specified offset
    /// Thread-safe: multiple callers can write different blocks concurrently
    /// </summary>
    public async Task<bool> WriteBlockAsync(long offset, byte[] data, byte[]? hash = null,
        string? sourceDevice = null, CancellationToken ct = default)
    {
        if (_disposed || _finalized)
        {
            _logger.LogWarning("Attempt to write to disposed/finalized file: {Path}", FilePath);
            return false;
        }

        Interlocked.Increment(ref _blocksReceived);

        // Check if block was already written
        if (_writtenBlocks.ContainsKey(offset))
        {
            _logger.LogTrace("Block already written: offset={Offset}, file={Path}", offset, FilePath);
            return true;
        }

        // Validate block boundaries
        if (offset < 0 || offset >= FileSize)
        {
            _logger.LogWarning("Invalid block offset: offset={Offset}, fileSize={FileSize}", offset, FileSize);
            return false;
        }

        if (offset + data.Length > FileSize)
        {
            _logger.LogWarning("Block exceeds file size: offset={Offset}, dataLen={DataLen}, fileSize={FileSize}",
                offset, data.Length, FileSize);
            return false;
        }

        // Acquire write lock for actual I/O
        await _writeLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_writtenBlocks.ContainsKey(offset))
            {
                return true;
            }

            // Seek and write
            _fileStream.Seek(offset, SeekOrigin.Begin);
            await _fileStream.WriteAsync(data, ct);

            // Mark block as written
            _writtenBlocks[offset] = true;
            Interlocked.Add(ref _bytesWritten, data.Length);
            Interlocked.Increment(ref _blocksWritten);

            _logger.LogTrace("Wrote block: offset={Offset}, len={Length}, file={Path}, source={Source}",
                offset, data.Length, FilePath, sourceDevice ?? "unknown");

            // Flush if configured
            if (_options.FlushAfterWrite)
            {
                await _fileStream.FlushAsync(ct);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write block: offset={Offset}, file={Path}", offset, FilePath);
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Check if a block at the offset has already been written
    /// </summary>
    public bool IsBlockWritten(long offset)
    {
        return _writtenBlocks.ContainsKey(offset);
    }

    /// <summary>
    /// Get the list of missing block offsets
    /// </summary>
    public IReadOnlyList<long> GetMissingBlockOffsets(int blockSize)
    {
        var missing = new List<long>();

        for (long offset = 0; offset < FileSize; offset += blockSize)
        {
            if (!_writtenBlocks.ContainsKey(offset))
            {
                missing.Add(offset);
            }
        }

        return missing;
    }

    /// <summary>
    /// Get current write status
    /// </summary>
    public ConcurrentWriteStatus GetStatus()
    {
        return new ConcurrentWriteStatus
        {
            FilePath = FilePath,
            FileSize = FileSize,
            BytesWritten = Interlocked.Read(ref _bytesWritten),
            BlocksReceived = Interlocked.CompareExchange(ref _blocksReceived, 0, 0),
            BlocksWritten = Interlocked.CompareExchange(ref _blocksWritten, 0, 0),
            BlocksPending = _pendingBlocks.Count,
            StartedAt = _startedAt
        };
    }

    /// <summary>
    /// Finalize the file (flush, truncate to actual size if needed)
    /// </summary>
    public async Task<bool> FinalizeAsync(CancellationToken ct = default)
    {
        if (_finalized)
            return true;

        await _writeLock.WaitAsync(ct);
        try
        {
            // Final flush
            await _fileStream.FlushAsync(ct);

            // Verify all bytes were written
            var bytesWritten = Interlocked.Read(ref _bytesWritten);
            if (bytesWritten != FileSize)
            {
                _logger.LogWarning("File incomplete: expected={Expected}, written={Written}, file={Path}",
                    FileSize, bytesWritten, FilePath);

                // Truncate to actual written size if incomplete
                if (_options.TruncateOnIncomplete && bytesWritten < FileSize)
                {
                    _fileStream.SetLength(bytesWritten);
                }

                return false;
            }

            _finalized = true;
            _logger.LogDebug("Finalized file: {Path}, size={Size}", FilePath, FileSize);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finalize file: {Path}", FilePath);
            return false;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        await _writeLock.WaitAsync();
        try
        {
            await _fileStream.DisposeAsync();
        }
        finally
        {
            _writeLock.Release();
            _writeLock.Dispose();
        }

        _logger.LogDebug("Disposed concurrent file writer: {Path}", FilePath);
    }
}

/// <summary>
/// Options for concurrent file writer
/// </summary>
public class ConcurrentFileWriterOptions
{
    /// <summary>
    /// Buffer size for file operations (default: 64KB)
    /// </summary>
    public int BufferSize { get; set; } = 64 * 1024;

    /// <summary>
    /// Pre-allocate file to expected size (default: true)
    /// </summary>
    public bool PreAllocate { get; set; } = true;

    /// <summary>
    /// Flush after each block write (default: false, for performance)
    /// </summary>
    public bool FlushAfterWrite { get; set; }

    /// <summary>
    /// Truncate file on incomplete writes (default: true)
    /// </summary>
    public bool TruncateOnIncomplete { get; set; } = true;
}

/// <summary>
/// Factory for creating concurrent file writers
/// </summary>
public class ConcurrentFileWriterFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ConcurrentDictionary<string, IConcurrentFileWriter> _activeWriters = new();

    public ConcurrentFileWriterFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Get or create a concurrent file writer for a path
    /// </summary>
    public IConcurrentFileWriter GetOrCreate(string filePath, long fileSize, ConcurrentFileWriterOptions? options = null)
    {
        return _activeWriters.GetOrAdd(filePath, path =>
        {
            var logger = _loggerFactory.CreateLogger<ConcurrentFileWriter>();
            return new ConcurrentFileWriter(logger, path, fileSize, options);
        });
    }

    /// <summary>
    /// Remove a writer from tracking (after finalization)
    /// </summary>
    public bool Remove(string filePath)
    {
        return _activeWriters.TryRemove(filePath, out _);
    }

    /// <summary>
    /// Get all active writers
    /// </summary>
    public IReadOnlyDictionary<string, IConcurrentFileWriter> GetActiveWriters()
    {
        return _activeWriters;
    }
}
