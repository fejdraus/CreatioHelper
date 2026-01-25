namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// BEP Error Codes
/// </summary>
public enum BepErrorCode
{
    NoError = 0,
    Generic = 1,
    NoSuchFile = 2,
    InvalidFile = 3
}

/// <summary>
/// Event arguments for BEP connection specific events  
/// </summary>
/// <summary>
/// Event arguments for BEP Index message received.
/// Index message contains the complete file listing for a folder.
/// </summary>
public class BepIndexReceivedEventArgs : EventArgs
{
    /// <summary>
    /// The folder ID this index is for.
    /// </summary>
    public string FolderId { get; set; } = string.Empty;

    /// <summary>
    /// The files in this index.
    /// </summary>
    public IEnumerable<object> Files { get; set; } = new List<object>();

    /// <summary>
    /// The sequence number of the last file in this index.
    /// Used for tracking sync progress and resuming interrupted syncs.
    /// </summary>
    public long LastSequence { get; set; }
}

/// <summary>
/// Event arguments for BEP IndexUpdate message received.
/// IndexUpdate message contains incremental changes to a folder's file listing.
/// </summary>
public class BepIndexUpdateReceivedEventArgs : EventArgs
{
    /// <summary>
    /// The folder ID this index update is for.
    /// </summary>
    public string FolderId { get; set; } = string.Empty;

    /// <summary>
    /// The changed files in this index update.
    /// </summary>
    public IEnumerable<object> Files { get; set; } = new List<object>();

    /// <summary>
    /// The sequence number of the last file in this index update.
    /// Used for tracking sync progress and resuming interrupted syncs.
    /// </summary>
    public long LastSequence { get; set; }
}

public class BepBlockRequestReceivedEventArgs : EventArgs
{
    public int RequestId { get; set; }
    public string FolderId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Offset { get; set; }
    public int Size { get; set; }
    public byte[]? Hash { get; set; }
}

public class BepBlockResponseReceivedEventArgs : EventArgs
{
    public int RequestId { get; set; }
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public BepErrorCode ErrorCode { get; set; }
}

public class BepPingReceivedEventArgs : EventArgs
{
}

public class BepPongReceivedEventArgs : EventArgs
{
}

public class BepConnectionClosedEventArgs : EventArgs
{
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Event arguments for BEP DownloadProgress message received.
/// DownloadProgress message provides feedback about file transfer progress.
/// </summary>
public class BepDownloadProgressReceivedEventArgs : EventArgs
{
    /// <summary>
    /// The folder ID this download progress is for.
    /// </summary>
    public string FolderId { get; set; } = string.Empty;

    /// <summary>
    /// List of file download progress updates.
    /// </summary>
    public IEnumerable<BepFileDownloadProgressUpdateInfo> Updates { get; set; } = new List<BepFileDownloadProgressUpdateInfo>();
}

/// <summary>
/// Update type for download progress.
/// </summary>
public enum BepDownloadProgressUpdateType
{
    /// <summary>
    /// Append new block indexes to the existing set.
    /// </summary>
    Append = 0,

    /// <summary>
    /// Forget all previously reported progress for this file.
    /// </summary>
    Forget = 1
}

/// <summary>
/// Individual file download progress update information.
/// </summary>
public class BepFileDownloadProgressUpdateInfo
{
    /// <summary>
    /// The type of update (Append or Forget).
    /// </summary>
    public BepDownloadProgressUpdateType UpdateType { get; set; }

    /// <summary>
    /// The file name this update is for.
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// Block indexes that have been downloaded or are in progress.
    /// </summary>
    public IEnumerable<int> BlockIndexes { get; set; } = new List<int>();

