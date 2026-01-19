namespace CreatioHelper.Infrastructure.Services.Performance;

/// <summary>
/// Service for limiting concurrent file write operations.
/// Based on Syncthing's maxConcurrentWrites configuration (folderconfiguration.go)
///
/// This limits the total number of files being written to simultaneously,
/// preventing disk I/O saturation and improving overall throughput.
///
/// Key behaviors:
/// - Limit total concurrent file writes
/// - Limit writes per folder
/// - Track write operation statistics
/// - Provide write slots with acquire/release semantics
/// </summary>
public interface IConcurrentWriteLimiter
{
    /// <summary>
    /// Try to acquire a write slot for a file.
    /// </summary>
    /// <param name="filePath">Full path of the file to write</param>
    /// <param name="folderId">Folder identifier (optional, for per-folder limits)</param>
    /// <returns>Write slot if acquired, null otherwise</returns>
    IWriteSlot? TryAcquire(string filePath, string? folderId = null);

    /// <summary>
    /// Acquire a write slot, waiting if necessary.
    /// </summary>
    /// <param name="filePath">Full path of the file to write</param>
    /// <param name="folderId">Folder identifier (optional)</param>
    /// <param name="timeout">Maximum wait time</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Write slot</returns>
    Task<IWriteSlot?> AcquireAsync(string filePath, string? folderId = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get current write count for a folder.
    /// </summary>
    int GetFolderWriteCount(string folderId);

    /// <summary>
    /// Get total active write count.
    /// </summary>
    int TotalWriteCount { get; }

    /// <summary>
    /// Check if writes are allowed for a folder.
    /// </summary>
    bool CanWrite(string? folderId = null);

    /// <summary>
    /// Update configuration.
    /// </summary>
    void UpdateConfiguration(ConcurrentWriteLimiterConfiguration configuration);

    /// <summary>
    /// Set per-folder write limit.
    /// </summary>
    void SetFolderLimit(string folderId, int maxWrites);

    /// <summary>
    /// Remove per-folder write limit override.
    /// </summary>
    void RemoveFolderLimit(string folderId);

    /// <summary>
    /// Get current statistics.
    /// </summary>
    ConcurrentWriteLimiterStatistics GetStatistics();
}

/// <summary>
/// Represents an acquired write slot that must be disposed when the write completes.
/// </summary>
public interface IWriteSlot : IDisposable
{
    /// <summary>
    /// File path being written.
    /// </summary>
    string FilePath { get; }

    /// <summary>
    /// Folder ID (if specified).
    /// </summary>
    string? FolderId { get; }

    /// <summary>
    /// When the slot was acquired.
    /// </summary>
    DateTime AcquiredAt { get; }

    /// <summary>
    /// Bytes written so far (for tracking).
    /// </summary>
    long BytesWritten { get; set; }

    /// <summary>
    /// Whether the slot is still valid.
    /// </summary>
    bool IsValid { get; }
}

/// <summary>
/// Configuration for concurrent write limiting.
/// </summary>
public class ConcurrentWriteLimiterConfiguration
{
    /// <summary>
    /// Maximum total concurrent file writes (0 = unlimited).
    /// Default matches Syncthing's default of 2.
    /// </summary>
    public int MaxConcurrentWrites { get; set; } = 2;

    /// <summary>
    /// Maximum concurrent writes per folder (0 = unlimited).
    /// </summary>
    public int MaxWritesPerFolder { get; set; } = 0;

    /// <summary>
    /// Default timeout for acquiring write slots.
    /// </summary>
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Per-folder overrides for max concurrent writes.
    /// </summary>
    public Dictionary<string, int> FolderOverrides { get; set; } = new();

    /// <summary>
    /// Whether to track write duration statistics.
    /// </summary>
    public bool TrackStatistics { get; set; } = true;

    /// <summary>
    /// Whether to prioritize smaller files (finish faster, release slots sooner).
    /// </summary>
    public bool PrioritizeSmallerFiles { get; set; } = false;

    /// <summary>
    /// File size threshold for priority boost (bytes).
    /// Files smaller than this get priority if PrioritizeSmallerFiles is true.
    /// </summary>
    public long SmallFileSizeThreshold { get; set; } = 1024 * 1024; // 1 MB
}

/// <summary>
/// Statistics for concurrent write limiting.
/// </summary>
public class ConcurrentWriteLimiterStatistics
{
    /// <summary>
    /// Current active writes.
    /// </summary>
    public int ActiveWrites { get; set; }

    /// <summary>
    /// Peak concurrent writes.
    /// </summary>
    public int PeakWrites { get; set; }

    /// <summary>
    /// Total writes completed.
    /// </summary>
    public long TotalWritesCompleted { get; set; }

    /// <summary>
    /// Total writes rejected (limit exceeded).
    /// </summary>
    public long TotalWritesRejected { get; set; }

    /// <summary>
    /// Total bytes written.
    /// </summary>
    public long TotalBytesWritten { get; set; }

    /// <summary>
    /// Writes by folder.
    /// </summary>
    public Dictionary<string, int> WritesByFolder { get; set; } = new();

    /// <summary>
    /// Average write duration.
    /// </summary>
    public TimeSpan AverageWriteDuration { get; set; }

    /// <summary>
    /// Current waiting requests.
    /// </summary>
    public int WaitingRequests { get; set; }

    /// <summary>
    /// Files currently being written.
    /// </summary>
    public List<string> ActiveFiles { get; set; } = new();
}
