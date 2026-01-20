using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// Interface for file system operations with case-insensitive path resolution.
/// </summary>
/// <remarks>
/// Mirrors the functionality of casefs.go in Syncthing:
/// - Provides case-insensitive file lookups on case-sensitive file systems
/// - Caches real paths for performance
/// - Handles path normalization and conflict detection
/// </remarks>
public interface ICaseInsensitiveFileSystem : IDisposable
{
    /// <summary>
    /// The base path for all file operations.
    /// </summary>
    string BasePath { get; }

    /// <summary>
    /// Whether to use case-insensitive matching.
    /// </summary>
    bool IsCaseInsensitive { get; }

    /// <summary>
    /// Resolves a path to its actual case on disk.
    /// </summary>
    /// <param name="path">The relative path to resolve.</param>
    /// <returns>The path with correct case, or null if not found.</returns>
    string? GetRealPath(string path);

    /// <summary>
    /// Checks if a file exists (case-insensitive).
    /// </summary>
    bool FileExists(string path);

    /// <summary>
    /// Checks if a directory exists (case-insensitive).
    /// </summary>
    bool DirectoryExists(string path);

    /// <summary>
    /// Opens a file for reading (case-insensitive).
    /// </summary>
    Stream? OpenRead(string path);

    /// <summary>
    /// Opens a file for writing, creating directories as needed.
    /// Uses the exact path provided (does not resolve case).
    /// </summary>
    Stream CreateWrite(string path);

    /// <summary>
    /// Deletes a file (case-insensitive).
    /// </summary>
    bool Delete(string path);

    /// <summary>
    /// Moves/renames a file (case-insensitive source lookup).
    /// </summary>
    bool Move(string sourcePath, string destPath);

    /// <summary>
    /// Gets file information (case-insensitive).
    /// </summary>
    FileInfo? GetFileInfo(string path);

    /// <summary>
    /// Enumerates files in a directory (case-insensitive).
    /// </summary>
    IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly);

    /// <summary>
    /// Enumerates directories in a directory (case-insensitive).
    /// </summary>
    IEnumerable<string> EnumerateDirectories(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly);

    /// <summary>
    /// Clears the path cache.
    /// </summary>
    void ClearCache();

    /// <summary>
    /// Invalidates a specific path in the cache.
    /// </summary>
    void InvalidatePath(string path);
}

/// <summary>
/// Provides case-insensitive file system operations by caching real paths.
/// </summary>
/// <remarks>
/// This is useful for scenarios where:
/// - Windows paths need to work on Linux/macOS
/// - Syncthing folders may have case differences between nodes
/// - You need consistent path handling across platforms
///
/// The implementation caches real paths for performance, as directory
/// enumeration can be expensive on large directories.
/// </remarks>
public class CaseInsensitiveFs : ICaseInsensitiveFileSystem
{
    private readonly ILogger<CaseInsensitiveFs>? _logger;
    private readonly string _basePath;
    private readonly bool _caseInsensitive;
    private readonly ConcurrentDictionary<string, string?> _pathCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxCacheSize;
    private volatile int _cacheHits;
    private volatile int _cacheMisses;

    public string BasePath => _basePath;
    public bool IsCaseInsensitive => _caseInsensitive;

    /// <summary>
    /// Gets cache statistics.
    /// </summary>
    public (int Hits, int Misses, int Size) CacheStats =>
        (_cacheHits, _cacheMisses, _pathCache.Count);

    public CaseInsensitiveFs(
        string basePath,
        bool caseInsensitive = true,
        int maxCacheSize = 10000,
        ILogger<CaseInsensitiveFs>? logger = null)
    {
        _basePath = Path.GetFullPath(basePath);
        _caseInsensitive = caseInsensitive;
        _maxCacheSize = maxCacheSize;
        _logger = logger;

        _logger?.LogDebug(
            "Initialized CaseInsensitiveFs: basePath={BasePath}, caseInsensitive={CaseInsensitive}",
            _basePath,
            _caseInsensitive);
    }

    /// <inheritdoc/>
    public string? GetRealPath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        if (!_caseInsensitive)
        {
            // If case-sensitive mode, just check if path exists
            var fullPath = GetFullPath(path);
            return File.Exists(fullPath) || Directory.Exists(fullPath) ? path : null;
        }

