namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Block metadata entity for database storage - similar to Syncthing's block storage
/// </summary>
public class BlockMetadata
{
    /// <summary>
    /// SHA256 hash of the block (primary key)
    /// </summary>
    public byte[] Hash { get; set; } = Array.Empty<byte>();
    
    /// <summary>
    /// Size of the block in bytes
    /// </summary>
    public int Size { get; set; }
    
    /// <summary>
    /// Weak hash for rolling hash matching (similar to rsync)
    /// </summary>
    public uint WeakHash { get; set; }
    
    /// <summary>
    /// Reference count for deduplication
    /// </summary>
    public int ReferenceCount { get; set; }
    
    /// <summary>
    /// When this block was first stored
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Last time this block was accessed
    /// </summary>
    public DateTime LastAccessed { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Whether this block is stored locally
    /// </summary>
    public bool IsLocal { get; set; } = true;
    
    /// <summary>
    /// Compression type used for this block
    /// </summary>
    public string CompressionType { get; set; } = "none";
    
    /// <summary>
    /// Compressed size if compression is used
    /// </summary>
    public int CompressedSize { get; set; }
    
    /// <summary>
    /// Block data (for temporary storage during transfer)
    /// </summary>
    public byte[]? Data { get; set; }
    
    // Syncthing compatibility properties
    public int DeviceIdx { get; set; }
    public string FolderId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public int BlockIndex { get; set; }
}