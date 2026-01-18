using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// Copy range method enumeration (based on Syncthing copyrange_*.go)
/// </summary>
public enum CopyRangeMethod
{
    /// <summary>
    /// Standard copy using read/write (always works)
    /// </summary>
    Standard,

    /// <summary>
    /// Linux copy_file_range syscall (kernel >= 4.5)
    /// </summary>
    CopyFileRange,

    /// <summary>
    /// Linux ioctl FICLONE/FICLONERANGE (btrfs, xfs, etc.)
    /// </summary>
    Reflink,

    /// <summary>
    /// Windows FSCTL_DUPLICATE_EXTENTS_TO_FILE (ReFS, newer NTFS)
    /// </summary>
    DuplicateExtents,

    /// <summary>
    /// sendfile (Linux/macOS, for network efficiency)
    /// </summary>
    SendFile,

    /// <summary>
    /// Auto-detect best available method
    /// </summary>
    Auto
}

/// <summary>
/// Provides optimized file range copying operations (based on Syncthing copyrange_*.go)
/// Uses platform-specific optimizations when available
/// </summary>
public interface ICopyRangeOptimizer
{
    /// <summary>
    /// Whether optimized copy range is supported
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Currently active copy method
    /// </summary>
    CopyRangeMethod ActiveMethod { get; }

    /// <summary>
    /// Try to copy a range of bytes between files using optimized methods
    /// Falls back to standard copy if optimized methods fail
    /// </summary>
    Task<bool> TryCopyRangeAsync(string srcPath, string dstPath,
        long srcOffset, long dstOffset, long length, CancellationToken ct = default);

    /// <summary>
    /// Try to clone an entire file using reflink or similar technology
    /// </summary>
    Task<bool> TryCloneFileAsync(string srcPath, string dstPath, CancellationToken ct = default);

    /// <summary>
    /// Detect the best available copy method for a path
    /// </summary>
    Task<CopyRangeMethod> DetectBestMethodAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Get copy range statistics
    /// </summary>
    CopyRangeStats GetStats();
}

/// <summary>
/// Statistics for copy range operations
/// </summary>
public record CopyRangeStats
{
    public long TotalCopies { get; init; }
    public long OptimizedCopies { get; init; }
    public long FallbackCopies { get; init; }
    public long TotalBytesCopied { get; init; }
    public long OptimizedBytesCopied { get; init; }
    public TimeSpan TotalCopyTime { get; init; }
    public double OptimizedPercentage => TotalCopies > 0 ? (double)OptimizedCopies / TotalCopies * 100 : 0;
}

/// <summary>
/// Implementation of copy range optimization (based on Syncthing copyrange_*.go)
/// </summary>
public class CopyRangeOptimizer : ICopyRangeOptimizer
{
    private readonly ILogger<CopyRangeOptimizer> _logger;
    private readonly CopyRangeMethod _preferredMethod;
    private CopyRangeMethod _activeMethod;

    // Statistics
    private long _totalCopies;
    private long _optimizedCopies;
    private long _fallbackCopies;
    private long _totalBytesCopied;
    private long _optimizedBytesCopied;
    private long _totalCopyTimeTicks;

    // Buffer for standard copy
    private const int CopyBufferSize = 128 * 1024; // 128KB

    public bool IsSupported { get; private set; }
    public CopyRangeMethod ActiveMethod => _activeMethod;

    public CopyRangeOptimizer(ILogger<CopyRangeOptimizer> logger, CopyRangeMethod preferredMethod = CopyRangeMethod.Auto)
    {
        _logger = logger;
        _preferredMethod = preferredMethod;
        _activeMethod = DetectActiveMethod(preferredMethod);
        IsSupported = _activeMethod != CopyRangeMethod.Standard;

        _logger.LogInformation("Copy range optimizer initialized: method={Method}, supported={Supported}",
            _activeMethod, IsSupported);
    }

    private CopyRangeMethod DetectActiveMethod(CopyRangeMethod preferred)
    {
        if (preferred != CopyRangeMethod.Auto)
            return preferred;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Linux: prefer copy_file_range, fall back to sendfile
            return CopyRangeMethod.CopyFileRange;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: try DuplicateExtents on modern systems
            return CopyRangeMethod.DuplicateExtents;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS: use clonefile or sendfile
            return CopyRangeMethod.Reflink;
        }

