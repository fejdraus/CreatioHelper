using System.Text.Json.Serialization;

namespace CreatioHelper.WebUI.Models;

/// <summary>
/// Folder configuration
/// </summary>
public class FolderConfig
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "sendreceive";

    [JsonPropertyName("devices")]
    public FolderDevice[] Devices { get; set; } = [];

    [JsonPropertyName("rescanIntervalS")]
    public int RescanIntervalS { get; set; } = 3600;

    [JsonPropertyName("fsWatcherEnabled")]
    public bool FsWatcherEnabled { get; set; } = true;

    [JsonPropertyName("fsWatcherDelayS")]
    public double FsWatcherDelayS { get; set; } = 10;

    [JsonPropertyName("ignorePerms")]
    public bool IgnorePerms { get; set; }

    [JsonPropertyName("autoNormalize")]
    public bool AutoNormalize { get; set; } = true;

    [JsonPropertyName("minDiskFree")]
    public MinDiskFree? MinDiskFree { get; set; }

    [JsonPropertyName("versioning")]
    public VersioningConfig? Versioning { get; set; }

    [JsonPropertyName("copiers")]
    public int Copiers { get; set; }

    [JsonPropertyName("pullerMaxPendingKiB")]
    public int PullerMaxPendingKiB { get; set; }

    [JsonPropertyName("hashers")]
    public int Hashers { get; set; }

    [JsonPropertyName("order")]
    public string Order { get; set; } = "random";

    [JsonPropertyName("ignoreDelete")]
    public bool IgnoreDelete { get; set; }

    [JsonPropertyName("scanProgressIntervalS")]
    public int ScanProgressIntervalS { get; set; }

    [JsonPropertyName("pullerPauseS")]
    public int PullerPauseS { get; set; }

    [JsonPropertyName("maxConflicts")]
    public int MaxConflicts { get; set; } = 10;

    [JsonPropertyName("disableSparseFiles")]
    public bool DisableSparseFiles { get; set; }

    [JsonPropertyName("disableTempIndexes")]
    public bool DisableTempIndexes { get; set; }

    [JsonPropertyName("paused")]
    public bool Paused { get; set; }

    [JsonPropertyName("markerName")]
    public string MarkerName { get; set; } = ".stfolder";

    [JsonPropertyName("copyOwnershipFromParent")]
    public bool CopyOwnershipFromParent { get; set; }

    [JsonPropertyName("modTimeWindowS")]
    public int ModTimeWindowS { get; set; }

    [JsonPropertyName("maxConcurrentWrites")]
    public int MaxConcurrentWrites { get; set; }

    [JsonPropertyName("caseSensitiveFS")]
    public bool CaseSensitiveFs { get; set; }

    [JsonPropertyName("junctionsAsDirs")]
    public bool JunctionsAsDirs { get; set; }

    public string DisplayName => string.IsNullOrEmpty(Label) ? Id : Label;

    public FolderType FolderType => Type switch
    {
        "sendreceive" => FolderType.SendReceive,
        "sendonly" => FolderType.SendOnly,
        "receiveonly" => FolderType.ReceiveOnly,
        "receiveencrypted" => FolderType.ReceiveEncrypted,
        _ => FolderType.SendReceive
    };
}

public class FolderDevice
{
    [JsonPropertyName("deviceID")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("introducedBy")]
    public string IntroducedBy { get; set; } = string.Empty;

    [JsonPropertyName("encryptionPassword")]
    public string EncryptionPassword { get; set; } = string.Empty;
}

public class MinDiskFree
{
    [JsonPropertyName("value")]
    public double Value { get; set; } = 1;

    [JsonPropertyName("unit")]
    public string Unit { get; set; } = "%";
}

public class VersioningConfig
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("params")]
    public Dictionary<string, string>? Params { get; set; }
}

/// <summary>
/// Folder status from /rest/db/status
/// </summary>
public class FolderStatus
{
    [JsonPropertyName("folder")]
    public string Folder { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("stateChanged")]
    public DateTime StateChanged { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("errors")]
    public int Errors { get; set; }

    [JsonPropertyName("pullErrors")]
    public int PullErrors { get; set; }

    [JsonPropertyName("globalFiles")]
    public long GlobalFiles { get; set; }

    [JsonPropertyName("globalDirectories")]
    public long GlobalDirectories { get; set; }

    [JsonPropertyName("globalSymlinks")]
    public long GlobalSymlinks { get; set; }

    [JsonPropertyName("globalDeleted")]
    public long GlobalDeleted { get; set; }

    [JsonPropertyName("globalBytes")]
    public long GlobalBytes { get; set; }

    [JsonPropertyName("globalTotalItems")]
    public long GlobalTotalItems { get; set; }

    [JsonPropertyName("localFiles")]
    public long LocalFiles { get; set; }

    [JsonPropertyName("localDirectories")]
    public long LocalDirectories { get; set; }

    [JsonPropertyName("localSymlinks")]
    public long LocalSymlinks { get; set; }

