namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// Service for handling sparse files efficiently.
/// Based on Syncthing's sparse file support from lib/fs/basicfs.go
///
/// Sparse files are files that contain empty (zero) regions that don't
/// need to be stored on disk, saving storage space for large files
/// with gaps.
///
/// Key behaviors:
/// - Detect sparse file support on the file system
/// - Create files with sparse regions
/// - Seek over zero regions efficiently
/// - Query file allocation information
/// </summary>
public interface ISparseFileHandler
{
    /// <summary>
    /// Check if the file system at the given path supports sparse files.
    /// </summary>
    bool IsSparseSupported(string path);

    /// <summary>
    /// Create a sparse file with the specified size.
    /// </summary>
    /// <param name="path">File path</param>
    /// <param name="size">Total file size</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task CreateSparseFileAsync(string path, long size, CancellationToken cancellationToken = default);

    /// <summary>
    /// Write data to a sparse file, creating sparse regions for zero-filled areas.
    /// </summary>
    /// <param name="path">File path</param>
    /// <param name="offset">Write offset</param>
    /// <param name="data">Data to write</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task WriteSparseAsync(string path, long offset, byte[] data, CancellationToken cancellationToken = default);

    /// <summary>
    /// Punch a hole (deallocate) in a file, creating a sparse region.
    /// </summary>
    /// <param name="path">File path</param>
    /// <param name="offset">Hole start offset</param>
    /// <param name="length">Hole length</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<bool> PunchHoleAsync(string path, long offset, long length, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the actual allocated size of a file on disk.
    /// </summary>
    /// <param name="path">File path</param>
    /// <returns>Allocated size in bytes</returns>
    long GetAllocatedSize(string path);

    /// <summary>
    /// Get sparse regions (holes) in a file.
    /// </summary>
    /// <param name="path">File path</param>
    /// <returns>List of sparse regions</returns>
    IEnumerable<SparseRegion> GetSparseRegions(string path);

    /// <summary>
    /// Check if a specific region of a file is sparse (a hole).
    /// </summary>
    /// <param name="path">File path</param>
    /// <param name="offset">Region start offset</param>
    /// <param name="length">Region length</param>
    bool IsSparseRegion(string path, long offset, long length);

    /// <summary>
    /// Optimize a file by converting zero-filled regions to sparse holes.
    /// </summary>
    /// <param name="path">File path</param>
    /// <param name="minHoleSize">Minimum size for a region to be considered for hole-punching</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of bytes saved</returns>
    Task<long> OptimizeFileAsync(string path, long minHoleSize = 4096, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get statistics about sparse file operations.
    /// </summary>
    SparseFileStatistics GetStatistics();
}

/// <summary>
/// Represents a sparse region (hole) in a file.
/// </summary>
public class SparseRegion
{
    /// <summary>
    /// Start offset of the sparse region.
    /// </summary>
    public long Offset { get; set; }

    /// <summary>
    /// Length of the sparse region.
    /// </summary>
    public long Length { get; set; }

    /// <summary>
    /// End offset (Offset + Length).
    /// </summary>
    public long End => Offset + Length;
}

/// <summary>
/// Statistics for sparse file operations.
/// </summary>
public class SparseFileStatistics
{
    /// <summary>
    /// Total sparse files created.
    /// </summary>
    public long FilesCreated { get; set; }

    /// <summary>
    /// Total holes punched.
    /// </summary>
    public long HolesPunched { get; set; }

    /// <summary>
    /// Total bytes saved through sparse file operations.
    /// </summary>
    public long BytesSaved { get; set; }

    /// <summary>
    /// Total sparse writes performed.
    /// </summary>
    public long SparseWrites { get; set; }

    /// <summary>
    /// Files optimized.
    /// </summary>
    public long FilesOptimized { get; set; }

    /// <summary>
    /// Whether sparse files are supported on the current system.
    /// </summary>
    public bool IsSupported { get; set; }
}
