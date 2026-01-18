using System.Globalization;
using System.Text.RegularExpressions;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Versioning;

/// <summary>
/// Simple versioning implementation compatible with Syncthing.
/// Keeps the last N versions of each file and removes versions older than cleanoutDays.
/// Based on Syncthing's lib/versioner/simple.go
/// </summary>
public class SimpleVersioner : IVersioner
{
    private readonly ILogger<SimpleVersioner> _logger;
    private readonly string _folderPath;
    private readonly string _versionsPath;
    private readonly int _keepVersions;
    private readonly int _cleanoutDays;
    private readonly Timer _cleanupTimer;

    // Syncthing time format
    private const string TimeFormat = "yyyyMMdd-HHmmss";
    private const string DefaultVersionsPath = ".stversions";

    // Regex for extracting version tags
    private static readonly Regex TagRegex = new(@"~(\d{8}-\d{6})(?:\.[^.]+)?$", RegexOptions.Compiled);

    public string VersionerType => "simple";
    public string VersionsPath => _versionsPath;

    public SimpleVersioner(
        ILogger<SimpleVersioner> logger,
        string folderPath,
        int keepVersions = 5,
        int cleanoutDays = 0,
        string? versionsPath = null,
        int cleanupIntervalS = 3600)
    {
        _logger = logger;
        _folderPath = folderPath;
        _versionsPath = versionsPath ?? Path.Combine(folderPath, DefaultVersionsPath);
        _keepVersions = keepVersions;
        _cleanoutDays = cleanoutDays;

        EnsureVersionsDirectoryExists();

        // Setup periodic cleanup
        _cleanupTimer = new Timer(
            async _ => await CleanAsync(),
            null,
            TimeSpan.FromSeconds(cleanupIntervalS),
            TimeSpan.FromSeconds(cleanupIntervalS));

        _logger.LogDebug("Simple versioner initialized: keep={Keep} cleanoutDays={CleanoutDays} path={VersionsPath}",
            _keepVersions, _cleanoutDays, _versionsPath);
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
                File.Move(fullPath, versionedPath);
                File.SetLastWriteTime(versionedPath, fileInfo.LastWriteTime);
            }, cancellationToken);

            _logger.LogDebug("Archived {FilePath} to {VersionedPath}", filePath, versionedPath);

            // Clean up old versions for this file
            await CleanupVersionsForFileAsync(filePath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to archive file: {FilePath}", filePath);
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
            _logger.LogWarning("Version not found: {VersionedPath}", versionedPath);
            throw new FileNotFoundException("Version not found", versionedPath);
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

        _logger.LogInformation("Restored {FilePath} from version {Timestamp}", filePath, timestamp);
    }

    public async Task CleanAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_versionsPath))
            return;

        _logger.LogDebug("Starting simple version cleanup in: {VersionsPath}", _versionsPath);

        try
        {
            var allVersions = await GetVersionsAsync(cancellationToken);

            foreach (var (filePath, _) in allVersions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await CleanupVersionsForFileAsync(filePath, cancellationToken);
            }

            _logger.LogDebug("Completed simple version cleanup");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during simple version cleanup");
        }
    }

    private async Task CleanupVersionsForFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var versionedDir = Path.Combine(_versionsPath, Path.GetDirectoryName(filePath) ?? "");
        var fileName = Path.GetFileName(filePath);

        if (!Directory.Exists(versionedDir))
            return;

        var pattern = $"{Path.GetFileNameWithoutExtension(fileName)}~*{Path.GetExtension(fileName)}";
        var now = DateTime.Now;
        var cutoffTime = _cleanoutDays > 0 ? now - TimeSpan.FromDays(_cleanoutDays) : DateTime.MinValue;

        var versionFiles = Directory.GetFiles(versionedDir, pattern)
            .Select(file =>
            {
                var (_, tag) = UntagFilename(Path.GetFileName(file));
                if (DateTime.TryParseExact(tag, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var versionTime))
                {
                    return new { Path = file, VersionTime = versionTime };
                }
                return null;
            })
            .Where(v => v != null)
            .OrderByDescending(v => v!.VersionTime)
            .ToList();

        var toDelete = new HashSet<string>();

        // Keep only the newest N versions
        if (versionFiles.Count > _keepVersions)
        {
            foreach (var v in versionFiles.Skip(_keepVersions))
            {
                toDelete.Add(v!.Path);
            }
        }

        // Remove versions older than cleanoutDays (check ALL versions, not just kept ones)
        if (_cleanoutDays > 0)
        {
            foreach (var version in versionFiles)
            {
                if (version!.VersionTime < cutoffTime)
                {
                    toDelete.Add(version.Path);
                }
            }
        }

        await Task.Run(() =>
        {
            foreach (var path in toDelete)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        _logger.LogDebug("Removed old version: {VersionPath}", path);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove version: {VersionPath}", path);
                }
            }
        }, cancellationToken);
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
