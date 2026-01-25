using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.FileSystem;

/// <summary>
/// Windows implementation for handling junction points, symlinks, and reparse points.
/// Provides cross-platform compatible API with Windows-specific optimizations.
/// Based on Syncthing's reparse point handling logic.
/// </summary>
public class JunctionPointHandler : IJunctionPointHandler
{
    private readonly ILogger<JunctionPointHandler>? _logger;

    /// <summary>
    /// IO_REPARSE_TAG_MOUNT_POINT - Used for junction points on Windows
    /// </summary>
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

    /// <summary>
    /// IO_REPARSE_TAG_SYMLINK - Used for symbolic links on Windows
    /// </summary>
    private const uint IO_REPARSE_TAG_SYMLINK = 0xA000000C;

    /// <summary>
    /// Maximum number of symlink redirections to follow before considering it a loop
    /// Syncthing uses a similar limit to prevent infinite recursion
    /// </summary>
    public const int MaxSymlinkDepth = 40;

    /// <summary>
    /// Creates a new JunctionPointHandler with optional logging support
    /// </summary>
    public JunctionPointHandler()
    {
    }

    /// <summary>
    /// Creates a new JunctionPointHandler with logging support
    /// </summary>
    /// <param name="logger">Logger for diagnostic output</param>
    public JunctionPointHandler(ILogger<JunctionPointHandler> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Checks if the specified path is a junction point (Windows mount point)
    /// </summary>
    /// <param name="path">The path to check</param>
    /// <returns>True if the path is a junction point</returns>
    public bool IsJunctionPoint(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            _logger?.LogTrace("IsJunctionPoint called with null or empty path");
            return false;
        }

        if (!Directory.Exists(path))
        {
            _logger?.LogTrace("Path does not exist as directory: {Path}", path);
            return false;
        }

        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                _logger?.LogTrace("Path is not a reparse point: {Path}", path);
                return false;
            }

            var tag = GetReparseTag(path);
            var isJunction = tag == IO_REPARSE_TAG_MOUNT_POINT;

            _logger?.LogTrace("Path {Path} is junction point: {IsJunction} (tag: 0x{Tag:X8})",
                path, isJunction, tag);

            return isJunction;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error checking if path is junction point: {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// Gets the target path of a junction point or symbolic link
    /// </summary>
    /// <param name="path">The path of the junction point or symlink</param>
    /// <returns>The resolved target path, or null if not a link or resolution failed</returns>
    public string? GetJunctionTarget(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            _logger?.LogTrace("GetJunctionTarget called with null or empty path");
            return null;
        }

        if (!IsJunctionPoint(path) && !IsSymlink(path))
        {
            _logger?.LogTrace("Path is not a junction point or symlink: {Path}", path);
            return null;
        }

        try
        {
            string? linkTarget = null;

            // Try directory first
            if (Directory.Exists(path))
            {
                var dirInfo = new DirectoryInfo(path);
                linkTarget = dirInfo.LinkTarget;
            }
            // Then try file
            else if (File.Exists(path))
            {
                var fileInfo = new FileInfo(path);
                linkTarget = fileInfo.LinkTarget;
            }

            if (linkTarget != null)
            {
                // Resolve relative paths to absolute
                var basePath = Path.GetDirectoryName(path) ?? path;
                var resolvedPath = Path.GetFullPath(linkTarget, basePath);

                _logger?.LogDebug("Resolved junction/symlink target: {Path} -> {Target}",
                    path, resolvedPath);

                return resolvedPath;
            }

            _logger?.LogTrace("Could not resolve link target for: {Path}", path);
            return null;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error getting junction target for: {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Checks if the specified path is a symbolic link
    /// </summary>
    /// <param name="path">The path to check</param>
    /// <returns>True if the path is a symbolic link</returns>
    public bool IsSymlink(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            _logger?.LogTrace("IsSymlink called with null or empty path");
            return false;
        }

        if (!PathExists(path))
        {
            _logger?.LogTrace("Path does not exist: {Path}", path);
            return false;
        }

        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                _logger?.LogTrace("Path is not a reparse point: {Path}", path);
                return false;
            }

            var tag = GetReparseTag(path);
            var isSymlink = tag == IO_REPARSE_TAG_SYMLINK;

            _logger?.LogTrace("Path {Path} is symlink: {IsSymlink} (tag: 0x{Tag:X8})",
                path, isSymlink, tag);

            return isSymlink;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error checking if path is symlink: {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// Checks if the specified path is any type of reparse point (junction, symlink, etc.)
    /// </summary>
    /// <param name="path">The path to check</param>
    /// <returns>True if the path is a reparse point</returns>
    public bool IsReparsePoint(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            _logger?.LogTrace("IsReparsePoint called with null or empty path");
            return false;
        }

        if (!PathExists(path))
        {
            _logger?.LogTrace("Path does not exist: {Path}", path);
            return false;
        }

        try
        {
            var attributes = File.GetAttributes(path);
            var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;

            _logger?.LogTrace("Path {Path} is reparse point: {IsReparsePoint}", path, isReparsePoint);

            return isReparsePoint;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error checking if path is reparse point: {Path}", path);
            return false;
        }
    }

    /// <summary>
    /// Gets the type of reparse point for the specified path
    /// </summary>
    /// <param name="path">The path to check</param>
    /// <returns>The reparse point type</returns>
    public ReparsePointType GetReparsePointType(string path)
    {
        if (string.IsNullOrEmpty(path) || !PathExists(path))
        {
            return ReparsePointType.None;
        }

        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                return ReparsePointType.None;
            }

            var tag = GetReparseTag(path);

            var type = tag switch
            {
                IO_REPARSE_TAG_MOUNT_POINT => ReparsePointType.JunctionPoint,
                IO_REPARSE_TAG_SYMLINK => ReparsePointType.SymbolicLink,
                _ => ReparsePointType.Other
            };

            _logger?.LogDebug("Reparse point type for {Path}: {Type} (tag: 0x{Tag:X8})",
                path, type, tag);

            return type;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error getting reparse point type for: {Path}", path);
            return ReparsePointType.None;
        }
    }

