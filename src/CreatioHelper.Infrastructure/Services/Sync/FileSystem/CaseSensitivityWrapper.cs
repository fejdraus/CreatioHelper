using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// Case sensitivity mode for file system operations (based on Syncthing casefs.go)
/// </summary>
public enum CaseSensitivity
{
    /// <summary>
    /// Auto-detect based on platform (Unix = sensitive, Windows = insensitive)
    /// </summary>
    Auto,

    /// <summary>
    /// Force case-sensitive file operations
    /// </summary>
    ForceCase,

    /// <summary>
    /// Force case-insensitive file operations
    /// </summary>
    IgnoreCase
}

/// <summary>
/// Provides case sensitivity handling for file system operations (based on Syncthing casefs.go)
/// Allows configuring case sensitivity behavior independently of the underlying file system
/// </summary>
public interface ICaseSensitiveFileSystem
{
    /// <summary>
    /// Current case sensitivity mode
    /// </summary>
    CaseSensitivity Mode { get; }

    /// <summary>
    /// Check if the file system is case-sensitive
    /// </summary>
    bool IsCaseSensitive { get; }

    /// <summary>
    /// Normalize a path according to case sensitivity rules
    /// </summary>
    string NormalizePath(string path);

    /// <summary>
    /// Compare two paths for equality
    /// </summary>
    bool PathEquals(string path1, string path2);

    /// <summary>
    /// Find the actual case of a path on disk
    /// </summary>
    Task<string?> GetActualPathAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Check if a file exists with exact case matching
    /// </summary>
    Task<bool> FileExistsExactAsync(string path, CancellationToken ct = default);

    /// <summary>
    /// Detect case conflicts in a directory
    /// </summary>
    Task<IReadOnlyList<CaseConflict>> DetectCaseConflictsAsync(string directory, CancellationToken ct = default);
}

/// <summary>
/// Represents a case conflict between files
/// </summary>
public record CaseConflict(string Path1, string Path2, string NormalizedPath);

/// <summary>
/// Implementation of case-sensitive file system wrapper (based on Syncthing casefs.go)
/// </summary>
public class CaseSensitiveFileSystem : ICaseSensitiveFileSystem
{
    private readonly ILogger<CaseSensitiveFileSystem> _logger;
    private readonly CaseSensitivity _mode;
    private readonly string _basePath;
    private readonly bool _effectivelyCaseSensitive;

    public CaseSensitivity Mode => _mode;
    public bool IsCaseSensitive => _effectivelyCaseSensitive;

    public CaseSensitiveFileSystem(
        ILogger<CaseSensitiveFileSystem> logger,
        string basePath,
        CaseSensitivity mode = CaseSensitivity.Auto)
    {
        _logger = logger;
        _basePath = basePath;
        _mode = mode;
        _effectivelyCaseSensitive = DetermineCaseSensitivity(mode);

        _logger.LogDebug("Initialized case-sensitive FS wrapper: mode={Mode}, effective={Effective}, path={Path}",
            mode, _effectivelyCaseSensitive ? "case-sensitive" : "case-insensitive", basePath);
    }

    /// <summary>
    /// Determine effective case sensitivity based on mode and platform
    /// </summary>
    private static bool DetermineCaseSensitivity(CaseSensitivity mode)
    {
        return mode switch
        {
            CaseSensitivity.ForceCase => true,
            CaseSensitivity.IgnoreCase => false,
            CaseSensitivity.Auto => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
            _ => !RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        };
    }

    /// <summary>
    /// Normalize a path according to case sensitivity rules
    /// </summary>
    public string NormalizePath(string path)
    {
        if (_effectivelyCaseSensitive)
        {
            return path;
        }

        // Normalize to lowercase for case-insensitive comparison
        return path.ToLowerInvariant();
    }

    /// <summary>
    /// Compare two paths for equality
    /// </summary>
    public bool PathEquals(string path1, string path2)
    {
        var comparison = _effectivelyCaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        return string.Equals(path1, path2, comparison);
    }

