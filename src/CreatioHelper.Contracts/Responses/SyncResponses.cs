using System.Text.Json.Serialization;

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

/// <summary>
/// System status for SignalR broadcast
/// </summary>
public class SystemStatusDto
{
    [JsonPropertyName("myID")]
    public string MyId { get; set; } = string.Empty;

    [JsonPropertyName("startTime")]
    public DateTime StartTime { get; set; }

    [JsonPropertyName("uptime")]
    public long Uptime { get; set; }

    [JsonPropertyName("sys")]
    public long Sys { get; set; }

    [JsonPropertyName("goroutines")]
    public int Goroutines { get; set; }

    [JsonPropertyName("totalIn")]
    public long TotalIn { get; set; }

    [JsonPropertyName("totalOut")]
    public long TotalOut { get; set; }

    [JsonPropertyName("inBytesPerSec")]
    public long InBytesPerSec { get; set; }

    [JsonPropertyName("outBytesPerSec")]
    public long OutBytesPerSec { get; set; }

    [JsonPropertyName("dbSize")]
    public long DbSize { get; set; }

    [JsonPropertyName("cpuPercent")]
    public double CpuPercent { get; set; }

    [JsonPropertyName("alloc")]
    public long Alloc { get; set; }

    [JsonPropertyName("appMemory")]
    public long AppMemory { get; set; }

    [JsonPropertyName("osMemoryUsed")]
    public long OsMemoryUsed { get; set; }

    [JsonPropertyName("totalPhysicalMemory")]
    public long TotalPhysicalMemory { get; set; }

    [JsonPropertyName("gcGen0Collections")]
    public int GcGen0Collections { get; set; }

    [JsonPropertyName("gcGen1Collections")]
    public int GcGen1Collections { get; set; }

    [JsonPropertyName("gcGen2Collections")]
    public int GcGen2Collections { get; set; }

    [JsonPropertyName("gcTotalPauseMs")]
    public double GcTotalPauseMs { get; set; }

    [JsonPropertyName("heapSizeBytes")]
    public long HeapSizeBytes { get; set; }

    [JsonPropertyName("heapFragmentedBytes")]
    public long HeapFragmentedBytes { get; set; }

    [JsonPropertyName("processHandleCount")]
    public int ProcessHandleCount { get; set; }

    [JsonPropertyName("processThreadCount")]
    public int ProcessThreadCount { get; set; }

    [JsonPropertyName("totalBytesIn")]
    public long TotalBytesIn { get; set; }

    [JsonPropertyName("totalBytesOut")]
    public long TotalBytesOut { get; set; }
}

/// <summary>
/// Folder status for SignalR broadcast
/// </summary>
public class FolderStatusDto
{
    [JsonPropertyName("folder")]
    public string Folder { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("globalFiles")]
    public long GlobalFiles { get; set; }

    [JsonPropertyName("globalDirectories")]
    public long GlobalDirectories { get; set; }

    [JsonPropertyName("globalBytes")]
    public long GlobalBytes { get; set; }

    [JsonPropertyName("localFiles")]
    public long LocalFiles { get; set; }

    [JsonPropertyName("localDirectories")]
    public long LocalDirectories { get; set; }

    [JsonPropertyName("localBytes")]
    public long LocalBytes { get; set; }

    [JsonPropertyName("needFiles")]
    public long NeedFiles { get; set; }

    [JsonPropertyName("needBytes")]
    public long NeedBytes { get; set; }

    [JsonPropertyName("inSyncBytes")]
    public long InSyncBytes { get; set; }

    [JsonPropertyName("syncPercentage")]
    public double SyncPercentage { get; set; }
}

/// <summary>
/// Connection info for SignalR broadcast
/// </summary>
public class ConnectionInfoDto
{
    [JsonPropertyName("deviceID")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("connected")]
    public bool Connected { get; set; }

    [JsonPropertyName("address")]
    public string Address { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("crypto")]
    public string Crypto { get; set; } = string.Empty;

    [JsonPropertyName("clientVersion")]
    public string ClientVersion { get; set; } = string.Empty;

    [JsonPropertyName("connectedAt")]
    public DateTime? ConnectedAt { get; set; }

    [JsonPropertyName("connectionDuration")]
    public double? ConnectionDurationSeconds { get; set; }

    [JsonPropertyName("isRelay")]
    public bool IsRelay { get; set; }

    [JsonPropertyName("inBytesTotal")]
    public long InBytesTotal { get; set; }

    [JsonPropertyName("outBytesTotal")]
    public long OutBytesTotal { get; set; }

    [JsonPropertyName("inBytesPerSec")]
    public long InBytesPerSec { get; set; }

    [JsonPropertyName("outBytesPerSec")]
    public long OutBytesPerSec { get; set; }
}