using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Versioning;

/// <summary>
/// Staggered versioning implementation compatible with Syncthing's staggered versioner
/// Uses time-based intervals with decreasing frequency:
/// - First hour: 30-second intervals
/// - Next day: 1-hour intervals  
/// - Next 30 days: 1-day intervals
/// - Until maxAge: 1-week intervals
/// </summary>
public class StaggeredVersioner : BaseVersioner
{
    private readonly int _maxAgeSeconds;
    
    // Interval definitions matching Syncthing's staggered logic
    private static readonly List<IntervalRule> Intervals = new()
    {
        new(TimeSpan.FromSeconds(30), TimeSpan.FromHours(1)),    // 30s intervals for first hour
        new(TimeSpan.FromHours(1), TimeSpan.FromDays(1)),       // 1h intervals for first day  
        new(TimeSpan.FromDays(1), TimeSpan.FromDays(30)),       // 1d intervals for first 30 days
        new(TimeSpan.FromDays(7), TimeSpan.MaxValue)            // 1w intervals until maxAge
    };

    public StaggeredVersioner(ILogger<StaggeredVersioner> logger, string folderPath, VersioningConfiguration config) 
        : base(logger, folderPath, config)
    {
        if (!int.TryParse(config.Params.GetValueOrDefault("maxAge", "31536000"), out _maxAgeSeconds))
            _maxAgeSeconds = 31536000; // Default: ~1 year

        _logger.LogInformation("Staggered versioner initialized: maxAge={MaxAgeSeconds}s ({MaxAgeDays:F1} days), versionsPath={VersionsPath}", 
            _maxAgeSeconds, _maxAgeSeconds / 86400.0, VersionsPath);
    }

    public override string VersionerType => "staggered";

    public override async Task CleanAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogDebug("Starting staggered versioner cleanup: maxAge={MaxAgeSeconds}s", _maxAgeSeconds);
            
            var versions = await GetVersionsAsync(cancellationToken).ConfigureAwait(false);
            var filesToRemove = new List<string>();
            var cutoffTime = DateTime.UtcNow.AddSeconds(-_maxAgeSeconds);

            foreach (var (originalPath, versionList) in versions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                // Remove versions older than maxAge
                var expiredVersions = versionList.Where(v => v.VersionTime < cutoffTime).ToList();
                filesToRemove.AddRange(expiredVersions.Select(v => v.VersionPath));

                // Apply staggered interval logic to remaining versions
                var validVersions = versionList.Where(v => v.VersionTime >= cutoffTime)
                    .OrderByDescending(v => v.VersionTime)
                    .ToList();

                var toRemove = GetVersionsToRemove(validVersions);
                filesToRemove.AddRange(toRemove.Select(v => v.VersionPath));
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

            _logger.LogInformation("Staggered versioner cleanup completed: removed {RemovedCount} version files", removedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Staggered versioner cleanup failed: {Error}", ex.Message);
            throw;
        }
    }

    /// <summary>
    /// Applies staggered interval logic to determine which versions to remove
    /// Keeps oldest version in each time interval, removes others
    /// </summary>
    private List<FileVersion> GetVersionsToRemove(List<FileVersion> versions)
    {
        if (versions.Count <= 1)
            return new List<FileVersion>();

        var toRemove = new List<FileVersion>();
        var now = DateTime.UtcNow;
        
        // Group versions by interval rules
        foreach (var interval in Intervals)
        {
            var intervalStart = now.Subtract(interval.MaxAge);
            var intervalEnd = interval.MaxAge == TimeSpan.MaxValue 
                ? DateTime.MinValue 
                : now.Subtract(interval.MinAge);
            
            // Find versions in this interval
            var intervalVersions = versions
                .Where(v => v.VersionTime >= intervalStart && 
                           (interval.MaxAge == TimeSpan.MaxValue || v.VersionTime < intervalEnd))
                .OrderBy(v => v.VersionTime)
                .ToList();

            if (intervalVersions.Count <= 1)
                continue;

            // Keep versions at the specified interval, remove others
            var intervalSeconds = interval.Interval.TotalSeconds;
            var keeper = intervalVersions.First();
            
            for (int i = 1; i < intervalVersions.Count; i++)
            {
                var version = intervalVersions[i];
                var timeSinceKeeper = (version.VersionTime - keeper.VersionTime).TotalSeconds;
                
                if (timeSinceKeeper < intervalSeconds)
                {
                    // Too close to keeper - remove this version
                    toRemove.Add(version);
                }
                else
                {
                    // Far enough - keep this version and make it the new keeper
                    keeper = version;
                }
            }
        }

        return toRemove;
    }

    /// <summary>
    /// Gets statistics about current staggered versioning state
    /// </summary>
    public async Task<StaggeredVersioningStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var versions = await GetVersionsAsync(cancellationToken);
        var totalVersions = versions.Values.Sum(v => v.Count);
        var totalSize = 0L;
        var intervalCounts = new Dictionary<string, int>();

        var now = DateTime.UtcNow;
        var cutoffTime = now.AddSeconds(-_maxAgeSeconds);

        foreach (var versionList in versions.Values)
        {
            foreach (var version in versionList)
            {
                totalSize += version.Size;
                
                if (version.VersionTime >= cutoffTime)
                {
                    var intervalName = GetIntervalName(now - version.VersionTime);
                    intervalCounts[intervalName] = intervalCounts.GetValueOrDefault(intervalName, 0) + 1;
                }
            }
        }

        return new StaggeredVersioningStats
        {
            TotalFiles = versions.Count,
            TotalVersions = totalVersions,
            TotalSize = totalSize,
            MaxAgeSeconds = _maxAgeSeconds,
            IntervalCounts = intervalCounts
        };
    }

    /// <summary>
    /// Gets a human-readable interval name for statistics
    /// </summary>
    private string GetIntervalName(TimeSpan age)
    {
        if (age <= TimeSpan.FromHours(1)) return "Last Hour";
        if (age <= TimeSpan.FromDays(1)) return "Last Day";
        if (age <= TimeSpan.FromDays(30)) return "Last Month";
        return "Older";
    }

    /// <summary>
    /// Represents an interval rule for staggered versioning
    /// </summary>
    private record IntervalRule(TimeSpan Interval, TimeSpan MinAge, TimeSpan MaxAge)
    {
        public IntervalRule(TimeSpan interval, TimeSpan maxAge) : this(interval, TimeSpan.Zero, maxAge) { }
    }
}

/// <summary>
/// Statistics specific to staggered versioning
/// </summary>
public class StaggeredVersioningStats
{
    public int TotalFiles { get; set; }
    public int TotalVersions { get; set; }
    public long TotalSize { get; set; }
    public int MaxAgeSeconds { get; set; }
    public Dictionary<string, int> IntervalCounts { get; set; } = new();

    public override string ToString()
    {
        var sizeStr = TotalSize > 1024 * 1024 * 1024 
            ? $"{TotalSize / (1024.0 * 1024 * 1024):F1}GB"
            : TotalSize > 1024 * 1024 
                ? $"{TotalSize / (1024.0 * 1024):F1}MB"
                : $"{TotalSize / 1024.0:F1}KB";

        var intervalInfo = string.Join(", ", IntervalCounts.Select(kvp => $"{kvp.Key}: {kvp.Value}"));
        
        return $"staggered: {TotalVersions} versions for {TotalFiles} files ({sizeStr}) - {intervalInfo}";
    }
}