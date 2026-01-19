using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// Implementation of sparse file handling.
/// Uses platform-specific APIs for Windows, with fallback for other platforms.
/// </summary>
public class SparseFileHandler : ISparseFileHandler
{
    private readonly ILogger<SparseFileHandler> _logger;
    private readonly object _statsLock = new();

    private long _filesCreated;
    private long _holesPunched;
    private long _bytesSaved;
    private long _sparseWrites;
    private long _filesOptimized;
    private bool? _isSupportedCached;

    public SparseFileHandler(ILogger<SparseFileHandler> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsSparseSupported(string path)
    {
        if (_isSupportedCached.HasValue)
            return _isSupportedCached.Value;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                _isSupportedCached = IsSupportedOnWindows(path);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                // Most Linux file systems support sparse files (ext4, xfs, btrfs)
                _isSupportedCached = true;
            }
            else
            {
                // macOS APFS supports sparse files
                _isSupportedCached = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to detect sparse file support for {Path}", path);
            _isSupportedCached = false;
        }

        return _isSupportedCached.Value;
    }

    /// <inheritdoc />
    public async Task CreateSparseFileAsync(string path, long size, CancellationToken cancellationToken = default)
    {
        if (size < 0)
            throw new ArgumentOutOfRangeException(nameof(size));

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await CreateSparseFileWindowsAsync(path, size, cancellationToken);
        }
        else
        {
            await CreateSparseFileUnixAsync(path, size, cancellationToken);
        }