    /// <summary>
    /// Resolves a path through all symlinks/junctions to get the final target
    /// </summary>
    /// <param name="path">The path to resolve</param>
    /// <param name="maxDepth">Maximum number of symlinks to follow (default: MaxSymlinkDepth)</param>
    /// <returns>The fully resolved path, or null if a loop or broken link is detected</returns>
    public string? ResolveFullPath(string path, int maxDepth = MaxSymlinkDepth)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var currentPath = Path.GetFullPath(path);
        var visitedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var depth = 0;

        while (depth < maxDepth)
        {
            if (!visitedPaths.Add(currentPath))
            {
                _logger?.LogWarning("Symlink loop detected at: {Path}", currentPath);
                return null;
            }

            if (!IsReparsePoint(currentPath))
            {
                _logger?.LogTrace("Resolved path after {Depth} redirections: {Path}", depth, currentPath);
                return currentPath;
            }

            var target = GetJunctionTarget(currentPath);
            if (target == null)
            {
                _logger?.LogWarning("Broken symlink or junction at: {Path}", currentPath);
                return null;
            }

            currentPath = target;
            depth++;
        }

        _logger?.LogWarning("Maximum symlink depth ({MaxDepth}) exceeded for: {Path}", maxDepth, path);
        return null;
    }

    /// <summary>
    /// Checks if following the specified path would create a loop
    /// </summary>
    /// <param name="path">The path to check</param>
    /// <param name="visitedPaths">Set of already visited paths</param>
    /// <returns>True if following this path would create a loop</returns>
    public bool WouldCreateLoop(string path, ISet<string> visitedPaths)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var resolvedPath = ResolveFullPath(path);
        if (resolvedPath == null)
        {
            // Resolution failed (loop or broken link already detected)
            return true;
        }

        if (visitedPaths.Contains(resolvedPath))
        {
            _logger?.LogDebug("Path would create loop: {Path} -> {ResolvedPath}", path, resolvedPath);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets information about a reparse point including its target and type
    /// </summary>
    /// <param name="path">The path to get information for</param>
    /// <returns>Reparse point information, or null if not a reparse point</returns>
    public ReparsePointInfo? GetReparsePointInfo(string path)
    {
        if (string.IsNullOrEmpty(path) || !PathExists(path))
        {
            return null;
        }

        var type = GetReparsePointType(path);
        if (type == ReparsePointType.None)
        {
            return null;
        }

        var target = GetJunctionTarget(path);
        var isDirectory = Directory.Exists(path);

        _logger?.LogDebug("Reparse point info for {Path}: Type={Type}, Target={Target}, IsDirectory={IsDirectory}",
            path, type, target ?? "(unresolved)", isDirectory);

        return new ReparsePointInfo
        {
            Path = path,
            Type = type,
            Target = target,
            IsDirectory = isDirectory,
            IsBroken = type != ReparsePointType.None && target == null
        };
    }

    /// <summary>
    /// Checks if a path exists (file or directory)
    /// </summary>
    private static bool PathExists(string path)
    {
        return File.Exists(path) || Directory.Exists(path);
    }

    /// <summary>
    /// Gets the reparse tag for a path using .NET APIs
    /// </summary>
    private uint GetReparseTag(string path)
    {
        try
        {
            // Use .NET 6+ APIs to get reparse point info
            if (Directory.Exists(path))
            {
                var dirInfo = new DirectoryInfo(path);
                if (dirInfo.LinkTarget != null)
                {
                    // Has LinkTarget - it's a symlink
                    return IO_REPARSE_TAG_SYMLINK;
                }

                // Check if it's a reparse point without LinkTarget (junction on Windows)
                var attributes = dirInfo.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return TryGetReparseTagFromPath(path);
                }
            }
            else if (File.Exists(path))
            {
                var fileInfo = new FileInfo(path);
                if (fileInfo.LinkTarget != null)
                {
                    return IO_REPARSE_TAG_SYMLINK;
                }

                var attributes = fileInfo.Attributes;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return TryGetReparseTagFromPath(path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogTrace(ex, "Error getting reparse tag for path: {Path}", path);
        }

        return 0;
    }

    /// <summary>
    /// Attempts to determine the reparse tag from path characteristics
    /// On Windows, junctions typically don't report LinkTarget the same way as symlinks
    /// </summary>
    private uint TryGetReparseTagFromPath(string path)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // On non-Windows, all reparse points are effectively symlinks
            return IO_REPARSE_TAG_SYMLINK;
        }

        try
        {
            // On Windows, check directory info
            if (Directory.Exists(path))
            {
                var dirInfo = new DirectoryInfo(path);

                // If it has ReparsePoint attribute but no LinkTarget, it's likely a junction
                if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (dirInfo.LinkTarget == null)
                    {
                        // This is typically a junction point on Windows
                        _logger?.LogTrace("Path identified as junction point (no LinkTarget): {Path}", path);
                        return IO_REPARSE_TAG_MOUNT_POINT;
                    }
                    return IO_REPARSE_TAG_SYMLINK;
                }
            }
            else if (File.Exists(path))
            {
                var fileInfo = new FileInfo(path);
                if ((fileInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // File symlinks always report as symlinks
                    return IO_REPARSE_TAG_SYMLINK;
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogTrace(ex, "Error determining reparse tag from path: {Path}", path);
        }

        return 0;
    }
}

/// <summary>
/// Types of reparse points supported
/// </summary>
public enum ReparsePointType
{
    /// <summary>
    /// Not a reparse point
    /// </summary>
    None,

    /// <summary>
    /// Windows junction point (mount point)
    /// </summary>
    JunctionPoint,

    /// <summary>
    /// Symbolic link (file or directory)
    /// </summary>
    SymbolicLink,

    /// <summary>
    /// Other type of reparse point
    /// </summary>
    Other
}

/// <summary>
/// Information about a reparse point
/// </summary>
public class ReparsePointInfo
{
    /// <summary>
    /// The path of the reparse point
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// The type of reparse point
    /// </summary>
    public ReparsePointType Type { get; init; }

    /// <summary>
    /// The target path of the reparse point, if resolvable
    /// </summary>
    public string? Target { get; init; }

    /// <summary>
    /// True if the reparse point is a directory
    /// </summary>
    public bool IsDirectory { get; init; }

    /// <summary>
    /// True if the reparse point target cannot be resolved (broken link)
    /// </summary>
    public bool IsBroken { get; init; }
}