        return CopyRangeMethod.Standard;
    }

    public async Task<bool> TryCopyRangeAsync(string srcPath, string dstPath,
        long srcOffset, long dstOffset, long length, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _totalCopies);
        var startTime = DateTime.UtcNow;

        try
        {
            bool success = _activeMethod switch
            {
                CopyRangeMethod.CopyFileRange => await TryCopyFileRangeLinuxAsync(srcPath, dstPath, srcOffset, dstOffset, length, ct),
                CopyRangeMethod.Reflink => await TryReflinkCopyAsync(srcPath, dstPath, srcOffset, dstOffset, length, ct),
                CopyRangeMethod.DuplicateExtents => await TryDuplicateExtentsWindowsAsync(srcPath, dstPath, srcOffset, dstOffset, length, ct),
                CopyRangeMethod.SendFile => await TrySendFileCopyAsync(srcPath, dstPath, srcOffset, dstOffset, length, ct),
                _ => false
            };

            if (success)
            {
                Interlocked.Increment(ref _optimizedCopies);
                Interlocked.Add(ref _optimizedBytesCopied, length);
                Interlocked.Add(ref _totalBytesCopied, length);
                Interlocked.Add(ref _totalCopyTimeTicks, (DateTime.UtcNow - startTime).Ticks);

                _logger.LogTrace("Optimized copy: {Src} -> {Dst}, offset={SrcOffset}->{DstOffset}, len={Length}",
                    srcPath, dstPath, srcOffset, dstOffset, length);
                return true;
            }

            // Fall back to standard copy
            success = await StandardCopyRangeAsync(srcPath, dstPath, srcOffset, dstOffset, length, ct);
            if (success)
            {
                Interlocked.Increment(ref _fallbackCopies);
                Interlocked.Add(ref _totalBytesCopied, length);
            }

            Interlocked.Add(ref _totalCopyTimeTicks, (DateTime.UtcNow - startTime).Ticks);
            return success;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copy range failed: {Src} -> {Dst}", srcPath, dstPath);
            return false;
        }
    }

    public async Task<bool> TryCloneFileAsync(string srcPath, string dstPath, CancellationToken ct = default)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return await TryReflinkCloneAsync(srcPath, dstPath, ct);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return await TryCloneFileMacOSAsync(srcPath, dstPath, ct);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return await TryBlockCloneWindowsAsync(srcPath, dstPath, ct);
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clone file failed: {Src} -> {Dst}", srcPath, dstPath);
            return false;
        }
    }

    public Task<CopyRangeMethod> DetectBestMethodAsync(string path, CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            try
            {
                // Create a small test file to detect capabilities
                var testDir = Path.GetDirectoryName(path) ?? Path.GetTempPath();
                var testSrc = Path.Combine(testDir, $".copytest.{Guid.NewGuid():N}.src");
                var testDst = Path.Combine(testDir, $".copytest.{Guid.NewGuid():N}.dst");

                try
                {
                    // Create test source file
                    File.WriteAllBytes(testSrc, new byte[4096]);

                    // Try various methods
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        if (TryReflinkCloneSync(testSrc, testDst))
                        {
                            File.Delete(testDst);
                            return CopyRangeMethod.Reflink;
                        }
                        return CopyRangeMethod.CopyFileRange;
                    }
                    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        // Check for ReFS or modern NTFS
                        var driveInfo = new DriveInfo(Path.GetPathRoot(path) ?? "C:");
                        if (driveInfo.DriveFormat == "ReFS")
                        {
                            return CopyRangeMethod.DuplicateExtents;
                        }
                        return CopyRangeMethod.Standard;
                    }
                    else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    {
                        // APFS supports clonefile
                        return CopyRangeMethod.Reflink;
                    }

                    return CopyRangeMethod.Standard;
                }
                finally
                {
                    // Cleanup test files
                    try { File.Delete(testSrc); } catch { }
                    try { File.Delete(testDst); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error detecting best copy method for: {Path}", path);
                return CopyRangeMethod.Standard;
            }
        }, ct);
    }

    public CopyRangeStats GetStats()
    {
        return new CopyRangeStats
        {
            TotalCopies = Interlocked.Read(ref _totalCopies),
            OptimizedCopies = Interlocked.Read(ref _optimizedCopies),
            FallbackCopies = Interlocked.Read(ref _fallbackCopies),
            TotalBytesCopied = Interlocked.Read(ref _totalBytesCopied),
            OptimizedBytesCopied = Interlocked.Read(ref _optimizedBytesCopied),
            TotalCopyTime = TimeSpan.FromTicks(Interlocked.Read(ref _totalCopyTimeTicks))
        };
    }

    // Linux copy_file_range syscall
    [DllImport("libc", EntryPoint = "copy_file_range", SetLastError = true)]
    [SupportedOSPlatform("linux")]
    private static extern long linux_copy_file_range(int fd_in, ref long off_in, int fd_out, ref long off_out, long len, uint flags);

    // Linux ioctl for FICLONE
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    [SupportedOSPlatform("linux")]
    private static extern int linux_ioctl(int fd, ulong request, int src_fd);

    private const ulong FICLONE = 0x40049409;

    // macOS clonefile
    [DllImport("libc", EntryPoint = "clonefile", SetLastError = true)]
    [SupportedOSPlatform("macos")]
    private static extern int macos_clonefile(string src, string dst, uint flags);

    private async Task<bool> TryCopyFileRangeLinuxAsync(string srcPath, string dstPath,
        long srcOffset, long dstOffset, long length, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        return await Task.Run(() =>
        {
            try
            {
                using var srcFs = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var dstFs = new FileStream(dstPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);

                var srcHandle = srcFs.SafeFileHandle.DangerousGetHandle().ToInt32();
                var dstHandle = dstFs.SafeFileHandle.DangerousGetHandle().ToInt32();

                long remaining = length;
                while (remaining > 0)
                {
                    ct.ThrowIfCancellationRequested();

                    long srcOff = srcOffset + (length - remaining);
                    long dstOff = dstOffset + (length - remaining);

                    // CA1416: Platform check is done at method entry
#pragma warning disable CA1416
                    long copied = linux_copy_file_range(srcHandle, ref srcOff, dstHandle, ref dstOff, remaining, 0);
#pragma warning restore CA1416
                    if (copied < 0)
                    {
                        _logger.LogTrace("copy_file_range failed: errno={Error}", Marshal.GetLastWin32Error());
                        return false;
                    }
                    if (copied == 0)
                        break;

                    remaining -= copied;
                }

                return remaining == 0;
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "copy_file_range exception");
                return false;
            }
        }, ct);
    }

    private async Task<bool> TryReflinkCopyAsync(string srcPath, string dstPath,
        long srcOffset, long dstOffset, long length, CancellationToken ct)
    {
        // Reflink typically works on whole files, not ranges
        // For ranges, fall back to copy_file_range or standard copy
        return await Task.FromResult(false);
    }

    private Task<bool> TryReflinkCloneAsync(string srcPath, string dstPath, CancellationToken ct)
    {
        return Task.Run(() => TryReflinkCloneSync(srcPath, dstPath), ct);
    }

    private bool TryReflinkCloneSync(string srcPath, string dstPath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return false;

        try
        {
            using var srcFs = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var dstFs = new FileStream(dstPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);

            var srcHandle = srcFs.SafeFileHandle.DangerousGetHandle().ToInt32();
            var dstHandle = dstFs.SafeFileHandle.DangerousGetHandle().ToInt32();

            int result = linux_ioctl(dstHandle, FICLONE, srcHandle);
            return result == 0;
        }
        catch
        {
            return false;
        }
    }

    private Task<bool> TryCloneFileMacOSAsync(string srcPath, string dstPath, CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return Task.FromResult(false);

        return Task.Run(() =>
        {
            try
            {
                // CA1416: Platform check is done at method entry
#pragma warning disable CA1416
                int result = macos_clonefile(srcPath, dstPath, 0);
#pragma warning restore CA1416
                return result == 0;
            }
            catch
            {
                return false;
            }
        }, ct);
    }

    private Task<bool> TryDuplicateExtentsWindowsAsync(string srcPath, string dstPath,
        long srcOffset, long dstOffset, long length, CancellationToken ct)
    {
        // Windows FSCTL_DUPLICATE_EXTENTS_TO_FILE requires ReFS or specific NTFS configurations
        // Implementation would use DeviceIoControl with FSCTL_DUPLICATE_EXTENTS_TO_FILE
        // For simplicity, we return false here and fall back to standard copy
        return Task.FromResult(false);
    }

    private Task<bool> TryBlockCloneWindowsAsync(string srcPath, string dstPath, CancellationToken ct)
    {
        // Similar to DuplicateExtents but for whole files
        return Task.FromResult(false);
    }

    private Task<bool> TrySendFileCopyAsync(string srcPath, string dstPath,
        long srcOffset, long dstOffset, long length, CancellationToken ct)
    {
        // sendfile is typically used for network transfers
        // For file-to-file copies, copy_file_range is preferred
        return Task.FromResult(false);
    }

    private async Task<bool> StandardCopyRangeAsync(string srcPath, string dstPath,
        long srcOffset, long dstOffset, long length, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[Math.Min(length, CopyBufferSize)];

            using var srcFs = new FileStream(srcPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var dstFs = new FileStream(dstPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);

            srcFs.Seek(srcOffset, SeekOrigin.Begin);
            dstFs.Seek(dstOffset, SeekOrigin.Begin);

            long remaining = length;
            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();

                int toRead = (int)Math.Min(remaining, buffer.Length);
                int read = await srcFs.ReadAsync(buffer.AsMemory(0, toRead), ct);

                if (read == 0)
                    break;

                await dstFs.WriteAsync(buffer.AsMemory(0, read), ct);
                remaining -= read;
            }

            return remaining == 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Standard copy range failed: {Src} -> {Dst}", srcPath, dstPath);
            return false;
        }
    }
}