        Interlocked.Increment(ref _filesCreated);
        _logger.LogDebug("Created sparse file {Path} with size {Size}", path, size);
    }

    /// <inheritdoc />
    public async Task WriteSparseAsync(string path, long offset, byte[] data, CancellationToken cancellationToken = default)
    {
        if (data == null || data.Length == 0)
            return;

        // Check if data is all zeros - if so, skip writing (leave as sparse)
        if (IsAllZeros(data))
        {
            _logger.LogDebug("Skipping write of zero-filled data at offset {Offset} for sparse file {Path}",
                offset, path);
            Interlocked.Add(ref _bytesSaved, data.Length);
            return;
        }

        await using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        stream.Seek(offset, SeekOrigin.Begin);
        await stream.WriteAsync(data, cancellationToken);

        Interlocked.Increment(ref _sparseWrites);
    }

    /// <inheritdoc />
    public async Task<bool> PunchHoleAsync(string path, long offset, long length, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return false;

        if (length <= 0)
            return false;

        bool success;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            success = await PunchHoleWindowsAsync(path, offset, length, cancellationToken);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            success = await PunchHoleLinuxAsync(path, offset, length, cancellationToken);
        }
        else
        {
            // Fallback: write zeros (not truly sparse, but maintains file integrity)
            success = await PunchHoleFallbackAsync(path, offset, length, cancellationToken);
        }

        if (success)
        {
            Interlocked.Increment(ref _holesPunched);
            Interlocked.Add(ref _bytesSaved, length);
            _logger.LogDebug("Punched hole in {Path} at offset {Offset}, length {Length}",
                path, offset, length);
        }

        return success;
    }

    /// <inheritdoc />
    public long GetAllocatedSize(string path)
    {
        if (!File.Exists(path))
            return 0;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return GetAllocatedSizeWindows(path);
            }
            else
            {
                return GetAllocatedSizeUnix(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get allocated size for {Path}", path);
            return new FileInfo(path).Length;
        }
    }

    /// <inheritdoc />
    public IEnumerable<SparseRegion> GetSparseRegions(string path)
    {
        if (!File.Exists(path))
            yield break;

        var fileInfo = new FileInfo(path);
        var logicalSize = fileInfo.Length;
        var allocatedSize = GetAllocatedSize(path);

        // If allocated size equals logical size, no sparse regions
        if (allocatedSize >= logicalSize)
            yield break;

        // For Windows, we can query actual sparse regions
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var region in GetSparseRegionsWindows(path))
            {
                yield return region;
            }
        }
        else
        {
            // On other platforms, we can only estimate based on size difference
            // A proper implementation would use SEEK_HOLE/SEEK_DATA on Linux
            var savedBytes = logicalSize - allocatedSize;
            if (savedBytes > 0)
            {
                yield return new SparseRegion
                {
                    Offset = 0,
                    Length = savedBytes // Approximate
                };
            }
        }
    }

    /// <inheritdoc />
    public bool IsSparseRegion(string path, long offset, long length)
    {
        var regions = GetSparseRegions(path);
        return regions.Any(r => r.Offset <= offset && r.End >= offset + length);
    }

    /// <inheritdoc />
    public async Task<long> OptimizeFileAsync(string path, long minHoleSize = 4096, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            return 0;

        if (!IsSparseSupported(path))
            return 0;

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length < minHoleSize)
            return 0;

        long bytesSaved = 0;
        var buffer = new byte[minHoleSize];

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long offset = 0;

        while (offset < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytesRead = await stream.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
                break;

            // Check if this block is all zeros
            if (IsAllZeros(buffer.AsSpan(0, bytesRead)))
            {
                // This is a candidate for hole-punching
                var success = await PunchHoleAsync(path, offset, bytesRead, cancellationToken);
                if (success)
                {
                    bytesSaved += bytesRead;
                }
            }

            offset += bytesRead;
        }

        if (bytesSaved > 0)
        {
            Interlocked.Increment(ref _filesOptimized);
            _logger.LogInformation("Optimized file {Path}, saved {BytesSaved} bytes", path, bytesSaved);
        }

        return bytesSaved;
    }

    /// <inheritdoc />
    public SparseFileStatistics GetStatistics()
    {
        return new SparseFileStatistics
        {
            FilesCreated = Interlocked.Read(ref _filesCreated),
            HolesPunched = Interlocked.Read(ref _holesPunched),
            BytesSaved = Interlocked.Read(ref _bytesSaved),
            SparseWrites = Interlocked.Read(ref _sparseWrites),
            FilesOptimized = Interlocked.Read(ref _filesOptimized),
            IsSupported = _isSupportedCached ?? false
        };
    }

    #region Windows Implementation

    [SupportedOSPlatform("windows")]
    private static bool IsSupportedOnWindows(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
                return false;

            var driveInfo = new DriveInfo(root);
            // NTFS and ReFS support sparse files
            return driveInfo.DriveFormat == "NTFS" || driveInfo.DriveFormat == "ReFS";
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task CreateSparseFileWindowsAsync(string path, long size, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous);

        // Mark as sparse using DeviceIoControl
        SetSparseFlag(stream.SafeFileHandle);

        // Set the file size
        stream.SetLength(size);
    }

    [SupportedOSPlatform("windows")]
    private static void SetSparseFlag(SafeFileHandle handle)
    {
        const int FSCTL_SET_SPARSE = 0x000900c4;

        var result = DeviceIoControl(
            handle,
            FSCTL_SET_SPARSE,
            IntPtr.Zero, 0,
            IntPtr.Zero, 0,
            out _,
            IntPtr.Zero);

        if (!result)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [SupportedOSPlatform("windows")]
    private async Task<bool> PunchHoleWindowsAsync(string path, long offset, long length, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.Read);

            // Ensure file is marked as sparse
            SetSparseFlag(stream.SafeFileHandle);

            // Set zero data (creates hole)
            var zeroData = new FILE_ZERO_DATA_INFORMATION
            {
                FileOffset = offset,
                BeyondFinalZero = offset + length
            };

            const int FSCTL_SET_ZERO_DATA = 0x000980c8;
            var size = Marshal.SizeOf<FILE_ZERO_DATA_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);

            try
            {
                Marshal.StructureToPtr(zeroData, ptr, false);

                var result = DeviceIoControl(
                    stream.SafeFileHandle,
                    FSCTL_SET_ZERO_DATA,
                    ptr, (uint)size,
                    IntPtr.Zero, 0,
                    out _,
                    IntPtr.Zero);

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to punch hole in {Path}", path);
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private long GetAllocatedSizeWindows(string path)
    {
        uint high;
        var low = GetCompressedFileSize(path, out high);

        if (low == 0xFFFFFFFF)
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 0)
            {
                return new FileInfo(path).Length;
            }
        }

        return ((long)high << 32) | low;
    }

    [SupportedOSPlatform("windows")]
    private IEnumerable<SparseRegion> GetSparseRegionsWindows(string path)
    {
        // Simplified implementation - returns approximate regions
        // A full implementation would use FSCTL_QUERY_ALLOCATED_RANGES
        var fileInfo = new FileInfo(path);
        var allocated = GetAllocatedSizeWindows(path);
        var saved = fileInfo.Length - allocated;

        if (saved > 0)
        {
            yield return new SparseRegion { Offset = 0, Length = saved };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ZERO_DATA_INFORMATION
    {
        public long FileOffset;
        public long BeyondFinalZero;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        int dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetCompressedFileSize(string lpFileName, out uint lpFileSizeHigh);

    #endregion

    #region Unix Implementation

    private async Task CreateSparseFileUnixAsync(string path, long size, CancellationToken cancellationToken)
    {
        // On Unix, just creating a file and setting its length creates a sparse file
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous);

        stream.SetLength(size);
    }

    private async Task<bool> PunchHoleLinuxAsync(string path, long offset, long length, CancellationToken cancellationToken)
    {
        // On Linux, we would use fallocate with FALLOC_FL_PUNCH_HOLE
        // For simplicity, use fallback implementation
        return await PunchHoleFallbackAsync(path, offset, length, cancellationToken);
    }

    private long GetAllocatedSizeUnix(string path)
    {
        // On Unix, we would use stat() to get st_blocks
        // For simplicity, return file size
        return new FileInfo(path).Length;
    }

    #endregion

    #region Fallback Implementation

    private async Task<bool> PunchHoleFallbackAsync(string path, long offset, long length, CancellationToken cancellationToken)
    {
        // Fallback: write zeros (doesn't actually create sparse hole)
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read);
            stream.Seek(offset, SeekOrigin.Begin);

            var buffer = new byte[Math.Min(length, 64 * 1024)];
            var remaining = length;

            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var toWrite = (int)Math.Min(remaining, buffer.Length);
                await stream.WriteAsync(buffer.AsMemory(0, toWrite), cancellationToken);
                remaining -= toWrite;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Helpers

    private static bool IsAllZeros(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            if (b != 0)
                return false;
        }
        return true;
    }

    #endregion
}
