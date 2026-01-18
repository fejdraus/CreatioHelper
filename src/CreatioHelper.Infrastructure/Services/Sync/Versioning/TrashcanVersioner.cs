using System.Globalization;
using System.Text.RegularExpressions;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Versioning;

/// <summary>
/// Trashcan versioning implementation compatible with Syncthing.
/// Simply moves deleted/replaced files to a trash directory and removes them after cleanoutDays.
/// If cleanoutDays is 0, files are kept indefinitely.
/// Based on Syncthing's lib/versioner/trashcan.go
/// </summary>
public class TrashcanVersioner : IVersioner
{
    private readonly ILogger<TrashcanVersioner> _logger;
    private readonly string _folderPath;
    private readonly string _versionsPath;
    private readonly int _cleanoutDays;
    private readonly Timer _cleanupTimer;

    // Syncthing time format
    private const string TimeFormat = "yyyyMMdd-HHmmss";
    private const string DefaultVersionsPath = ".stversions";

    // Regex for extracting version tags
    private static readonly Regex TagRegex = new(@"~(\d{8}-\d{6})(?:\.[^.]+)?$", RegexOptions.Compiled);

    public string VersionerType => "trashcan";
    public string VersionsPath => _versionsPath;

    public TrashcanVersioner(
        ILogger<TrashcanVersioner> logger,
        string folderPath,
        int cleanoutDays = 0, // 0 = keep forever
        string? versionsPath = null,
        int cleanupIntervalS = 3600)
    {
        _logger = logger;
        _folderPath = folderPath;
        _versionsPath = versionsPath ?? Path.Combine(folderPath, DefaultVersionsPath);
        _cleanoutDays = cleanoutDays;

        EnsureVersionsDirectoryExists();

        // Setup periodic cleanup
        if (_cleanoutDays > 0)
        {
            _cleanupTimer = new Timer(
                async _ => await CleanAsync(),
                null,
                TimeSpan.FromSeconds(cleanupIntervalS),
                TimeSpan.FromSeconds(cleanupIntervalS));
        }
        else
        {
            _cleanupTimer = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        }

        _logger.LogDebug("Trashcan versioner initialized: cleanoutDays={CleanoutDays} path={VersionsPath}",
            _cleanoutDays, _versionsPath);
    }

    public async Task ArchiveAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(_folderPath, filePath);

        if (!File.Exists(fullPath))
        {
            _logger.LogDebug("Not archiving nonexistent file: {FilePath}", filePath);
            return;
        }

        var fileInfo = new FileInfo(fullPath);
        var timestamp = DateTime.Now.ToString(TimeFormat);
        var versionedFileName = TagFilename(Path.GetFileName(filePath), timestamp);
        var versionedDir = Path.Combine(_versionsPath, Path.GetDirectoryName(filePath) ?? "");
        var versionedPath = Path.Combine(versionedDir, versionedFileName);

        Directory.CreateDirectory(versionedDir);

        try
        {
            await Task.Run(() =>
            {
                // Move to trash (Syncthing behavior for trashcan)
                File.Move(fullPath, versionedPath);
                File.SetLastWriteTime(versionedPath, fileInfo.LastWriteTime);
            }, cancellationToken);

            _logger.LogDebug("Moved to trashcan: {FilePath} -> {VersionedPath}", filePath, versionedPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move file to trashcan: {FilePath}", filePath);
            throw;
        }
    }

    public async Task<Dictionary<string, List<FileVersion>>> GetVersionsAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, List<FileVersion>>();

        if (!Directory.Exists(_versionsPath))
            return result;

        await Task.Run(() =>
        {
            foreach (var file in Directory.EnumerateFiles(_versionsPath, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(_versionsPath, file);
                var (originalName, tag) = UntagFilename(relativePath);

                if (string.IsNullOrEmpty(originalName) || string.IsNullOrEmpty(tag))
                    continue;

                if (!DateTime.TryParseExact(tag, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var versionTime))
                    continue;

                var fileInfo = new FileInfo(file);
                var version = new FileVersion
                {
                    VersionTime = versionTime,
                    ModTime = fileInfo.LastWriteTime,
                    Size = fileInfo.Length,
                    VersionPath = file,
                    OriginalPath = originalName
                };

                if (!result.TryGetValue(originalName, out var versions))
                {
                    versions = new List<FileVersion>();
                    result[originalName] = versions;
                }

                versions.Add(version);
            }
        }, cancellationToken);

        // Sort versions by time (newest first)
        foreach (var versions in result.Values)
        {
            versions.Sort((a, b) => b.VersionTime.CompareTo(a.VersionTime));
        }

        return result;
    }

