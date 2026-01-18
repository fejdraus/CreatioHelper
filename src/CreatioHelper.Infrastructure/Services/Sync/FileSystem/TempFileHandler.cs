using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// Handles .syncthing.* temporary files during sync operations (based on Syncthing folder.go)
/// Temporary files are used for atomic file writes to prevent corruption during sync
/// </summary>
public interface ITempFileHandler
{
    /// <summary>
    /// Prefix for temporary files
    /// </summary>
    string TempPrefix { get; }

    /// <summary>
    /// Create a temporary file path for a target file
    /// </summary>
    string GetTempFilePath(string targetPath);

    /// <summary>
    /// Finalize a temporary file by moving it to the target location
    /// </summary>
    Task<bool> FinalizeTempFileAsync(string tempPath, string targetPath, CancellationToken ct = default);

    /// <summary>
    /// Clean up a temporary file
    /// </summary>
    Task CleanupTempFileAsync(string tempPath, CancellationToken ct = default);

    /// <summary>
    /// Clean up all orphaned temporary files in a directory
    /// </summary>
    Task<int> CleanupOrphanedTempFilesAsync(string directory, TimeSpan maxAge, CancellationToken ct = default);

    /// <summary>
    /// Check if a path is a temporary file
    /// </summary>
    bool IsTempFile(string path);

    /// <summary>
    /// Get active temporary file count
    /// </summary>
    int ActiveTempFileCount { get; }
}

/// <summary>
/// Implementation of temporary file handling (based on Syncthing folder.go)
/// Uses .syncthing.* prefix for temporary files during sync
/// </summary>
public class TempFileHandler : ITempFileHandler
{
    private readonly ILogger<TempFileHandler> _logger;
    private readonly string _basePath;
    private readonly TempFileOptions _options;

    // Track active temp files for cleanup
    private readonly ConcurrentDictionary<string, TempFileInfo> _activeTempFiles = new();

    public string TempPrefix => _options.TempPrefix;
    public int ActiveTempFileCount => _activeTempFiles.Count;

    public TempFileHandler(
        ILogger<TempFileHandler> logger,
        string basePath,
        TempFileOptions? options = null)
    {
        _logger = logger;
        _basePath = basePath;
        _options = options ?? new TempFileOptions();
    }

    /// <summary>
    /// Create a temporary file path for a target file
    /// Format: .syncthing.{originalname}.tmp or .syncthing.{originalname}.{randomhex}.tmp
    /// </summary>
    public string GetTempFilePath(string targetPath)
    {
        var directory = Path.GetDirectoryName(targetPath) ?? string.Empty;
        var fileName = Path.GetFileName(targetPath);

        string tempFileName;
        if (_options.UseRandomSuffix)
        {
            var randomBytes = new byte[4];
            RandomNumberGenerator.Fill(randomBytes);
            var randomSuffix = Convert.ToHexString(randomBytes).ToLowerInvariant();
            tempFileName = $"{_options.TempPrefix}{fileName}.{randomSuffix}{_options.TempSuffix}";
        }
        else
        {
            tempFileName = $"{_options.TempPrefix}{fileName}{_options.TempSuffix}";
        }

        var tempPath = Path.Combine(directory, tempFileName);

        // Track the temp file
        _activeTempFiles[tempPath] = new TempFileInfo
        {
            TempPath = tempPath,
            TargetPath = targetPath,
            CreatedAt = DateTime.UtcNow
        };

        _logger.LogTrace("Created temp file path: {TempPath} for {TargetPath}", tempPath, targetPath);
        return tempPath;
    }