        // Normalize the key
        var normalizedKey = path.ToLowerInvariant();

        // Check cache
        if (_pathCache.TryGetValue(normalizedKey, out var cachedPath))
        {
            Interlocked.Increment(ref _cacheHits);
            return cachedPath;
        }

        Interlocked.Increment(ref _cacheMisses);

        // Find the real path by traversing directories
        var realPath = FindRealPath(path);

        // Cache the result (even null results to avoid repeated lookups)
        if (_pathCache.Count < _maxCacheSize)
        {
            _pathCache[normalizedKey] = realPath;
        }
        else
        {
            // Cache is full, clear oldest entries (simple strategy)
            _logger?.LogDebug("Path cache full, clearing");
            ClearCache();
            _pathCache[normalizedKey] = realPath;
        }

        return realPath;
    }

    private string? FindRealPath(string path)
    {
        try
        {
            // Normalize path separators
            path = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

            var components = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (components.Length == 0)
                return null;

            var currentPath = _basePath;
            var realComponents = new List<string>();

            foreach (var component in components)
            {
                if (component == "." || component == "..")
                {
                    _logger?.LogWarning("Skipping special path component: {Component}", component);
                    continue;
                }

                if (!Directory.Exists(currentPath))
                    return null;

                // Find the actual entry with matching name (case-insensitive)
                var matchingEntry = FindMatchingEntry(currentPath, component);
                if (matchingEntry == null)
                    return null;

                realComponents.Add(matchingEntry);
                currentPath = Path.Combine(currentPath, matchingEntry);
            }

            return string.Join(Path.DirectorySeparatorChar, realComponents);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error finding real path for: {Path}", path);
            return null;
        }
    }

    private string? FindMatchingEntry(string directory, string name)
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var entryName = Path.GetFileName(entry);
                if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return entryName;
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger?.LogWarning(ex, "Access denied when searching directory: {Directory}", directory);
        }
        catch (DirectoryNotFoundException)
        {
            // Directory was deleted between check and enumeration
        }

        return null;
    }

    private string GetFullPath(string path)
    {
        return Path.Combine(_basePath, path);
    }

    /// <inheritdoc/>
    public bool FileExists(string path)
    {
        if (!_caseInsensitive)
        {
            return File.Exists(GetFullPath(path));
        }

        var realPath = GetRealPath(path);
        if (realPath == null)
            return false;

        return File.Exists(GetFullPath(realPath));
    }

    /// <inheritdoc/>
    public bool DirectoryExists(string path)
    {
        if (!_caseInsensitive)
        {
            return Directory.Exists(GetFullPath(path));
        }

        var realPath = GetRealPath(path);
        if (realPath == null)
            return false;

        return Directory.Exists(GetFullPath(realPath));
    }

    /// <inheritdoc/>
    public Stream? OpenRead(string path)
    {
        var realPath = _caseInsensitive ? GetRealPath(path) : path;
        if (realPath == null)
            return null;

        var fullPath = GetFullPath(realPath);
        if (!File.Exists(fullPath))
            return null;

        try
        {
            return File.OpenRead(fullPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error opening file for read: {Path}", path);
            return null;
        }
    }

    /// <inheritdoc/>
    public Stream CreateWrite(string path)
    {
        var fullPath = GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Invalidate any cached path for this location
        InvalidatePath(path);

        return File.Create(fullPath);
    }

    /// <inheritdoc/>
    public bool Delete(string path)
    {
        var realPath = _caseInsensitive ? GetRealPath(path) : path;
        if (realPath == null)
            return false;

        var fullPath = GetFullPath(realPath);

        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                InvalidatePath(path);
                return true;
            }

            if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
                InvalidatePath(path);
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error deleting: {Path}", path);
        }

        return false;
    }

    /// <inheritdoc/>
    public bool Move(string sourcePath, string destPath)
    {
        var realSourcePath = _caseInsensitive ? GetRealPath(sourcePath) : sourcePath;
        if (realSourcePath == null)
            return false;

        var fullSourcePath = GetFullPath(realSourcePath);
        var fullDestPath = GetFullPath(destPath);

        try
        {
            // Ensure destination directory exists
            var destDirectory = Path.GetDirectoryName(fullDestPath);
            if (!string.IsNullOrEmpty(destDirectory) && !Directory.Exists(destDirectory))
            {
                Directory.CreateDirectory(destDirectory);
            }

            if (File.Exists(fullSourcePath))
            {
                File.Move(fullSourcePath, fullDestPath, overwrite: true);
            }
            else if (Directory.Exists(fullSourcePath))
            {
                Directory.Move(fullSourcePath, fullDestPath);
            }
            else
            {
                return false;
            }

            InvalidatePath(sourcePath);
            InvalidatePath(destPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error moving {Source} to {Dest}", sourcePath, destPath);
            return false;
        }
    }

    /// <inheritdoc/>
    public FileInfo? GetFileInfo(string path)
    {
        var realPath = _caseInsensitive ? GetRealPath(path) : path;
        if (realPath == null)
            return null;

        var fullPath = GetFullPath(realPath);
        if (!File.Exists(fullPath))
            return null;

        try
        {
            return new FileInfo(fullPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error getting file info: {Path}", path);
            return null;
        }
    }

    /// <inheritdoc/>
    public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        string fullPath;

        // Handle empty path as base directory
        if (string.IsNullOrEmpty(path))
        {
            fullPath = _basePath;
        }
        else
        {
            var realPath = _caseInsensitive ? GetRealPath(path) : path;
            if (realPath == null)
                yield break;
            fullPath = GetFullPath(realPath);
        }

        if (!Directory.Exists(fullPath))
            yield break;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(fullPath, searchPattern, searchOption);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error enumerating files: {Path}", path);
            yield break;
        }

        foreach (var file in files)
        {
            yield return Path.GetRelativePath(_basePath, file);
        }
    }

    /// <inheritdoc/>
    public IEnumerable<string> EnumerateDirectories(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        string fullPath;

        // Handle empty path as base directory
        if (string.IsNullOrEmpty(path))
        {
            fullPath = _basePath;
        }
        else
        {
            var realPath = _caseInsensitive ? GetRealPath(path) : path;
            if (realPath == null)
                yield break;
            fullPath = GetFullPath(realPath);
        }

        if (!Directory.Exists(fullPath))
            yield break;

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(fullPath, searchPattern, searchOption);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error enumerating directories: {Path}", path);
            yield break;
        }

        foreach (var directory in directories)
        {
            yield return Path.GetRelativePath(_basePath, directory);
        }
    }

    /// <inheritdoc/>
    public void ClearCache()
    {
        var count = _pathCache.Count;
        _pathCache.Clear();
        _cacheHits = 0;
        _cacheMisses = 0;
        _logger?.LogDebug("Cleared path cache ({Count} entries)", count);
    }

    /// <inheritdoc/>
    public void InvalidatePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        var normalizedKey = path.ToLowerInvariant();
        _pathCache.TryRemove(normalizedKey, out _);

        // Also invalidate parent paths
        var parent = Path.GetDirectoryName(path);
        while (!string.IsNullOrEmpty(parent))
        {
            var parentKey = parent.ToLowerInvariant();
            _pathCache.TryRemove(parentKey, out _);
            parent = Path.GetDirectoryName(parent);
        }
    }

    public void Dispose()
    {
        ClearCache();
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Factory for creating case-insensitive file system wrappers.
/// </summary>
public class CaseInsensitiveFsFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public CaseInsensitiveFsFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Creates a case-insensitive file system for the specified path.
    /// </summary>
    public ICaseInsensitiveFileSystem Create(string basePath, bool caseInsensitive = true, int maxCacheSize = 10000)
    {
        var logger = _loggerFactory.CreateLogger<CaseInsensitiveFs>();
        return new CaseInsensitiveFs(basePath, caseInsensitive, maxCacheSize, logger);
    }

    /// <summary>
    /// Creates from a SyncFolder configuration.
    /// </summary>
    public ICaseInsensitiveFileSystem CreateFromFolder(Domain.Entities.SyncFolder folder)
    {
        // Use case-insensitive mode unless explicitly configured otherwise
        var caseInsensitive = !folder.CaseSensitiveFS;
        return Create(folder.Path, caseInsensitive);
    }
}
