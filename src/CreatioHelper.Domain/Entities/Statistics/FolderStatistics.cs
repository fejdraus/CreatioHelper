using System.Text.Json.Serialization;

namespace CreatioHelper.Domain.Entities.Statistics;

/// <summary>
/// Last file information compatible with Syncthing
/// Exact match to syncthing/lib/stats/folder.go LastFile
/// </summary>
public class LastFile
{
    [JsonPropertyName("at")]
    public DateTime At { get; set; }
    
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;
    
    [JsonPropertyName("deleted")]
    public bool Deleted { get; set; }
}

/// <summary>
/// Syncthing-compatible folder statistics structure
/// Exact match to syncthing/lib/stats/folder.go FolderStatistics
/// </summary>
public class SyncthingFolderStatistics
{
    [JsonPropertyName("lastFile")]
    public LastFile LastFile { get; set; } = new();
    
    [JsonPropertyName("lastScan")]
    public DateTime LastScan { get; set; }
}

/// <summary>
/// Extended folder statistics (based on Syncthing FolderStatistics)
/// Tracks synchronization statistics for sync folders
/// </summary>
public class FolderStatistics
{
    /// <summary>
    /// Folder ID
    /// </summary>
    public string FolderId { get; set; } = string.Empty;

    /// <summary>
    /// Folder label (human-readable name)
    /// </summary>
    public string FolderLabel { get; set; } = string.Empty;

    /// <summary>
    /// Folder path
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Information about the last file that was synchronized
    /// </summary>
    public LastFile LastFile { get; set; } = new();

    /// <summary>
    /// Last time folder scan was completed
    /// </summary>
    public DateTime LastScan { get; set; }

    /// <summary>
    /// Next scheduled scan time
    /// </summary>
    public DateTime? NextScan { get; set; }

    /// <summary>
    /// Duration of the last scan
    /// </summary>
    public TimeSpan LastScanDuration { get; set; }

    /// <summary>
    /// Current folder status
    /// </summary>
    public FolderStatus Status { get; set; } = FolderStatus.Idle;

    /// <summary>
    /// Total number of files in folder
    /// </summary>
    public long TotalFiles { get; set; }

    /// <summary>
    /// Total size of folder in bytes
    /// </summary>
    public long TotalSize { get; set; }

    /// <summary>
    /// Number of files that need to be synchronized
    /// </summary>
    public long FilesToSync { get; set; }

    /// <summary>
    /// Size of files that need to be synchronized
    /// </summary>
    public long BytesToSync { get; set; }

    /// <summary>
    /// Number of files currently being synchronized
    /// </summary>
    public long FilesInProgress { get; set; }

    /// <summary>
    /// Number of files with conflicts
    /// </summary>
    public long ConflictedFiles { get; set; }

    /// <summary>
    /// Number of files with errors
    /// </summary>
    public long ErroredFiles { get; set; }

    /// <summary>
    /// Current sync progress percentage (0-100)
    /// </summary>
    public double SyncProgress { get; set; }

    /// <summary>
    /// Files synchronized in the last session
    /// </summary>
    public long FilesLastSession { get; set; }

    /// <summary>
    /// Bytes synchronized in the last session
    /// </summary>
    public long BytesLastSession { get; set; }

    /// <summary>
    /// Total files synchronized since startup
    /// </summary>
    public long TotalFilesSynced { get; set; }

    /// <summary>
    /// Total bytes synchronized since startup
    /// </summary>
    public long TotalBytesSynced { get; set; }

    /// <summary>
    /// Number of devices sharing this folder
    /// </summary>
    public int SharedWithDevices { get; set; }

    /// <summary>
    /// Watch enabled status
    /// </summary>
    public bool WatchEnabled { get; set; }

    /// <summary>
    /// Folder type (send-receive, send-only, receive-only)
    /// </summary>
    public string FolderType { get; set; } = "sendreceive";

    /// <summary>
    /// Ignore patterns enabled
    /// </summary>
    public bool IgnorePatternsEnabled { get; set; }

    /// <summary>
    /// Number of ignored files
    /// </summary>
    public long IgnoredFiles { get; set; }

    /// <summary>
    /// Versioning enabled
    /// </summary>
    public bool VersioningEnabled { get; set; }

    /// <summary>
    /// Last error message
    /// </summary>
    public string? LastError { get; set; }

    /// <summary>
    /// Last error time
    /// </summary>
    public DateTime? LastErrorTime { get; set; }

    /// <summary>
    /// Compression enabled for this folder
    /// </summary>
    public bool CompressionEnabled { get; set; }

