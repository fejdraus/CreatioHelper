using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.FileSystem;

/// <summary>
/// Detects and prevents symlink loops during directory traversal.
/// Based on Syncthing's loop detection algorithm that tracks visited real paths.
/// Thread-safe for concurrent directory scanning operations.
/// </summary>
public class SymlinkLoopDetector
{
    private readonly ILogger<SymlinkLoopDetector>? _logger;
    private readonly IJunctionPointHandler? _junctionPointHandler;
    private readonly HashSet<string> _visitedRealPaths;
    private readonly object _lock = new();

    /// <summary>
    /// Maximum symlink depth to follow before considering it a loop
    /// Matches Syncthing's behavior
    /// </summary>
    public const int DefaultMaxDepth = 40;

    /// <summary>
    /// Gets the current depth tracking for the detection session
    /// </summary>
    public int CurrentDepth { get; private set; }

    /// <summary>
    /// Gets the maximum depth allowed for symlink following
    /// </summary>
    public int MaxDepth { get; }

    /// <summary>
    /// Gets the number of unique paths visited during the current detection session
    /// </summary>
    public int VisitedPathCount
    {
        get
        {
            lock (_lock)
            {
                return _visitedRealPaths.Count;
            }
        }
    }

    /// <summary>
    /// Creates a new SymlinkLoopDetector with default settings
    /// </summary>
    public SymlinkLoopDetector() : this(null, null, DefaultMaxDepth)
    {
    }

    /// <summary>
    /// Creates a new SymlinkLoopDetector with logging support
    /// </summary>
    /// <param name="logger">Logger for diagnostic output</param>
    public SymlinkLoopDetector(ILogger<SymlinkLoopDetector> logger) : this(logger, null, DefaultMaxDepth)
    {
    }

    /// <summary>
    /// Creates a new SymlinkLoopDetector with full configuration
    /// </summary>
    /// <param name="logger">Logger for diagnostic output</param>
    /// <param name="junctionPointHandler">Handler for resolving symlink/junction targets</param>
    /// <param name="maxDepth">Maximum depth of symlinks to follow</param>
    public SymlinkLoopDetector(
        ILogger<SymlinkLoopDetector>? logger,
        IJunctionPointHandler? junctionPointHandler,
        int maxDepth = DefaultMaxDepth)
    {
        _logger = logger;
        _junctionPointHandler = junctionPointHandler;
        MaxDepth = maxDepth;
        _visitedRealPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if visiting the specified path would create a loop.
    /// The path is added to the visited set if it doesn't create a loop.
    /// </summary>
    /// <param name="path">The path to check</param>
    /// <returns>True if visiting this path would create a loop</returns>
    public bool WouldCreateLoop(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            _logger?.LogTrace("WouldCreateLoop called with null or empty path");
            return false;
        }

        var realPath = ResolvePath(path);

        lock (_lock)
        {
            if (!_visitedRealPaths.Add(realPath))
            {
                _logger?.LogDebug("Loop detected: path {Path} resolves to already visited {RealPath}",
                    path, realPath);
                return true;
            }

            _logger?.LogTrace("Path added to visited set: {Path} -> {RealPath} (total: {Count})",
                path, realPath, _visitedRealPaths.Count);
            return false;
        }
    }

    /// <summary>
    /// Checks if the specified path has already been visited
    /// </summary>
    /// <param name="path">The path to check</param>
    /// <returns>True if the path has been visited</returns>
    public bool HasVisited(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var realPath = ResolvePath(path);

        lock (_lock)
        {
            return _visitedRealPaths.Contains(realPath);
        }
    }

    /// <summary>
    /// Marks a path as visited without checking for loops
    /// </summary>
    /// <param name="path">The path to mark as visited</param>
    /// <returns>True if the path was newly added, false if already visited</returns>
    public bool MarkVisited(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var realPath = ResolvePath(path);

        lock (_lock)
        {
            var added = _visitedRealPaths.Add(realPath);

            if (added)
            {
                _logger?.LogTrace("Path marked as visited: {Path} -> {RealPath}", path, realPath);
            }

            return added;
        }
    }

    /// <summary>
    /// Attempts to enter a directory for traversal, checking for loops
    /// </summary>
    /// <param name="path">The directory path to enter</param>
    /// <returns>Result indicating success or failure reason</returns>
    public LoopDetectionResult TryEnterDirectory(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return LoopDetectionResult.InvalidPath("Path is null or empty");
        }

        // Check depth limit
        if (CurrentDepth >= MaxDepth)
        {
            _logger?.LogWarning("Maximum directory depth ({MaxDepth}) exceeded at: {Path}",
                MaxDepth, path);
            return LoopDetectionResult.MaxDepthExceeded(path, MaxDepth);
        }

