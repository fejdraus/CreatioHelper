namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Represents a versioned file compatible with Syncthing's FileVersion structure
/// Contains timestamp and metadata information for file versions
/// </summary>
public class FileVersion
{
    /// <summary>
    /// Time when this version was created (when file was archived)
    /// Used for version identification and restoration
    /// </summary>
    public DateTime VersionTime { get; set; }

    /// <summary>
    /// Original modification time of the file when it was archived
    /// Preserved from the original file's metadata
    /// </summary>
    public DateTime ModTime { get; set; }

    /// <summary>
    /// Size of the versioned file in bytes
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Full path to the versioned file in the versions directory
    /// </summary>
    public string VersionPath { get; set; } = string.Empty;

    /// <summary>
    /// Original file path relative to the sync folder
    /// </summary>
    public string OriginalPath { get; set; } = string.Empty;

    public override string ToString()
    {
        return $"FileVersion: {OriginalPath} @ {VersionTime:yyyy-MM-dd HH:mm:ss} ({Size} bytes)";
    }
}