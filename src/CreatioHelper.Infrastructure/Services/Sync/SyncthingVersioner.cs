using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// 100% Syncthing-compatible file versioning system
/// Based on Syncthing's lib/versioner package (simple.go, util.go)
/// </summary>
public class SyncthingFileVersioner : IDisposable
{
    private readonly ILogger<SyncthingFileVersioner> _logger;
    private readonly string _folderPath;
    private readonly string _versionsPath;
    private readonly int _keepVersions;
    private readonly int _cleanoutDays;
    
    // Syncthing time format: "20060102-150405"
    private const string TimeFormat = "yyyyMMdd-HHmmss";
    private const string DefaultVersionsPath = ".stversions";
    private const string TimeGlob = "[0-9][0-9][0-9][0-9][0-9][0-9][0-9][0-9]-[0-9][0-9][0-9][0-9][0-9][0-9]";
    
    // Regex for extracting version tags: .*~([^~.]+)(?:\.[^.]+)?$
    private static readonly Regex TagRegex = new(@".*~([^~.]+)(?:\.[^.]+)?$", RegexOptions.Compiled);

    public SyncthingFileVersioner(
        ILogger<SyncthingFileVersioner> logger,
        string folderPath,
        int keepVersions = 5,
        int cleanoutDays = 0,
        string? versionsPath = null)
    {
        _logger = logger;
        _folderPath = folderPath;
        _keepVersions = keepVersions;
        _cleanoutDays = cleanoutDays;
        _versionsPath = versionsPath ?? Path.Combine(folderPath, DefaultVersionsPath);
        
        EnsureVersionsDirectoryExists();
        _logger.LogDebug("Syncthing versioner initialized: keep={Keep} cleanout={Cleanout} path={VersionsPath}",
            _keepVersions, _cleanoutDays, _versionsPath);
    }

    /// <summary>
    /// Archive a file by moving it to .stversions with Syncthing timestamp
    /// Returns true if file was archived, false if it didn't exist
    /// </summary>
    public async Task<bool> ArchiveAsync(string filePath)
    {
        var fullPath = Path.Combine(_folderPath, filePath);
        
        if (!File.Exists(fullPath))
        {
            _logger.LogDebug("Not archiving nonexistent file: {FilePath}", filePath);
            return false;
        }

        var fileInfo = new FileInfo(fullPath);
        var timestamp = DateTime.Now.ToString(TimeFormat);
        var versionedFileName = TagFilename(Path.GetFileName(filePath), timestamp);
        var versionedDir = Path.Combine(_versionsPath, Path.GetDirectoryName(filePath) ?? "");
        var versionedPath = Path.Combine(versionedDir, versionedFileName);

        // Ensure directory exists
        Directory.CreateDirectory(versionedDir);

        try
        {
            // Move file to versions directory (Syncthing behavior)
            File.Move(fullPath, versionedPath);
            
            // Preserve modification time
            File.SetLastWriteTime(versionedPath, fileInfo.LastWriteTime);
            
            _logger.LogDebug("Archived {FilePath} to {VersionedPath}", filePath, versionedPath);

            // Clean up old versions
            await CleanupVersionsAsync(filePath);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to archive file: {FilePath}", filePath);
            throw;
        }
    }