    public async Task RestoreAsync(string filePath, DateTime versionTime, CancellationToken cancellationToken = default)
    {
        var timestamp = versionTime.ToString(TimeFormat);
        var versionedFileName = TagFilename(Path.GetFileName(filePath), timestamp);
        var versionedDir = Path.Combine(_versionsPath, Path.GetDirectoryName(filePath) ?? "");
        var versionedPath = Path.Combine(versionedDir, versionedFileName);

        if (!File.Exists(versionedPath))
        {
            _logger.LogWarning("Trashcan version not found: {VersionedPath}", versionedPath);
            throw new FileNotFoundException("Version not found in trashcan", versionedPath);
        }

        var targetPath = Path.Combine(_folderPath, filePath);
        var targetDir = Path.GetDirectoryName(targetPath);

        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        // Archive existing file if it exists
        if (File.Exists(targetPath))
        {
            await ArchiveAsync(filePath, cancellationToken);
        }

        await Task.Run(() =>
        {
            var versionedFileInfo = new FileInfo(versionedPath);
            File.Move(versionedPath, targetPath);
            File.SetLastWriteTime(targetPath, versionedFileInfo.LastWriteTime);
        }, cancellationToken);

        _logger.LogInformation("Restored from trashcan: {FilePath} from version {Timestamp}", filePath, timestamp);
    }

    public async Task CleanAsync(CancellationToken cancellationToken = default)
    {
        if (_cleanoutDays <= 0 || !Directory.Exists(_versionsPath))
            return;

        _logger.LogDebug("Starting trashcan cleanup (cleanoutDays={CleanoutDays}) in: {VersionsPath}",
            _cleanoutDays, _versionsPath);

        var cutoffTime = DateTime.Now - TimeSpan.FromDays(_cleanoutDays);
        var deletedCount = 0;

        try
        {
            await Task.Run(() =>
            {
                foreach (var file in Directory.EnumerateFiles(_versionsPath, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var (_, tag) = UntagFilename(Path.GetFileName(file));

                    if (!DateTime.TryParseExact(tag, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var versionTime))
                        continue;

                    if (versionTime < cutoffTime)
                    {
                        try
                        {
                            File.Delete(file);
                            deletedCount++;
                            _logger.LogDebug("Removed expired trashcan file: {FilePath}", file);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to remove trashcan file: {FilePath}", file);
                        }
                    }
                }

                // Clean up empty directories with depth limit to prevent infinite recursion from symlinks
                CleanupEmptyDirectories(_versionsPath, maxDepth: 100);
            }, cancellationToken);

            _logger.LogDebug("Trashcan cleanup completed, removed {Count} files", deletedCount);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during trashcan cleanup");
        }
    }

    private void CleanupEmptyDirectories(string directory, int maxDepth = 100)
    {
        // Prevent infinite recursion from symlinks or deeply nested structures
        if (maxDepth <= 0) return;

        foreach (var subDir in Directory.GetDirectories(directory))
        {
            CleanupEmptyDirectories(subDir, maxDepth - 1);

            try
            {
                if (!Directory.EnumerateFileSystemEntries(subDir).Any())
                {
                    Directory.Delete(subDir);
                    _logger.LogDebug("Removed empty trashcan directory: {Directory}", subDir);
                }
            }
            catch
            {
                // Ignore errors when cleaning empty directories
            }
        }
    }

    private static string TagFilename(string name, string tag)
    {
        var ext = Path.GetExtension(name);
        var withoutExt = Path.GetFileNameWithoutExtension(name);
        return $"{withoutExt}~{tag}{ext}";
    }

    private static (string name, string tag) UntagFilename(string path)
    {
        var match = TagRegex.Match(path);
        if (!match.Success)
            return ("", "");

        var tag = match.Groups[1].Value;
        var ext = Path.GetExtension(path);

        // Safe bounds check for string slicing
        if (ext.Length >= path.Length)
            return ("", "");

        var withoutExt = path[..^ext.Length];
        var tagIndex = withoutExt.LastIndexOf($"~{tag}", StringComparison.Ordinal);

        if (tagIndex == -1)
            return ("", "");

        var name = withoutExt[..tagIndex] + ext;
        return (name, tag);
    }

    private void EnsureVersionsDirectoryExists()
    {
        if (!Directory.Exists(_versionsPath))
        {
            Directory.CreateDirectory(_versionsPath);

            if (OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetAttributes(_versionsPath, File.GetAttributes(_versionsPath) | FileAttributes.Hidden);
                }
                catch
                {
                    // Not critical
                }
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }
}
