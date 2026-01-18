using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// Controls fsync behavior for file operations (based on Syncthing basicfs.go)
/// fsync ensures data is written to disk, but can impact performance
/// </summary>
public interface IFsyncController
{
    /// <summary>
    /// Whether fsync is enabled
    /// </summary>
    bool FsyncEnabled { get; set; }

    /// <summary>
    /// Sync a file to disk
    /// </summary>
    Task SyncFileAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Sync a directory to disk
    /// </summary>
    Task SyncDirectoryAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Write data to a file with optional fsync
    /// </summary>
    Task WriteFileWithSyncAsync(string path, byte[] data, bool forceSync = false, CancellationToken ct = default);

    /// <summary>
    /// Create and write to a file atomically (temp file + rename + fsync)
    /// </summary>
    Task AtomicWriteAsync(string path, byte[] data, CancellationToken ct = default);

    /// <summary>
    /// Get fsync statistics
    /// </summary>
    FsyncStats GetStats();
}

/// <summary>
/// Statistics for fsync operations
/// </summary>
public record FsyncStats
{
    public long TotalSyncs { get; init; }
    public long SuccessfulSyncs { get; init; }
    public long FailedSyncs { get; init; }
    public long SkippedSyncs { get; init; }
    public TimeSpan TotalSyncTime { get; init; }
    public double AverageSyncTimeMs => TotalSyncs > 0 ? TotalSyncTime.TotalMilliseconds / TotalSyncs : 0;
}

/// <summary>
/// Implementation of fsync control (based on Syncthing basicfs.go)
/// </summary>
public class FsyncController : IFsyncController
{
    private readonly ILogger<FsyncController> _logger;

    private long _totalSyncs;
    private long _successfulSyncs;
    private long _failedSyncs;
    private long _skippedSyncs;
    private long _totalSyncTimeTicks;

    public bool FsyncEnabled { get; set; } = true;

    public FsyncController(ILogger<FsyncController> logger, bool fsyncEnabled = true)
    {
        _logger = logger;
        FsyncEnabled = fsyncEnabled;
    }

    /// <summary>
    /// Sync a file to disk using platform-specific methods
    /// </summary>
    public async Task SyncFileAsync(string path, CancellationToken ct = default)
    {
        if (!FsyncEnabled)
        {
            Interlocked.Increment(ref _skippedSyncs);
            return;
        }

        Interlocked.Increment(ref _totalSyncs);
        var startTime = DateTime.UtcNow;

        try
        {
            await Task.Run(() =>
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Flush(flushToDisk: true);
            }, ct);

            Interlocked.Increment(ref _successfulSyncs);
            Interlocked.Add(ref _totalSyncTimeTicks, (DateTime.UtcNow - startTime).Ticks);

            _logger.LogTrace("Synced file to disk: {Path}", path);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedSyncs);
            _logger.LogWarning(ex, "Failed to sync file: {Path}", path);
            throw;
        }
    }

    /// <summary>
    /// Sync a directory to disk
    /// On some platforms, syncing the parent directory ensures the directory entry is persisted
    /// </summary>
    public async Task SyncDirectoryAsync(string path, CancellationToken ct = default)
    {
        if (!FsyncEnabled)
        {
            Interlocked.Increment(ref _skippedSyncs);
            return;
        }

        // On Windows, directory sync is a no-op as NTFS doesn't support it the same way
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogTrace("Directory sync skipped on Windows: {Path}", path);
            return;
        }

        Interlocked.Increment(ref _totalSyncs);
        var startTime = DateTime.UtcNow;

        try
        {
            await Task.Run(() =>
            {
                // On Unix-like systems, we can fsync the directory
                // This is a simplified version; real implementation would use P/Invoke for fsync(2)
                // For now, touch the directory to update its timestamp
                Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }, ct);

            Interlocked.Increment(ref _successfulSyncs);
            Interlocked.Add(ref _totalSyncTimeTicks, (DateTime.UtcNow - startTime).Ticks);

            _logger.LogTrace("Synced directory: {Path}", path);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _failedSyncs);
            _logger.LogWarning(ex, "Failed to sync directory: {Path}", path);
        }
    }

    /// <summary>
    /// Write data to a file with optional fsync
    /// </summary>
    public async Task WriteFileWithSyncAsync(string path, byte[] data, bool forceSync = false, CancellationToken ct = default)
    {
        await Task.Run(() =>
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.WriteThrough);

            fs.Write(data, 0, data.Length);

            if (FsyncEnabled || forceSync)
            {
                fs.Flush(flushToDisk: true);
                Interlocked.Increment(ref _successfulSyncs);
            }
        }, ct);

        _logger.LogTrace("Wrote file with sync: {Path}, size={Size}", path, data.Length);
    }

    /// <summary>
    /// Create and write to a file atomically
    /// Uses temp file + rename pattern for atomic writes
    /// </summary>
    public async Task AtomicWriteAsync(string path, byte[] data, CancellationToken ct = default)
    {
        var tempPath = path + ".tmp";

        try
        {
            // Write to temp file
            await Task.Run(() =>
            {
                // Ensure directory exists
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 4096, FileOptions.WriteThrough))
                {
                    fs.Write(data, 0, data.Length);

                    if (FsyncEnabled)
                    {
                        fs.Flush(flushToDisk: true);
                    }
                }

                // Atomic rename
                File.Move(tempPath, path, overwrite: true);
            }, ct);

            // Optionally sync the directory to ensure the rename is persisted
            if (FsyncEnabled)
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    await SyncDirectoryAsync(dir, ct);
                }
            }

            _logger.LogTrace("Atomic write completed: {Path}, size={Size}", path, data.Length);
        }
        catch
        {
            // Cleanup temp file on error
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }

            throw;
        }
    }

    /// <summary>
    /// Get fsync statistics
    /// </summary>
    public FsyncStats GetStats()
    {
        return new FsyncStats
        {
            TotalSyncs = Interlocked.Read(ref _totalSyncs),
            SuccessfulSyncs = Interlocked.Read(ref _successfulSyncs),
            FailedSyncs = Interlocked.Read(ref _failedSyncs),
            SkippedSyncs = Interlocked.Read(ref _skippedSyncs),
            TotalSyncTime = TimeSpan.FromTicks(Interlocked.Read(ref _totalSyncTimeTicks))
        };
    }
}