    [JsonPropertyName("localDeleted")]
    public long LocalDeleted { get; set; }

    [JsonPropertyName("localBytes")]
    public long LocalBytes { get; set; }

    [JsonPropertyName("localTotalItems")]
    public long LocalTotalItems { get; set; }

    [JsonPropertyName("needFiles")]
    public long NeedFiles { get; set; }

    [JsonPropertyName("needDirectories")]
    public long NeedDirectories { get; set; }

    [JsonPropertyName("needSymlinks")]
    public long NeedSymlinks { get; set; }

    [JsonPropertyName("needDeletes")]
    public long NeedDeletes { get; set; }

    [JsonPropertyName("needBytes")]
    public long NeedBytes { get; set; }

    [JsonPropertyName("needTotalItems")]
    public long NeedTotalItems { get; set; }

    [JsonPropertyName("receiveOnlyChangedFiles")]
    public long ReceiveOnlyChangedFiles { get; set; }

    [JsonPropertyName("receiveOnlyChangedDirectories")]
    public long ReceiveOnlyChangedDirectories { get; set; }

    [JsonPropertyName("receiveOnlyChangedSymlinks")]
    public long ReceiveOnlyChangedSymlinks { get; set; }

    [JsonPropertyName("receiveOnlyChangedDeletes")]
    public long ReceiveOnlyChangedDeletes { get; set; }

    [JsonPropertyName("receiveOnlyChangedBytes")]
    public long ReceiveOnlyChangedBytes { get; set; }

    [JsonPropertyName("inSyncFiles")]
    public long InSyncFiles { get; set; }

    [JsonPropertyName("inSyncBytes")]
    public long InSyncBytes { get; set; }

    [JsonPropertyName("version")]
    public long Version { get; set; }

    [JsonPropertyName("sequence")]
    public long Sequence { get; set; }

    /// <summary>
    /// Scan progress information (null if not scanning)
    /// </summary>
    [JsonPropertyName("scanProgress")]
    public ScanProgressInfo? ScanProgress { get; set; }

    public double SyncPercentage => GlobalBytes > 0 ? (InSyncBytes * 100.0 / GlobalBytes) : 100.0;

    public FolderState FolderState => State switch
    {
        "idle" => FolderState.Idle,
        "scanning" => FolderState.Scanning,
        "syncing" => FolderState.Syncing,
        "sync-waiting" => FolderState.SyncWaiting,
        "sync-preparing" => FolderState.SyncPreparing,
        "cleaning" => FolderState.Cleaning,
        "clean-waiting" => FolderState.CleanWaiting,
        "error" => FolderState.Error,
        _ => FolderState.Unknown
    };
}

/// <summary>
/// File error from /rest/folder/errors
/// </summary>
public class FileError
{
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("time")]
    public DateTime Time { get; set; }
}

public enum FolderType
{
    SendReceive,
    SendOnly,
    ReceiveOnly,
    ReceiveEncrypted
}

public enum FolderState
{
    Unknown,
    Idle,
    Scanning,
    Syncing,
    SyncWaiting,
    SyncPreparing,
    Cleaning,
    CleanWaiting,
    Error
}

/// <summary>
/// Scan progress information
/// </summary>
public class ScanProgressInfo
{
    [JsonPropertyName("phase")]
    public string Phase { get; set; } = string.Empty;

    [JsonPropertyName("filesScanned")]
    public long FilesScanned { get; set; }

    [JsonPropertyName("filesTotal")]
    public long FilesTotal { get; set; }

    [JsonPropertyName("bytesScanned")]
    public long BytesScanned { get; set; }

    [JsonPropertyName("bytesTotal")]
    public long BytesTotal { get; set; }

    [JsonPropertyName("currentFile")]
    public string? CurrentFile { get; set; }

    [JsonPropertyName("percentComplete")]
    public double PercentComplete { get; set; }

    [JsonPropertyName("bytesPercentComplete")]
    public double BytesPercentComplete { get; set; }

    [JsonPropertyName("filesPerSecond")]
    public long FilesPerSecond { get; set; }

    [JsonPropertyName("bytesPerSecond")]
    public long BytesPerSecond { get; set; }

    [JsonPropertyName("elapsedSeconds")]
    public int ElapsedSeconds { get; set; }

    [JsonPropertyName("estimatedSecondsRemaining")]
    public double? EstimatedSecondsRemaining { get; set; }

    public ScanPhase ScanPhase => Phase.ToLowerInvariant() switch
    {
        "enumerating" => ScanPhase.Enumerating,
        "scanning" => ScanPhase.Scanning,
        "updating" => ScanPhase.Updating,
        "completed" => ScanPhase.Completed,
        "cancelled" => ScanPhase.Cancelled,
        "failed" => ScanPhase.Failed,
        _ => ScanPhase.Unknown
    };
}

public enum ScanPhase
{
    Unknown,
    Enumerating,
    Scanning,
    Updating,
    Completed,
    Cancelled,
    Failed
}
