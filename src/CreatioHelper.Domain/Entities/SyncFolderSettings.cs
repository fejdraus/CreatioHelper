namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Canonical folder settings shared by every source that can produce a <see cref="SyncFolder"/>
/// (config.xml, the API folder DTO, the local database). Each source maps into this type once,
/// and <see cref="SyncFolder.Create"/> is the single place that turns it into an entity.
/// </summary>
public sealed class SyncFolderSettings
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Type { get; init; } = "sendreceive";
    public int RescanIntervalS { get; init; } = 3600;
    public bool FsWatcherEnabled { get; init; } = true;
    public int FsWatcherDelayS { get; init; } = 10;
    public bool IgnorePerms { get; init; }
    public bool AutoNormalizeUnicode { get; init; } = true;
    public string MinDiskFree { get; init; } = "1%";
    public bool CopyOwnershipFromParent { get; init; }
    public int ModTimeWindowS { get; init; }
    public int MaxConflicts { get; init; } = 10;
    public bool DisableSparseFiles { get; init; }
    public bool DisableTempIndexes { get; init; }
    public bool Paused { get; init; }
    public int WeakHashThresholdPct { get; init; } = 25;
    public string MarkerName { get; init; } = ".stfolder";
    public string CopyRangeMethod { get; init; } = "standard";
    public bool CaseSensitiveFS { get; init; }
    public bool JunctionedAsDirectory { get; init; }
    public bool SyncOwnership { get; init; }
    public bool SendOwnership { get; init; }
    public bool SyncXattrs { get; init; }
    public bool SendXattrs { get; init; }

    public IReadOnlyList<string> Devices { get; init; } = Array.Empty<string>();
    public VersioningConfiguration? Versioning { get; init; }
    public string PullOrder { get; init; } = "random";
    public bool IgnoreDelete { get; init; }
}
