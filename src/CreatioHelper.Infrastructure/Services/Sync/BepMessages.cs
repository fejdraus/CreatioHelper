namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// BEP Message types according to Syncthing protocol
/// </summary>
public enum BepMessageType
{
    ClusterConfig = 0,
    Index = 1,
    IndexUpdate = 2,
    Request = 3,
    Response = 4,
    DownloadProgress = 5,
    Ping = 6,
    Close = 7,
    Hello = 8 // Extension for initial handshake
}

/// <summary>
/// BEP Compression types
/// </summary>
public enum BepCompression
{
    None = 0,
    Always = 1,
    Metadata = 2
}

/// <summary>
/// BEP Error codes
/// </summary>
public enum BepErrorCode
{
    NoError = 0,
    Generic = 1,
    NoSuchFile = 2,
    InvalidFile = 3
}

/// <summary>
/// BEP File info types
/// </summary>
public enum BepFileInfoType
{
    File = 0,
    Directory = 1,
    Symlink = 2
}

/// <summary>
/// BEP Update types for download progress
/// </summary>
public enum BepUpdateType
{
    Append = 0,
    Forget = 1
}

/// <summary>
/// BEP Protocol Header
/// </summary>
public class BepHeader
{
    public BepMessageType Type { get; set; }
    public BepCompression Compression { get; set; }
}

/// <summary>
/// Hello message for initial handshake
/// </summary>
public class BepHello
{
    // Standard Syncthing Hello fields
    public string DeviceName { get; set; } = string.Empty;     // device_name (field 1)
    public string ClientName { get; set; } = string.Empty;     // client_name (field 2)
    public string ClientVersion { get; set; } = string.Empty;  // client_version (field 3)
    public int NumConnections { get; set; } = 1;               // num_connections (field 4)
    public long Timestamp { get; set; }                        // timestamp (field 5)

    // Extension for device identification when TLS is disabled (non-standard)
    public string DeviceId { get; set; } = string.Empty;
}

/// <summary>
/// ClusterConfig message - device and folder configuration
/// </summary>
public class BepClusterConfig
{
    public List<BepFolder> Folders { get; set; } = new();
}

/// <summary>
/// Folder configuration
/// </summary>
public class BepFolder
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool ReadOnly { get; set; }
    public bool IgnorePermissions { get; set; }
    public bool IgnoreDelete { get; set; }
    public bool DisableTempIndexes { get; set; }
    public bool Paused { get; set; }
    public List<BepDevice> Devices { get; set; } = new();
}

