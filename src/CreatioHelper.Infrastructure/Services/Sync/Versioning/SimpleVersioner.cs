using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Versioning;

/// <summary>
/// Simple versioning implementation compatible with Syncthing's simple versioner
/// Keeps a specified number of versions, removing oldest when limit exceeded
/// Supports cleanout based on age (cleanoutDays parameter)
/// </summary>
public class SimpleVersioner : BaseVersioner
{
    private readonly int _keep;
    private readonly int _cleanoutDays;

    public SimpleVersioner(ILogger<SimpleVersioner> logger, string folderPath, VersioningConfiguration config) 
        : base(logger, folderPath, config)
    {
        if (!int.TryParse(config.Params.GetValueOrDefault("keep", "5"), out _keep))
            _keep = 5;

        if (!int.TryParse(config.Params.GetValueOrDefault("cleanoutDays", "0"), out _cleanoutDays))
            _cleanoutDays = 0;

        _logger.LogInformation("Simple versioner initialized: keep={Keep}, cleanoutDays={CleanoutDays}, versionsPath={VersionsPath}", 
            _keep, _cleanoutDays, VersionsPath);
    }

    public override string VersionerType => "simple";

    public override async Task CleanAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Starting simple versioner cleanup: keep={Keep}, cleanoutDays={CleanoutDays}", _keep, _cleanoutDays);
            
            var versions = await GetVersionsAsync(cancellationToken).ConfigureAwait(false);
            var filesToRemove = new List<string>();
            var cleanupCutoff = _cleanoutDays > 0 ? DateTime.UtcNow.AddDays(-_cleanoutDays) : DateTime.MinValue;

            foreach (var (originalPath, versionList) in versions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                // Apply cleanoutDays filter first
                var validVersions = _cleanoutDays > 0 
                    ? versionList.Where(v => v.VersionTime >= cleanupCutoff).ToList()
                    : versionList;

                // Add expired versions to removal list
                if (_cleanoutDays > 0)
                {
                    var expiredVersions = versionList.Where(v => v.VersionTime < cleanupCutoff);
                    filesToRemove.AddRange(expiredVersions.Select(v => v.VersionPath));
                }

                // Apply keep limit - remove excess versions (keep only newest N)
                if (validVersions.Count > _keep)
                {
                    // Sort by version time (newest first) and take excess
                    var excessVersions = validVersions
                        .OrderByDescending(v => v.VersionTime)
                        .Skip(_keep)
                        .Select(v => v.VersionPath);

                    filesToRemove.AddRange(excessVersions);
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
                        _logger.LogDebug("Removed old version: {FilePath}", filePath);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to remove version file {FilePath}: {Error}", filePath, ex.Message);
                }
            }

            // Clean up empty directories
            RemoveEmptyDirectories(VersionsPath);

            _logger.LogInformation("Simple versioner cleanup completed: removed {RemovedCount} version files", removedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Simple versioner cleanup failed: {Error}", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Gets statistics about current versioning state
    /// </summary>
    public async Task<VersioningStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var versions = await GetVersionsAsync(cancellationToken).ConfigureAwait(false);
        var totalVersions = versions.Values.Sum(v => v.Count);
        var totalSize = 0L;
        var oldestVersion = DateTime.MaxValue;
        var newestVersion = DateTime.MinValue;

        foreach (var versionList in versions.Values)
        {
            foreach (var version in versionList)
            {
                totalSize += version.Size;
                if (version.VersionTime < oldestVersion) oldestVersion = version.VersionTime;
                if (version.VersionTime > newestVersion) newestVersion = version.VersionTime;
            }
        }

        return new VersioningStats
        {
            TotalFiles = versions.Count,
            TotalVersions = totalVersions,
            TotalSize = totalSize,
            OldestVersion = oldestVersion == DateTime.MaxValue ? null : oldestVersion,
            NewestVersion = newestVersion == DateTime.MinValue ? null : newestVersion,
            VersionerType = VersionerType,
            KeepVersions = _keep,
            CleanoutDays = _cleanoutDays
        };
    }
}

/// <summary>
/// Statistics about versioning state for monitoring and debugging
/// </summary>
public class VersioningStats
{
    public int TotalFiles { get; set; }
    public int TotalVersions { get; set; }
    public long TotalSize { get; set; }
    public DateTime? OldestVersion { get; set; }
    public DateTime? NewestVersion { get; set; }
    public string VersionerType { get; set; } = string.Empty;
    public int KeepVersions { get; set; }
    public int CleanoutDays { get; set; }

    public override string ToString()
    {
        var sizeStr = TotalSize > 1024 * 1024 * 1024 
            ? $"{TotalSize / (1024.0 * 1024 * 1024):F1}GB"
            : TotalSize > 1024 * 1024 
                ? $"{TotalSize / (1024.0 * 1024):F1}MB"
                : $"{TotalSize / 1024.0:F1}KB";

        return $"{VersionerType}: {TotalVersions} versions for {TotalFiles} files ({sizeStr})";
    }
}