    /// <summary>
    /// Find the actual case of a path on disk by traversing directories
    /// Returns null if the path doesn't exist
    /// </summary>
    public async Task<string?> GetActualPathAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        try
        {
            var fullPath = Path.GetFullPath(Path.Combine(_basePath, path));

            if (!fullPath.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Path escapes base directory: {Path}", path);
                return null;
            }

            // Start from base path and traverse each component
            var relativePath = Path.GetRelativePath(_basePath, fullPath);
            var components = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var currentPath = _basePath;
            var actualComponents = new List<string>();

            foreach (var component in components)
            {
                if (string.IsNullOrEmpty(component) || component == ".")
                    continue;

                if (component == "..")
                {
                    _logger.LogWarning("Path contains parent directory reference: {Path}", path);
                    return null;
                }

                var found = await FindActualCaseAsync(currentPath, component, ct);
                if (found == null)
                {
                    return null; // Path doesn't exist
                }

                actualComponents.Add(found);
                currentPath = Path.Combine(currentPath, found);
            }

            return Path.Combine(actualComponents.ToArray());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting actual path case for: {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Find the actual case of a filename in a directory
    /// </summary>
    private async Task<string?> FindActualCaseAsync(string directory, string name, CancellationToken ct)
    {
        try
        {
            if (!Directory.Exists(directory))
                return null;

            return await Task.Run(() =>
            {
                var entries = Directory.EnumerateFileSystemEntries(directory);

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();

                    var entryName = Path.GetFileName(entry);
                    if (string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return entryName;
                    }
                }

                return null;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finding actual case for {Name} in {Directory}", name, directory);
            return null;
        }
    }

    /// <summary>
    /// Check if a file exists with exact case matching
    /// </summary>
    public async Task<bool> FileExistsExactAsync(string path, CancellationToken ct = default)
    {
        if (!_effectivelyCaseSensitive)
        {
            // On case-insensitive systems, just check if file exists
            return File.Exists(Path.Combine(_basePath, path));
        }

        var actualPath = await GetActualPathAsync(path, ct);
        if (actualPath == null)
            return false;

        // Check if the actual path matches the requested path exactly
        return string.Equals(path, actualPath, StringComparison.Ordinal);
    }

    /// <summary>
    /// Detect case conflicts in a directory (files that differ only by case)
    /// </summary>
    public async Task<IReadOnlyList<CaseConflict>> DetectCaseConflictsAsync(string directory, CancellationToken ct = default)
    {
        var conflicts = new List<CaseConflict>();

        try
        {
            var fullPath = Path.Combine(_basePath, directory);
            if (!Directory.Exists(fullPath))
                return conflicts;

            return await Task.Run(() =>
            {
                var pathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var entry in Directory.EnumerateFileSystemEntries(fullPath, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();

                    var relativePath = Path.GetRelativePath(fullPath, entry);
                    var normalizedPath = relativePath.ToLowerInvariant();

                    if (pathMap.TryGetValue(normalizedPath, out var existingPath))
                    {
                        // Found a case conflict
                        conflicts.Add(new CaseConflict(existingPath, relativePath, normalizedPath));
                        _logger.LogWarning("Case conflict detected: '{Path1}' vs '{Path2}'", existingPath, relativePath);
                    }
                    else
                    {
                        pathMap[normalizedPath] = relativePath;
                    }
                }

                return conflicts;
            }, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting case conflicts in: {Directory}", directory);
            return conflicts;
        }
    }
}

/// <summary>
/// Factory for creating case-sensitive file system wrappers
/// </summary>
public class CaseSensitiveFileSystemFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public CaseSensitiveFileSystemFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Create a case-sensitive file system wrapper for a path
    /// </summary>
    public ICaseSensitiveFileSystem Create(string basePath, CaseSensitivity mode = CaseSensitivity.Auto)
    {
        var logger = _loggerFactory.CreateLogger<CaseSensitiveFileSystem>();
        return new CaseSensitiveFileSystem(logger, basePath, mode);
    }

    /// <summary>
    /// Create from a SyncFolder configuration
    /// </summary>
    public ICaseSensitiveFileSystem CreateFromFolder(Domain.Entities.SyncFolder folder)
    {
        var mode = folder.CaseSensitiveFS ? CaseSensitivity.ForceCase : CaseSensitivity.Auto;
        return Create(folder.Path, mode);
    }
}
