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
public class BepIndexReceivedEventArgs : EventArgs
{
    public string FolderId { get; set; } = string.Empty;
    public IEnumerable<object> Files { get; set; } = new List<object>();
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
    event EventHandler<BepIndexReceivedEventArgs>? IndexReceived;
    event EventHandler<BepBlockRequestReceivedEventArgs>? BlockRequestReceived;
    event EventHandler<BepBlockResponseReceivedEventArgs>? BlockResponseReceived;
    event EventHandler<BepPingReceivedEventArgs>? PingReceived;
    event EventHandler<BepPongReceivedEventArgs>? PongReceived;
    event EventHandler<BepConnectionClosedEventArgs>? ConnectionClosed;
}