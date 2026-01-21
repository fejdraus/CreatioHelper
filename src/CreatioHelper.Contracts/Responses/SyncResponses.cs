namespace CreatioHelper.Contracts.Responses;

public class SyncSystemStatus
{
    public TimeSpan Uptime { get; set; }
    public int ConnectedDevices { get; set; }
    public int TotalDevices { get; set; }
    public int SyncedFolders { get; set; }
    public int TotalFolders { get; set; }
    public long TotalBytesIn { get; set; }
    public long TotalBytesOut { get; set; }
    public bool IsOnline { get; set; }
}

public class SyncDeviceDto
{
    public string DeviceId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsConnected { get; set; }
    public DateTime LastSeen { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsPaused { get; set; }
    public List<string> Addresses { get; set; } = new();
}

public class SyncFolderDto
{
    public string FolderId { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsPaused { get; set; }
    public string State { get; set; } = string.Empty;
    public long GlobalBytes { get; set; }
    public long LocalBytes { get; set; }
    public long GlobalFiles { get; set; }
    public long LocalFiles { get; set; }
    public DateTime LastScan { get; set; }
    public DateTime LastSync { get; set; }
    public List<string> DeviceIds { get; set; } = new();
}

public class SyncEventDto
{
    public string Type { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string? FolderId { get; set; }
    public string? DeviceId { get; set; }
    public string? Message { get; set; }
    public Dictionary<string, object> Data { get; set; } = new();
}