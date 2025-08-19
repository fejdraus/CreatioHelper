using CreatioHelper.Domain.Common;

namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Represents a sync folder configuration (based on Syncthing folder concept)
/// </summary>
public class SyncFolder : AggregateRoot
{
    public string FolderId { get; private set; } = string.Empty;
    public string Label { get; private set; } = string.Empty;
    public string Path { get; private set; } = string.Empty;
    public FolderType Type { get; private set; } = FolderType.SendReceive;
    public bool IsPaused { get; private set; }
    public int RescanIntervalSeconds { get; private set; } = 3600; // 1 hour default
    public DateTime LastScan { get; private set; }
    public long GlobalBytes { get; private set; }
    public long LocalBytes { get; private set; }
    public int GlobalFiles { get; private set; }
    public int LocalFiles { get; private set; }
    public List<string> IgnorePatterns { get; private set; } = new();
    public List<SyncFolderDevice> Devices { get; private set; } = new();
    public VersioningConfiguration? Versioning { get; private set; }
    public bool WatchForChanges { get; private set; } = true;
    public int MaxConflicts { get; private set; } = 10;

    private SyncFolder() { } // For EF Core

    public SyncFolder(string folderId, string label, string path, FolderType type = FolderType.SendReceive)
    {
        FolderId = folderId;
        Label = label;
        Path = path;
        Type = type;
        LastScan = DateTime.UtcNow;
    }

    public void AddDevice(string deviceId, bool isReceiveEncrypted = false)
    {
        if (!Devices.Any(d => d.DeviceId == deviceId))
        {
            Devices.Add(new SyncFolderDevice(deviceId, isReceiveEncrypted));
        }
    }

    public void RemoveDevice(string deviceId)
    {
        var device = Devices.FirstOrDefault(d => d.DeviceId == deviceId);
        if (device != null)
        {
            Devices.Remove(device);
        }
    }

    public void SetPaused(bool paused)
    {
        IsPaused = paused;
    }

    public void UpdateStatistics(long globalBytes, long localBytes, int globalFiles, int localFiles)
    {
        GlobalBytes = globalBytes;
        LocalBytes = localBytes;
        GlobalFiles = globalFiles;
        LocalFiles = localFiles;
    }

    public void SetVersioning(VersioningConfiguration versioning)
    {
        Versioning = versioning;
    }

    public void AddIgnorePattern(string pattern)
    {
        if (!IgnorePatterns.Contains(pattern))
        {
            IgnorePatterns.Add(pattern);
        }
    }

    public void RemoveIgnorePattern(string pattern)
    {
        IgnorePatterns.Remove(pattern);
    }

    public void UpdateLastScan()
    {
        LastScan = DateTime.UtcNow;
    }

    public override bool IsValid()
    {
        return !string.IsNullOrEmpty(FolderId) && 
               !string.IsNullOrEmpty(Label) && 
               !string.IsNullOrEmpty(Path) &&
               RescanIntervalSeconds >= 0;
    }

    public override IEnumerable<string> GetBrokenRules()
    {
        var brokenRules = new List<string>();

        if (string.IsNullOrEmpty(FolderId))
            brokenRules.Add("Folder ID cannot be empty");

        if (string.IsNullOrEmpty(Label))
            brokenRules.Add("Folder label cannot be empty");

        if (string.IsNullOrEmpty(Path))
            brokenRules.Add("Folder path cannot be empty");

        if (RescanIntervalSeconds < 0)
            brokenRules.Add("Rescan interval cannot be negative");

        if (MaxConflicts < 0)
            brokenRules.Add("Max conflicts cannot be negative");

        return brokenRules;
    }
}

public class SyncFolderDevice
{
    public string DeviceId { get; private set; } = string.Empty;
    public bool IsReceiveEncrypted { get; private set; }

    private SyncFolderDevice() { } // For EF Core

    public SyncFolderDevice(string deviceId, bool isReceiveEncrypted = false)
    {
        DeviceId = deviceId;
        IsReceiveEncrypted = isReceiveEncrypted;
    }
}

public class VersioningConfiguration
{
    public VersioningType Type { get; set; } = VersioningType.Simple;
    public Dictionary<string, string> Parameters { get; set; } = new();
}

public enum FolderType
{
    SendReceive,
    SendOnly,
    ReceiveOnly,
    ReceiveEncrypted
}

public enum VersioningType
{
    None,
    Simple,
    Staggered,
    External,
    TrashCan
}