    /// <summary>
    /// Block size used for the file transfer.
    /// </summary>
    public int BlockSize { get; set; }
}

/// <summary>
/// Event arguments for BEP ClusterConfig message received.
/// ClusterConfig MUST be the first message after TLS authentication (BEP spec requirement).
/// </summary>
public class BepClusterConfigReceivedEventArgs : EventArgs
{
    /// <summary>
    /// Folders shared by the remote device.
    /// </summary>
    public IEnumerable<BepClusterConfigFolder> Folders { get; set; } = new List<BepClusterConfigFolder>();
}

/// <summary>
/// Folder information from ClusterConfig message.
/// </summary>
public class BepClusterConfigFolder
{
    /// <summary>
    /// Folder ID (unique identifier).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Folder label (display name).
    /// </summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Whether the folder is read-only.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// Whether the folder is paused.
    /// </summary>
    public bool Paused { get; set; }

    /// <summary>
    /// Devices that share this folder.
    /// </summary>
    public IEnumerable<BepClusterConfigDevice> Devices { get; set; } = new List<BepClusterConfigDevice>();
}

/// <summary>
/// Device information from ClusterConfig message.
/// </summary>
public class BepClusterConfigDevice
{
    /// <summary>
    /// Device ID (raw bytes).
    /// </summary>
    public byte[] Id { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Device ID string representation.
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Device name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Whether this device is an introducer.
    /// </summary>
    public bool Introducer { get; set; }

    /// <summary>
    /// Maximum sequence number for this device.
    /// </summary>
    public long MaxSequence { get; set; }
}

/// <summary>
/// Interface for BEP connections with bandwidth management support
/// </summary>
public interface IBepConnection : IDisposable
{
    /// <summary>
    /// Device ID for this connection
    /// </summary>
    string DeviceId { get; }
    
    /// <summary>
    /// Whether the connection is active
    /// </summary>
    bool IsConnected { get; }
    
    /// <summary>
    /// Start the connection
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Stop the connection
    /// </summary>
    Task StopAsync();
    
    /// <summary>
    /// Send index information to the peer
    /// </summary>
    Task SendIndexAsync(string folderId, IEnumerable<object> files);
    
    /// <summary>
    /// Send a block request to the peer
    /// </summary>
    Task SendBlockRequestAsync(string folderId, string fileName, long offset, int size, byte[]? hash = null);
    
    /// <summary>
    /// Send a block response to the peer
    /// </summary>
    Task SendBlockResponseAsync(int requestId, byte[] data, BepErrorCode errorCode = BepErrorCode.NoError);
    
    /// <summary>
    /// Send ping message
    /// </summary>
    Task SendPingAsync();
    
    /// <summary>
    /// Send pong message
    /// </summary>
    Task SendPongAsync();
    
    // Events

    /// <summary>
    /// Raised when ClusterConfig message is received from the peer.
    /// ClusterConfig MUST be the first message after TLS authentication (BEP spec).
    /// </summary>
    event EventHandler<BepClusterConfigReceivedEventArgs>? ClusterConfigReceived;

    event EventHandler<BepIndexReceivedEventArgs>? IndexReceived;

    /// <summary>
    /// Raised when IndexUpdate message is received from the peer.
    /// IndexUpdate contains incremental changes to a folder's file listing.
    /// </summary>
    event EventHandler<BepIndexUpdateReceivedEventArgs>? IndexUpdateReceived;

    event EventHandler<BepBlockRequestReceivedEventArgs>? BlockRequestReceived;
    event EventHandler<BepBlockResponseReceivedEventArgs>? BlockResponseReceived;

    /// <summary>
    /// Raised when DownloadProgress message is received from the peer.
    /// DownloadProgress provides feedback about file transfer progress.
    /// </summary>
    event EventHandler<BepDownloadProgressReceivedEventArgs>? DownloadProgressReceived;

    event EventHandler<BepPingReceivedEventArgs>? PingReceived;
    event EventHandler<BepPongReceivedEventArgs>? PongReceived;
    event EventHandler<BepConnectionClosedEventArgs>? ConnectionClosed;
}