/// <summary>
/// Device configuration
/// </summary>
public class BepDevice
{
    public byte[] Id { get; set; } = Array.Empty<byte>();
    public string DeviceId { get; set; } = string.Empty;  // String representation for convenience
    public string Name { get; set; } = string.Empty;
    public List<string> Addresses { get; set; } = new();
    public BepCompression Compression { get; set; }
    public string CertName { get; set; } = string.Empty;
    public long MaxSequence { get; set; }
    public bool Introducer { get; set; }
    public ulong IndexId { get; set; }
    public bool SkipIntroductionRemovals { get; set; }
    public byte[] EncryptionPasswordToken { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Index message - complete file listing
/// </summary>
public class BepIndex
{
    public string Folder { get; set; } = string.Empty;
    public List<BepFileInfo> Files { get; set; } = new();
    public long LastSequence { get; set; }
}

/// <summary>
/// IndexUpdate message - incremental file changes
/// </summary>
public class BepIndexUpdate
{
    public string Folder { get; set; } = string.Empty;
    public List<BepFileInfo> Files { get; set; } = new();
    public long LastSequence { get; set; }
}

/// <summary>
/// File information according to BEP protocol
/// </summary>
public class BepFileInfo
{
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public long ModifiedS { get; set; } // Modified time in seconds since Unix epoch
    public int ModifiedNs { get; set; } // Nanoseconds part of modified time
    public ulong ModifiedBy { get; set; } // Short device ID
    public BepVector Version { get; set; } = new();
    public long Sequence { get; set; }
    public int BlockSize { get; set; } // Block size for this file
    public List<BepBlockInfo> Blocks { get; set; } = new();
    public string Symlink { get; set; } = string.Empty;
    public byte[] BlocksHash { get; set; } = Array.Empty<byte>();
    public bool Encrypted { get; set; }
    public BepFileInfoType Type { get; set; }
    public uint Permissions { get; set; }
    public bool Deleted { get; set; }
    public bool Invalid { get; set; }
    public bool NoPermissions { get; set; }
    public List<BepXattr> Xattrs { get; set; } = new();
    public BepPlatform Platform { get; set; } = new();
    public string LocalFlags { get; set; } = "";
    public byte[] VersionHash { get; set; } = Array.Empty<byte>();
    public bool InodeChangeNs { get; set; }
    public byte[] EncryptionTrailerHash { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Block information
/// </summary>
public class BepBlockInfo
{
    public long Offset { get; set; }
    public int Size { get; set; }
    public byte[] Hash { get; set; } = Array.Empty<byte>();
    public uint WeakHash { get; set; }
}

/// <summary>
/// Vector clock for conflict resolution
/// </summary>
public class BepVector
{
    public List<BepCounter> Counters { get; set; } = new();
}

/// <summary>
/// Individual counter in vector clock
/// </summary>
public class BepCounter
{
    public ulong Id { get; set; } // Short device ID
    public ulong Value { get; set; } // Logical timestamp
}

/// <summary>
/// Extended attributes
/// </summary>
public class BepXattr
{
    public string Name { get; set; } = string.Empty;
    public byte[] Value { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Platform-specific metadata
/// </summary>
public class BepPlatform
{
    public BepUnixData Unix { get; set; } = new();
    public BepWindowsData Windows { get; set; } = new();
    public BepLinuxData Linux { get; set; } = new();
    public BepDarwinData Darwin { get; set; } = new();
    public BepFreebsdData Freebsd { get; set; } = new();
    public BepNetbsdData Netbsd { get; set; } = new();
}

/// <summary>
/// Unix platform data
/// </summary>
public class BepUnixData
{
    public string OwnerName { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public int Uid { get; set; }
    public int Gid { get; set; }
}

/// <summary>
/// Windows platform data
/// </summary>
public class BepWindowsData
{
    public string OwnerName { get; set; } = string.Empty;
    public byte[] OwnerSid { get; set; } = Array.Empty<byte>();
    public bool OwnerIsGroup { get; set; }
}

/// <summary>
/// Linux platform data
/// </summary>
public class BepLinuxData
{
    public string OwnerName { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public int Uid { get; set; }
    public int Gid { get; set; }
    public List<BepXattr> Xattrs { get; set; } = new();
}

/// <summary>
/// Darwin (macOS) platform data
/// </summary>
public class BepDarwinData
{
    public string OwnerName { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public int Uid { get; set; }
    public int Gid { get; set; }
    public List<BepXattr> Xattrs { get; set; } = new();
}

/// <summary>
/// FreeBSD platform data
/// </summary>
public class BepFreebsdData
{
    public string OwnerName { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public int Uid { get; set; }
    public int Gid { get; set; }
    public List<BepXattr> Xattrs { get; set; } = new();
}

/// <summary>
/// NetBSD platform data
/// </summary>
public class BepNetbsdData
{
    public string OwnerName { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public int Uid { get; set; }
    public int Gid { get; set; }
    public List<BepXattr> Xattrs { get; set; } = new();
}

/// <summary>
/// Request message for file blocks
/// </summary>
public class BepRequest
{
    public int Id { get; set; }
    public string Folder { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Offset { get; set; }
    public int Size { get; set; }
    public byte[] Hash { get; set; } = Array.Empty<byte>();
    public bool FromTemporary { get; set; }
    public uint WeakHash { get; set; }
    public int BlockNo { get; set; }
}

/// <summary>
/// Response message with file data
/// </summary>
public class BepResponse
{
    public int Id { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public BepErrorCode Code { get; set; }
}

/// <summary>
/// Download progress message
/// </summary>
public class BepDownloadProgress
{
    public string Folder { get; set; } = string.Empty;
    public List<BepFileDownloadProgressUpdate> Updates { get; set; } = new();
}

/// <summary>
/// Download progress update
/// </summary>
public class BepFileDownloadProgressUpdate
{
    public BepUpdateType UpdateType { get; set; }
    public string Name { get; set; } = string.Empty;
    public BepVector Version { get; set; } = new();
    public List<int> BlockIndexes { get; set; } = new();
    public int BlockSize { get; set; }
}

/// <summary>
/// Ping message for keep-alive
/// </summary>
public class BepPing
{
}

/// <summary>
/// Close message for connection termination
/// </summary>
public class BepClose
{
    public string Reason { get; set; } = string.Empty;
}
