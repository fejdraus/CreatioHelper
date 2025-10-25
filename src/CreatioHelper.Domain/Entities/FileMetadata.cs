namespace CreatioHelper.Domain.Entities;

/// <summary>
/// File metadata entity for database storage - compatible with Syncthing's files table
/// Maps directly to the database schema for optimal performance
/// </summary>
public class FileMetadata
{
    // Core Syncthing-compatible fields
    public int DeviceIdx { get; set; }
    public string FolderId { get; set; } = string.Empty;
    public long Sequence { get; set; }
    public long? RemoteSequence { get; set; }
    public string FileName { get; set; } = string.Empty;
    public FileType FileType { get; set; } = FileType.File;
    public DateTime ModifiedTime { get; set; }
    public long Size { get; set; }
    public string VersionVector { get; set; } = string.Empty;
    public bool IsDeleted { get; set; }
    public bool IsInvalid { get; set; }
    public FileLocalFlags LocalFlags { get; set; } = FileLocalFlags.None;
    public int? Permissions { get; set; }
    public int? BlockSize { get; set; }
    public byte[]? BlocklistHash { get; set; }
    public string? SymlinkTarget { get; set; }
    public Dictionary<string, object> PlatformData { get; set; } = new();
    public byte[]? Hash { get; set; }
    public List<string> BlockHashes { get; set; } = new();
    public string ModifiedBy { get; set; } = string.Empty;
    
    // Additional fields for CreatioHelper
    public string DeviceId { get; set; } = string.Empty;
    public string? DeviceName { get; set; }
    public string? Version { get; set; }
    public bool LocallyChanged { get; set; }
    public byte[]? ProtobufData { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Legacy compatibility properties
    public bool IsNoPermissions => ((int)LocalFlags & 1) != 0;
    public bool IsSymlink => FileType == FileType.Symlink || FileType == FileType.SymlinkFile || FileType == FileType.SymlinkDirectory;
}