    /// <summary>
    /// Finalize a temporary file by moving it to the target location
    /// Uses atomic rename operation for data safety
    /// </summary>
    public async Task<bool> FinalizeTempFileAsync(string tempPath, string targetPath, CancellationToken ct = default)
    {
        try
        {
            var fullTempPath = Path.IsPathRooted(tempPath) ? tempPath : Path.Combine(_basePath, tempPath);
            var fullTargetPath = Path.IsPathRooted(targetPath) ? targetPath : Path.Combine(_basePath, targetPath);

            if (!File.Exists(fullTempPath))
            {
                _logger.LogWarning("Temp file does not exist: {TempPath}", fullTempPath);
                return false;
            }

            // Ensure target directory exists
            var targetDir = Path.GetDirectoryName(fullTargetPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // Remove existing target if present
            if (File.Exists(fullTargetPath))
            {
                if (_options.BackupOnOverwrite)
                {
                    var backupPath = fullTargetPath + ".bak";
                    File.Move(fullTargetPath, backupPath, overwrite: true);
                    _logger.LogTrace("Backed up existing file to: {BackupPath}", backupPath);
                }
                else
                {
                    File.Delete(fullTargetPath);
                }
            }

            // Atomic move
            await Task.Run(() => File.Move(fullTempPath, fullTargetPath), ct);

            // Remove from tracking
            _activeTempFiles.TryRemove(tempPath, out _);

            _logger.LogTrace("Finalized temp file: {TempPath} -> {TargetPath}", tempPath, targetPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to finalize temp file: {TempPath} -> {TargetPath}", tempPath, targetPath);
            return false;
        }
    }

    /// <summary>
    /// Clean up a temporary file
    /// </summary>
    public async Task CleanupTempFileAsync(string tempPath, CancellationToken ct = default)
    {
        try
        {
            var fullPath = Path.IsPathRooted(tempPath) ? tempPath : Path.Combine(_basePath, tempPath);

            if (File.Exists(fullPath))
            {
                await Task.Run(() => File.Delete(fullPath), ct);
                _logger.LogTrace("Cleaned up temp file: {TempPath}", tempPath);
            }

            _activeTempFiles.TryRemove(tempPath, out _);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup temp file: {TempPath}", tempPath);
        }
    }

    /// <summary>
    /// Clean up all orphaned temporary files in a directory
    /// Orphaned files are temp files older than maxAge that weren't finalized
    /// </summary>
    public async Task<int> CleanupOrphanedTempFilesAsync(string directory, TimeSpan maxAge, CancellationToken ct = default)
    {
        var cleaned = 0;

        try
        {
            var fullPath = Path.IsPathRooted(directory) ? directory : Path.Combine(_basePath, directory);

            if (!Directory.Exists(fullPath))
                return 0;

            var cutoffTime = DateTime.UtcNow - maxAge;

            await Task.Run(() =>
            {
                var tempFiles = Directory.EnumerateFiles(fullPath, $"{_options.TempPrefix}*{_options.TempSuffix}", SearchOption.AllDirectories);

                foreach (var tempFile in tempFiles)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var fileInfo = new FileInfo(tempFile);
                        if (fileInfo.LastWriteTimeUtc < cutoffTime)
                        {
                            fileInfo.Delete();
                            cleaned++;
                            _logger.LogDebug("Cleaned orphaned temp file: {TempFile}", tempFile);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to cleanup orphaned temp file: {TempFile}", tempFile);
                    }
                }
            }, ct);

            if (cleaned > 0)
            {
                _logger.LogInformation("Cleaned up {Count} orphaned temp files in {Directory}", cleaned, directory);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up orphaned temp files in: {Directory}", directory);
        }

        return cleaned;
    }

    /// <summary>
    /// Check if a path is a temporary file
    /// </summary>
    public bool IsTempFile(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.StartsWith(_options.TempPrefix, StringComparison.Ordinal) &&
               fileName.EndsWith(_options.TempSuffix, StringComparison.Ordinal);
    }
}

/// <summary>
/// Configuration options for temporary file handling
/// </summary>
public class TempFileOptions
{
    /// <summary>
    /// Prefix for temporary files (default: .syncthing.)
    /// </summary>
    public string TempPrefix { get; set; } = ".syncthing.";

    /// <summary>
    /// Suffix for temporary files (default: .tmp)
    /// </summary>
    public string TempSuffix { get; set; } = ".tmp";

    /// <summary>
    /// Use random suffix to avoid conflicts (default: true)
    /// </summary>
    public bool UseRandomSuffix { get; set; } = true;

    /// <summary>
    /// Create backup when overwriting existing files (default: false)
    /// </summary>
    public bool BackupOnOverwrite { get; set; }
}

/// <summary>
/// Information about an active temporary file
/// </summary>
internal class TempFileInfo
{
    public string TempPath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
