namespace CreatioHelper.Application.Interfaces;

public interface IConnectionLifecycle
{
    event EventHandler<ConnectionStateEventArgs>? StateChanged;
    ConnectionState State { get; }
    ConnectionHealth GetHealth();
}

public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Disconnecting,
    Failed
}

public class ConnectionStateEventArgs : EventArgs
{
    public ConnectionState OldState { get; init; }
    public ConnectionState NewState { get; init; }
    public string? Reason { get; init; }
    public string? DeviceId { get; init; }
}

public class ConnectionHealth
{
    public double Score { get; set; } // 0-100
    public TimeSpan Latency { get; set; }
    public DateTime LastActivity { get; set; }
    public long BytesSent { get; set; }
    public long BytesReceived { get; set; }
    public int ErrorCount { get; set; }
}
