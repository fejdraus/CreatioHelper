using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Versioning;

/// <summary>
/// Trashcan versioning implementation compatible with Syncthing's trashcan versioner
/// Simple trash-can approach - moves files without timestamp tags
/// Supports cleanout based on age (cleanoutDays parameter)
/// </summary>
public class TrashcanVersioner : BaseVersioner
{
    private readonly int _cleanoutDays;

    public TrashcanVersioner(ILogger<TrashcanVersioner> logger, string folderPath, VersioningConfiguration config) 
        : base(logger, folderPath, config)
    {
        if (!int.TryParse(config.Params.GetValueOrDefault("cleanoutDays", "0"), out _cleanoutDays))
            _cleanoutDays = 0;

        _logger.LogInformation("Trashcan versioner initialized: cleanoutDays={CleanoutDays}, versionsPath={VersionsPath}", 
            _cleanoutDays, VersionsPath);
    }

    public override string VersionerType => "trashcan";

    public override Task ArchiveAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(_folderPath, filePath);
        
        if (!File.Exists(fullPath))
        {
            _logger.LogWarning("Cannot archive non-existent file: {FilePath}", filePath);
            return Task.CompletedTask;
        }

        try
        {
            var versionPath = GetTrashcanPath(filePath);
            
            // Create directory structure in versions folder
            var versionDir = Path.GetDirectoryName(versionPath);
            if (!string.IsNullOrEmpty(versionDir) && !Directory.Exists(versionDir))
            {
                Directory.CreateDirectory(versionDir);
                
                // Copy directory permissions from source
                var sourceDir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(sourceDir) && Directory.Exists(sourceDir))
                {
                    CopyDirectoryPermissions(sourceDir, versionDir);
                }
            }

            // If target exists, make it unique with timestamp
            if (File.Exists(versionPath))
            {
                versionPath = GetUniqueTrashcanPath(filePath);
            }

            // Move file to trashcan (preserves modification time automatically)
            File.Move(fullPath, versionPath);
            
            _logger.LogInformation("Moved file {FilePath} to trashcan: {TrashcanPath}", filePath, versionPath);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move file {FilePath} to trashcan: {Error}", filePath, ex.Message);
            throw;
        }
    }

    public override async Task CleanAsync(CancellationToken cancellationToken = default)
    {
        if (_cleanoutDays <= 0)
        {
            _logger.LogDebug("Trashcan cleanup disabled (cleanoutDays=0)");
            return;
        }

        try
        {
            _logger.LogDebug("Starting trashcan cleanup: cleanoutDays={CleanoutDays}", _cleanoutDays);
            
            var cutoffTime = DateTime.UtcNow.AddDays(-_cleanoutDays);
            var filesToRemove = new List<string>();

            // Find all files older than cutoff
            await foreach (var file in EnumerateFilesRecursiveAsync(VersionsPath, cancellationToken))
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTime < cutoffTime)
                {
                    filesToRemove.Add(file);
                }
            }

            // Remove identified files
            var removedCount = 0;
            foreach (var filePath in filesToRemove)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                        removedCount++;
                        _logger.LogDebug("Removed old trashcan file: {FilePath}", filePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove trashcan file {FilePath}: {Error}", filePath, ex.Message);
                }
            }

            // Clean up empty directories
            RemoveEmptyDirectories(VersionsPath);

            _logger.LogInformation("Trashcan cleanup completed: removed {RemovedCount} files", removedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Trashcan cleanup failed: {Error}", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Gets the trashcan path for a file (preserves original filename)
    /// </summary>
    private string GetTrashcanPath(string originalPath)
    {
        var directory = Path.GetDirectoryName(originalPath) ?? "";
        return Path.Combine(VersionsPath, directory, Path.GetFileName(originalPath));
    }

    /// <summary>
    /// Gets a unique trashcan path when target already exists
    /// Adds timestamp to make it unique
    /// </summary>
    private string GetUniqueTrashcanPath(string originalPath)
    {
        var directory = Path.GetDirectoryName(originalPath) ?? "";
        var fileName = Path.GetFileName(originalPath);
        var extension = Path.GetExtension(fileName);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        
        var timestamp = DateTime.UtcNow.ToString(TimeFormat);
        var uniqueFileName = $"{nameWithoutExt}.{timestamp}{extension}";
        
        return Path.Combine(VersionsPath, directory, uniqueFileName);
    }

    /// <summary>
    /// Override to handle non-versioned files in trashcan
    /// In trashcan mode, all files are considered "versions"
    /// </summary>
    protected override bool IsVersionFile(string filePath)
    {
        // In trashcan mode, all files are versions
        return true;
    }

    /// <summary>
    /// Override version parsing for trashcan (no timestamps in names)
    /// </summary>
    protected override (string? originalPath, DateTime? versionTime) ParseVersionFileName(string versionFilePath)
    {
        var relativePath = Path.GetRelativePath(VersionsPath, versionFilePath);
        var fileInfo = new FileInfo(versionFilePath);
        
        // Use file modification time as version time
        return (relativePath, fileInfo.LastWriteTime);
    }

    /// <summary>
    /// Gets statistics about current trashcan state
    /// </summary>
    public async Task<TrashcanStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var totalFiles = 0;
        var totalSize = 0L;
        var oldestFile = DateTime.MaxValue;
        var newestFile = DateTime.MinValue;

        await foreach (var file in EnumerateFilesRecursiveAsync(VersionsPath, cancellationToken))
        {
            var fileInfo = new FileInfo(file);
            totalFiles++;
            totalSize += fileInfo.Length;
            
            if (fileInfo.LastWriteTime < oldestFile) oldestFile = fileInfo.LastWriteTime;
            if (fileInfo.LastWriteTime > newestFile) newestFile = fileInfo.LastWriteTime;
        }

        return new TrashcanStats
        {
            TotalFiles = totalFiles,
            TotalSize = totalSize,
            OldestFile = oldestFile == DateTime.MaxValue ? null : oldestFile,
            NewestFile = newestFile == DateTime.MinValue ? null : newestFile,
            CleanoutDays = _cleanoutDays
        };
    }
}

/// <summary>
/// Statistics for trashcan versioning
/// </summary>
public class TrashcanStats
{
    public int TotalFiles { get; set; }
    public long TotalSize { get; set; }
    public DateTime? OldestFile { get; set; }
    public DateTime? NewestFile { get; set; }
    public int CleanoutDays { get; set; }

    public override string ToString()
    {
        var sizeStr = TotalSize > 1024 * 1024 * 1024 
            ? $"{TotalSize / (1024.0 * 1024 * 1024):F1}GB"
            : TotalSize > 1024 * 1024 
                ? $"{TotalSize / (1024.0 * 1024):F1}MB"
                : $"{TotalSize / 1024.0:F1}KB";

        return $"trashcan: {TotalFiles} files ({sizeStr})";
    }
}