        var realPath = ResolvePath(path);

        lock (_lock)
        {
            if (!_visitedRealPaths.Add(realPath))
            {
                _logger?.LogDebug("Loop detected when entering directory: {Path} -> {RealPath}",
                    path, realPath);
                return LoopDetectionResult.LoopDetected(path, realPath);
            }

            CurrentDepth++;

            _logger?.LogTrace("Entered directory: {Path} -> {RealPath} (depth: {Depth})",
                path, realPath, CurrentDepth);

            return LoopDetectionResult.Success(realPath);
        }
    }

    /// <summary>
    /// Exits a directory, decreasing the depth counter
    /// </summary>
    public void ExitDirectory()
    {
        lock (_lock)
        {
            if (CurrentDepth > 0)
            {
                CurrentDepth--;
                _logger?.LogTrace("Exited directory (depth: {Depth})", CurrentDepth);
            }
        }
    }

    /// <summary>
    /// Validates a symlink target to ensure it won't cause issues
    /// </summary>
    /// <param name="symlinkPath">The path of the symlink</param>
    /// <param name="targetPath">The target of the symlink</param>
    /// <returns>Result indicating validity of the symlink</returns>
    public SymlinkValidationResult ValidateSymlink(string symlinkPath, string targetPath)
    {
        if (string.IsNullOrEmpty(symlinkPath) || string.IsNullOrEmpty(targetPath))
        {
            return new SymlinkValidationResult
            {
                IsValid = false,
                Reason = SymlinkValidationReason.InvalidPath,
                Message = "Symlink or target path is null or empty"
            };
        }

        // Check if target exists
        if (!File.Exists(targetPath) && !Directory.Exists(targetPath))
        {
            _logger?.LogDebug("Symlink target does not exist: {Symlink} -> {Target}",
                symlinkPath, targetPath);

            return new SymlinkValidationResult
            {
                IsValid = false,
                Reason = SymlinkValidationReason.TargetNotFound,
                Message = $"Target does not exist: {targetPath}"
            };
        }

        // Check for self-reference
        var resolvedSymlink = ResolvePath(symlinkPath);
        var resolvedTarget = ResolvePath(targetPath);

        if (string.Equals(resolvedSymlink, resolvedTarget, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogDebug("Symlink points to itself: {Symlink}", symlinkPath);

            return new SymlinkValidationResult
            {
                IsValid = false,
                Reason = SymlinkValidationReason.SelfReference,
                Message = "Symlink points to itself"
            };
        }

        // Check if target is an ancestor of the symlink (would cause infinite traversal)
        var symlinkDir = Path.GetDirectoryName(resolvedSymlink);
        if (symlinkDir != null && symlinkDir.StartsWith(resolvedTarget, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogDebug("Symlink target is an ancestor of the symlink: {Symlink} -> {Target}",
                symlinkPath, targetPath);

            return new SymlinkValidationResult
            {
                IsValid = false,
                Reason = SymlinkValidationReason.AncestorReference,
                Message = "Symlink target is an ancestor directory"
            };
        }

        // Check if already visited (would cause loop)
        lock (_lock)
        {
            if (_visitedRealPaths.Contains(resolvedTarget))
            {
                _logger?.LogDebug("Symlink target already visited: {Symlink} -> {Target}",
                    symlinkPath, targetPath);

                return new SymlinkValidationResult
                {
                    IsValid = false,
                    Reason = SymlinkValidationReason.AlreadyVisited,
                    Message = "Target path has already been visited"
                };
            }
        }

        return new SymlinkValidationResult
        {
            IsValid = true,
            Reason = SymlinkValidationReason.Valid,
            ResolvedTarget = resolvedTarget
        };
    }

    /// <summary>
    /// Clears all visited paths and resets the depth counter
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            var previousCount = _visitedRealPaths.Count;
            _visitedRealPaths.Clear();
            CurrentDepth = 0;

            _logger?.LogDebug("Loop detector reset (cleared {Count} visited paths)", previousCount);
        }
    }

    /// <summary>
    /// Gets a snapshot of all visited paths
    /// </summary>
    /// <returns>A read-only set of visited paths</returns>
    public IReadOnlySet<string> GetVisitedPaths()
    {
        lock (_lock)
        {
            return new HashSet<string>(_visitedRealPaths, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Creates a child detector that shares visited paths with this detector
    /// but has its own depth tracking. Useful for parallel directory traversal.
    /// </summary>
    /// <returns>A new detector sharing the visited paths</returns>
    public SymlinkLoopDetector CreateChildDetector()
    {
        var child = new SymlinkLoopDetector(_logger, _junctionPointHandler, MaxDepth);

        lock (_lock)
        {
            foreach (var path in _visitedRealPaths)
            {
                child._visitedRealPaths.Add(path);
            }
        }

        _logger?.LogTrace("Created child detector with {Count} pre-visited paths",
            child._visitedRealPaths.Count);

        return child;
    }

    /// <summary>
    /// Resolves a path to its real path, following symlinks if a handler is available
    /// </summary>
    private string ResolvePath(string path)
    {
        try
        {
            // If we have a junction point handler, use it to resolve symlinks
            if (_junctionPointHandler != null && _junctionPointHandler.IsReparsePoint(path))
            {
                var target = _junctionPointHandler.GetJunctionTarget(path);
                if (target != null)
                {
                    return Path.GetFullPath(target);
                }
            }

            return Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            _logger?.LogTrace(ex, "Error resolving path: {Path}", path);
            return Path.GetFullPath(path);
        }
    }
}

/// <summary>
/// Result of a loop detection check
/// </summary>
public class LoopDetectionResult
{
    /// <summary>
    /// Whether the operation was successful (no loop detected)
    /// </summary>
    public bool IsSuccess { get; init; }

    /// <summary>
    /// The type of failure, if any
    /// </summary>
    public LoopDetectionFailure FailureType { get; init; }

    /// <summary>
    /// The resolved path (only set on success)
    /// </summary>
    public string? ResolvedPath { get; init; }

    /// <summary>
    /// The path where the loop was detected (only set on loop detection)
    /// </summary>
    public string? LoopPath { get; init; }

    /// <summary>
    /// Human-readable error message
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Creates a successful result
    /// </summary>
    public static LoopDetectionResult Success(string resolvedPath) => new()
    {
        IsSuccess = true,
        FailureType = LoopDetectionFailure.None,
        ResolvedPath = resolvedPath
    };

    /// <summary>
    /// Creates a result indicating a loop was detected
    /// </summary>
    public static LoopDetectionResult LoopDetected(string path, string loopPath) => new()
    {
        IsSuccess = false,
        FailureType = LoopDetectionFailure.LoopDetected,
        LoopPath = loopPath,
        Message = $"Loop detected: {path} -> {loopPath}"
    };

    /// <summary>
    /// Creates a result indicating max depth was exceeded
    /// </summary>
    public static LoopDetectionResult MaxDepthExceeded(string path, int maxDepth) => new()
    {
        IsSuccess = false,
        FailureType = LoopDetectionFailure.MaxDepthExceeded,
        Message = $"Maximum depth ({maxDepth}) exceeded at: {path}"
    };

    /// <summary>
    /// Creates a result indicating an invalid path
    /// </summary>
    public static LoopDetectionResult InvalidPath(string message) => new()
    {
        IsSuccess = false,
        FailureType = LoopDetectionFailure.InvalidPath,
        Message = message
    };
}

/// <summary>
/// Types of loop detection failures
/// </summary>
public enum LoopDetectionFailure
{
    /// <summary>
    /// No failure (success)
    /// </summary>
    None,

    /// <summary>
    /// A loop was detected in the path
    /// </summary>
    LoopDetected,

    /// <summary>
    /// Maximum traversal depth was exceeded
    /// </summary>
    MaxDepthExceeded,

    /// <summary>
    /// The path is invalid
    /// </summary>
    InvalidPath
}

/// <summary>
/// Result of symlink validation
/// </summary>
public class SymlinkValidationResult
{
    /// <summary>
    /// Whether the symlink is valid
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// The reason for the validation result
    /// </summary>
    public SymlinkValidationReason Reason { get; init; }

    /// <summary>
    /// Human-readable message describing the result
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// The resolved target path (only set on success)
    /// </summary>
    public string? ResolvedTarget { get; init; }
}

/// <summary>
/// Reasons for symlink validation results
/// </summary>
public enum SymlinkValidationReason
{
    /// <summary>
    /// Symlink is valid
    /// </summary>
    Valid,

    /// <summary>
    /// Path is null or empty
    /// </summary>
    InvalidPath,

    /// <summary>
    /// Target does not exist
    /// </summary>
    TargetNotFound,

    /// <summary>
    /// Symlink points to itself
    /// </summary>
    SelfReference,

    /// <summary>
    /// Target is an ancestor directory (would cause infinite traversal)
    /// </summary>
    AncestorReference,

    /// <summary>
    /// Target has already been visited
    /// </summary>
    AlreadyVisited
}