    /// <summary>
    /// Paused status
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>
    /// Update scan statistics
    /// </summary>
    public void OnScanCompleted(DateTime scanTime, TimeSpan duration, long totalFiles, long totalSize)
    {
        LastScan = scanTime;
        LastScanDuration = duration;
        TotalFiles = totalFiles;
        TotalSize = totalSize;
        
        // Calculate next scan time if periodic scanning is enabled
        // This would be based on folder configuration
    }

    /// <summary>
    /// Record file synchronization
    /// </summary>
    public void OnFileSynced(string fileName, bool wasDeleted, long fileSize, DateTime syncTime)
    {
        LastFile = new LastFile
        {
            At = syncTime.Truncate(TimeSpan.FromSeconds(1)),
            Filename = fileName,
            Deleted = wasDeleted
        };

        FilesLastSession++;
        TotalFilesSynced++;

        if (!wasDeleted)
        {
            BytesLastSession += fileSize;
            TotalBytesSynced += fileSize;
        }
    }
    
    /// <summary>
    /// Convert to Syncthing-compatible format
    /// </summary>
    public SyncthingFolderStatistics ToSyncthingFormat()
    {
        return new SyncthingFolderStatistics
        {
            LastFile = LastFile,
            LastScan = LastScan
        };
    }

    /// <summary>
    /// Update sync progress
    /// </summary>
    public void UpdateSyncProgress(long filesToSync, long bytesToSync, long filesInProgress)
    {
        FilesToSync = filesToSync;
        BytesToSync = bytesToSync;
        FilesInProgress = filesInProgress;

        // Calculate progress percentage
        if (TotalFiles > 0)
        {
            var completedFiles = TotalFiles - filesToSync;
            SyncProgress = Math.Round((double)completedFiles / TotalFiles * 100, 2);
        }
    }

    /// <summary>
    /// Record error for this folder
    /// </summary>
    public void OnError(string error, DateTime errorTime)
    {
        LastError = error;
        LastErrorTime = errorTime;
        Status = FolderStatus.Error;
    }

    /// <summary>
    /// Get completion percentage
    /// </summary>
    public double GetCompletionPercentage()
    {
        if (TotalFiles == 0) return 100.0;
        
        var completedFiles = TotalFiles - FilesToSync;
        return Math.Round((double)completedFiles / TotalFiles * 100, 2);
    }

    /// <summary>
    /// Get formatted folder size
    /// </summary>
    public string GetFormattedSize()
    {
        return FormatBytes(TotalSize);
    }

    /// <summary>
    /// Get formatted bytes to sync
    /// </summary>
    public string GetFormattedBytesToSync()
    {
        return FormatBytes(BytesToSync);
    }

    /// <summary>
    /// Get average file size
    /// </summary>
    public double GetAverageFileSize()
    {
        return TotalFiles > 0 ? (double)TotalSize / TotalFiles : 0;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_000_000_000_000)
            return $"{bytes / 1_000_000_000_000.0:F1} TB";
        if (bytes >= 1_000_000_000)
            return $"{bytes / 1_000_000_000.0:F1} GB";
        if (bytes >= 1_000_000)
            return $"{bytes / 1_000_000.0:F1} MB";
        if (bytes >= 1_000)
            return $"{bytes / 1_000.0:F1} KB";
        return $"{bytes} B";
    }
}

/// <summary>
/// Information about the last file synchronized
/// </summary>
public class LastFileInfo
{
    /// <summary>
    /// Time when file was synchronized
    /// </summary>
    public DateTime At { get; set; }

    /// <summary>
    /// Name of the file
    /// </summary>
    public string Filename { get; set; } = string.Empty;

    /// <summary>
    /// Whether the file was deleted
    /// </summary>
    public bool Deleted { get; set; }

    /// <summary>
    /// Size of the file in bytes
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Device that synchronized the file
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Additional metadata about the sync operation
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();

    public override string ToString()
    {
        var action = Deleted ? "Deleted" : "Synced";
        return $"{action}: {Filename} ({FormatBytes(Size)}) at {At:yyyy-MM-dd HH:mm:ss}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1_000_000_000)
            return $"{bytes / 1_000_000_000.0:F1} GB";
        if (bytes >= 1_000_000)
            return $"{bytes / 1_000_000.0:F1} MB";
        if (bytes >= 1_000)
            return $"{bytes / 1_000.0:F1} KB";
        return $"{bytes} B";
    }
}

/// <summary>
/// Folder synchronization status
/// </summary>
public enum FolderStatus
{
    Idle,
    Scanning,
    Syncing,
    Error,
    Paused,
    Cleaning
}

public static class DateTimeExtensions
{
    public static DateTime Truncate(this DateTime dateTime, TimeSpan timeSpan)
    {
        if (timeSpan == TimeSpan.Zero) return dateTime;
        return dateTime.AddTicks(-(dateTime.Ticks % timeSpan.Ticks));
    }
}