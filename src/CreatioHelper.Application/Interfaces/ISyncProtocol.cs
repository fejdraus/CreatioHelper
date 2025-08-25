using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.Interfaces;

/// <summary>
/// Block Exchange Protocol interface (based on Syncthing BEP)
/// </summary>
public interface ISyncProtocol : IDisposable
{
    Task StartListeningAsync();
    Task<bool> ConnectAsync(SyncDevice device, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string deviceId);
    Task SendHelloAsync(string deviceId, string deviceName, string clientName, string clientVersion);
    Task SendClusterConfigAsync(string deviceId, List<SyncFolder> folders);
    Task SendIndexAsync(string deviceId, string folderId, List<SyncFileInfo> files);
    Task SendIndexUpdateAsync(string deviceId, string folderId, List<SyncFileInfo> changedFiles);
    Task<List<SyncFileInfo>> RequestIndexAsync(string deviceId, string folderId);
    Task<byte[]> RequestBlockAsync(string deviceId, string folderId, string fileName, long offset, int size, string hash);
    Task SendBlockAsync(string deviceId, string folderId, string fileName, long offset, byte[] data);
    Task SendDownloadProgressAsync(string deviceId, string folderId, List<FileDownloadProgress> progress);
    Task SendPingAsync(string deviceId);
    Task SendCloseAsync(string deviceId, string reason);
    Task<bool> IsConnectedAsync(string deviceId);
    Task SendBlockResponseAsync(string deviceId, object response); // Using object to avoid circular dependency
    Task RegisterConnectionAsync(object connection); // Using object to avoid circular dependency
    
    // Events for protocol messages
    event EventHandler<DeviceConnectedEventArgs> DeviceConnected;
    event EventHandler<DeviceDisconnectedEventArgs> DeviceDisconnected;
    event EventHandler<IndexReceivedEventArgs> IndexReceived;
    event EventHandler<IndexUpdateReceivedEventArgs> IndexUpdateReceived;
    event EventHandler<BlockRequestedEventArgs> BlockRequested;
    event EventHandler<DownloadProgressEventArgs> DownloadProgressReceived;
    event EventHandler<BlockRequestReceivedEventArgs> BlockRequestReceived;
}

public class DeviceConnectedEventArgs : EventArgs
{
    public SyncDevice Device { get; }
    public DeviceConnectedEventArgs(SyncDevice device) => Device = device;
}

public class DeviceDisconnectedEventArgs : EventArgs
{
    public string DeviceId { get; }
    public DeviceDisconnectedEventArgs(string deviceId) => DeviceId = deviceId;
}

public class IndexReceivedEventArgs : EventArgs
{
    public string DeviceId { get; }
    public string FolderId { get; }
    public List<SyncFileInfo> Files { get; }
    
    public IndexReceivedEventArgs(string deviceId, string folderId, List<SyncFileInfo> files)
    {
        DeviceId = deviceId;
        FolderId = folderId;
        Files = files;
    }
}

public class BlockReceivedEventArgs : EventArgs
{
    public string DeviceId { get; }
    public string FolderId { get; }
    public string FileName { get; }
    public long Offset { get; }
    public byte[] Data { get; }
    
    public BlockReceivedEventArgs(string deviceId, string folderId, string fileName, long offset, byte[] data)
    {
        DeviceId = deviceId;
        FolderId = folderId;
        FileName = fileName;
        Offset = offset;
        Data = data;
    }
}

public class IndexUpdateReceivedEventArgs : EventArgs
{
    public string DeviceId { get; }
    public string FolderId { get; }
    public List<SyncFileInfo> ChangedFiles { get; }
    
    public IndexUpdateReceivedEventArgs(string deviceId, string folderId, List<SyncFileInfo> changedFiles)
    {
        DeviceId = deviceId;
        FolderId = folderId;
        ChangedFiles = changedFiles;
    }
}

public class BlockRequestedEventArgs : EventArgs
{
    public string DeviceId { get; }
    public string FolderId { get; }
    public string FileName { get; }
    public long Offset { get; }
    public int Size { get; }
    public string Hash { get; }
    public int RequestId { get; }
    
    public BlockRequestedEventArgs(string deviceId, string folderId, string fileName, long offset, int size, string hash, int requestId)
    {
        DeviceId = deviceId;
        FolderId = folderId;
        FileName = fileName;
        Offset = offset;
        Size = size;
        Hash = hash;
        RequestId = requestId;
    }
}

public class DownloadProgressEventArgs : EventArgs
{
    public string DeviceId { get; }
    public string FolderId { get; }
    public List<FileDownloadProgress> Progress { get; }
    
    public DownloadProgressEventArgs(string deviceId, string folderId, List<FileDownloadProgress> progress)
    {
        DeviceId = deviceId;
        FolderId = folderId;
        Progress = progress;
    }
}

public class BlockRequestReceivedEventArgs : EventArgs
{
    public string DeviceId { get; }
    public object Request { get; } // Using object to avoid circular dependency
    
    public BlockRequestReceivedEventArgs(string deviceId, object request)
    {
        DeviceId = deviceId;
        Request = request;
    }
}

public class FileDownloadProgress
{
    public string FileName { get; set; } = string.Empty;
    public long BytesDownloaded { get; set; }
    public long TotalBytes { get; set; }
    public DateTime LastUpdate { get; set; } = DateTime.UtcNow;
}