    /// <summary>
    /// Restore a file from a specific version timestamp
    /// </summary>
    public async Task<bool> RestoreAsync(string filePath, DateTime versionTime)
    {
        var timestamp = versionTime.ToString(TimeFormat);
        var versionedFileName = TagFilename(Path.GetFileName(filePath), timestamp);
        var versionedDir = Path.Combine(_versionsPath, Path.GetDirectoryName(filePath) ?? "");
        var versionedPath = Path.Combine(versionedDir, versionedFileName);

        if (!File.Exists(versionedPath))
        {
            _logger.LogWarning("Version not found: {VersionedPath}", versionedPath);
            return false;
        }

        var targetPath = Path.Combine(_folderPath, filePath);
        var targetDir = Path.GetDirectoryName(targetPath);
        
        if (!string.IsNullOrEmpty(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        try
        {
            // Archive existing file if it exists
            if (File.Exists(targetPath))
            {
                await ArchiveAsync(filePath);
            }

            // Move version back to main folder
            var versionedFileInfo = new FileInfo(versionedPath);
            File.Move(versionedPath, targetPath);
            File.SetLastWriteTime(targetPath, versionedFileInfo.LastWriteTime);
            
            _logger.LogInformation("Restored {FilePath} from version {Timestamp}", filePath, timestamp);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restore file: {FilePath} from {Timestamp}", filePath, timestamp);
            throw;
        }
    }

    /// <summary>
    /// Get all available versions for a file
    /// </summary>
    public List<FileVersion> GetVersions(string filePath)
    {
        var versions = new List<FileVersion>();
        var versionedDir = Path.Combine(_versionsPath, Path.GetDirectoryName(filePath) ?? "");
        
        if (!Directory.Exists(versionedDir))
            return versions;

        var fileName = Path.GetFileName(filePath);
        var pattern = TagFilename(fileName, TimeGlob).Replace(TimeGlob, "*");
        
        try
        {
            var versionFiles = Directory.GetFiles(versionedDir, pattern);
            
            foreach (var versionFile in versionFiles)
            {
                var (originalName, tag) = UntagFilename(Path.GetFileName(versionFile));
                
                if (originalName != fileName || string.IsNullOrEmpty(tag))
                    continue;

                if (DateTime.TryParseExact(tag, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var versionTime))
                {
                    var fileInfo = new FileInfo(versionFile);
                    versions.Add(new FileVersion
                    {
                        VersionTime = versionTime,
                        ModTime = fileInfo.LastWriteTime,
                        Size = fileInfo.Length,
                        VersionPath = versionFile,
                        OriginalPath = originalName
                    });
                }
            }
            
            versions.Sort((a, b) => b.VersionTime.CompareTo(a.VersionTime));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting versions for: {FilePath}", filePath);
        }

        return versions;
    }

    /// <summary>
    /// Clean all versions according to keep/cleanout rules (Syncthing-compatible)
    /// </summary>
    public async Task CleanAllVersionsAsync()
    {
        if (!Directory.Exists(_versionsPath))
            return;

        _logger.LogDebug("Starting version cleanup in: {VersionsPath}", _versionsPath);

        try
        {
            var versionsPerFile = new Dictionary<string, List<string>>();

            // Walk all version files
            await Task.Run(() =>
            {
                WalkDirectory(_versionsPath, (filePath) =>
                {
                    var relativePath = Path.GetRelativePath(_versionsPath, filePath);
                    var (originalName, tag) = UntagFilename(relativePath);

                    if (!string.IsNullOrEmpty(originalName))
                    {
                        if (!versionsPerFile.ContainsKey(originalName))
                            versionsPerFile[originalName] = new List<string>();
                        
                        versionsPerFile[originalName].Add(filePath);
                    }
                });
            });

            // Clean versions for each file
            foreach (var (fileName, versionFiles) in versionsPerFile)
            {
                await CleanupVersionsForFileAsync(versionFiles);
            }

            _logger.LogDebug("Completed version cleanup");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during version cleanup");
        }
    }

    /// <summary>
    /// Cleanup versions for a specific file
    /// </summary>
    private async Task CleanupVersionsAsync(string filePath)
    {
        var versions = GetVersions(filePath);
        var versionPaths = versions.Select(v => v.VersionPath).ToList();
        
        if (versionPaths.Count > 0)
        {
            await CleanupVersionsForFileAsync(versionPaths);
        }
    }

    /// <summary>
    /// Clean versions for a file according to Syncthing rules
    /// </summary>
    private async Task CleanupVersionsForFileAsync(List<string> versionFiles)
    {
        if (versionFiles.Count == 0)
            return;

        var versionsToRemove = new List<string>();
        
        // Sort versions by timestamp (oldest first)
        var sortedVersions = versionFiles
            .Select(path => new { Path = path, Tag = ExtractTag(Path.GetFileName(path)) })
            .Where(v => !string.IsNullOrEmpty(v.Tag))
            .OrderBy(v => v.Tag)
            .ToList();

        // Remove excess versions (keep only N newest)
        if (sortedVersions.Count > _keepVersions)
        {
            var toRemove = sortedVersions.Take(sortedVersions.Count - _keepVersions);
            versionsToRemove.AddRange(toRemove.Select(v => v.Path));
        }

        // Remove versions older than cleanoutDays
        if (_cleanoutDays > 0)
        {
            var maxAge = TimeSpan.FromDays(_cleanoutDays);
            var cutoffTime = DateTime.Now - maxAge;

            foreach (var version in sortedVersions.Skip(Math.Max(0, sortedVersions.Count - _keepVersions)))
            {
                if (DateTime.TryParseExact(version.Tag, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var versionTime))
                {
                    if (versionTime < cutoffTime)
                    {
                        versionsToRemove.Add(version.Path);
                    }
                }
            }
        }

        // Delete the files
        foreach (var versionPath in versionsToRemove.Distinct())
        {
            try
            {
                if (File.Exists(versionPath))
                {
                    File.Delete(versionPath);
                    _logger.LogDebug("Removed old version: {VersionPath}", versionPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove version: {VersionPath}", versionPath);
            }
        }
    }

    /// <summary>
    /// Tag a filename with timestamp: file.ext -> file~20230808-143022.ext
    /// </summary>
    private static string TagFilename(string name, string tag)
    {
        var dir = Path.GetDirectoryName(name);
        var file = Path.GetFileName(name);
        var ext = Path.GetExtension(file);
        var withoutExt = Path.GetFileNameWithoutExtension(file);
        var taggedFile = $"{withoutExt}~{tag}{ext}";
        
        return string.IsNullOrEmpty(dir) ? taggedFile : Path.Combine(dir, taggedFile);
    }

    /// <summary>
    /// Extract tag from filename: file~20230808-143022.ext -> (file.ext, 20230808-143022)
    /// </summary>
    private static (string name, string tag) UntagFilename(string path)
    {
        var tag = ExtractTag(path);
        if (string.IsNullOrEmpty(tag))
            return ("", "");

        var ext = Path.GetExtension(path);
        var withoutExt = path[..^ext.Length];
        var tagIndex = withoutExt.LastIndexOf($"~{tag}", StringComparison.Ordinal);
        
        if (tagIndex == -1)
            return ("", "");

        var name = withoutExt[..tagIndex] + ext;
        return (name, tag);
    }

    /// <summary>
    /// Extract timestamp tag from filename
    /// </summary>
    private static string ExtractTag(string path)
    {
        var match = TagRegex.Match(path);
        return match.Success && match.Groups.Count == 2 ? match.Groups[1].Value : "";
    }

    private void EnsureVersionsDirectoryExists()
    {
        if (!Directory.Exists(_versionsPath))
        {
            Directory.CreateDirectory(_versionsPath);
            
            // Try to hide the .stversions directory on Windows
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    File.SetAttributes(_versionsPath, File.GetAttributes(_versionsPath) | FileAttributes.Hidden);
                }
            }
            catch
            {
                // Not critical if we can't hide it
            }
        }
    }

    private static void WalkDirectory(string path, Action<string> fileAction)
    {
        try
        {
            foreach (var file in Directory.GetFiles(path))
            {
                fileAction(file);
            }

            foreach (var dir in Directory.GetDirectories(path))
            {
                WalkDirectory(dir, fileAction);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip directories we can't access
        }
    }

    public void Dispose()
    {
        // Nothing to dispose currently
    }
}

