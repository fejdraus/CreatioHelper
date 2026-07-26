namespace CreatioHelper.Domain.Entities;

public static class ConfigXmlFolderExtensions
{
    public static SyncFolderSettings ToSyncFolderSettings(this ConfigXmlFolder config) => new()
    {
        Id = config.Id,
        Label = config.Label,
        Path = config.Path,
        Type = config.Type,
        RescanIntervalS = config.RescanIntervalS,
        FsWatcherEnabled = config.FsWatcherEnabled,
        FsWatcherDelayS = config.FsWatcherDelayS,
        IgnorePerms = config.IgnorePerms,
        AutoNormalizeUnicode = config.AutoNormalize,
        MinDiskFree = config.MinDiskFree?.ToString() ?? "1%",
        CopyOwnershipFromParent = config.CopyOwnershipFromParent,
        ModTimeWindowS = config.ModTimeWindowS,
        MaxConflicts = config.MaxConflicts,
        DisableSparseFiles = config.DisableSparseFiles,
        DisableTempIndexes = config.DisableTempIndexes,
        Paused = config.Paused,
        WeakHashThresholdPct = config.WeakHashThresholdPct,
        MarkerName = config.MarkerName,
        CopyRangeMethod = config.CopyRangeMethod,
        CaseSensitiveFS = config.CaseSensitiveFS,
        JunctionedAsDirectory = config.JunctionsAsDirs,
        SyncOwnership = config.SyncOwnership,
        SendOwnership = config.SendOwnership,
        SyncXattrs = config.SyncXattrs,
        SendXattrs = config.SendXattrs,
        Devices = config.Devices.Select(d => d.Id).ToList(),
        Versioning = ToVersioning(config.Versioning),
        PullOrder = config.Order,
        IgnoreDelete = config.IgnoreDelete
    };

    private static VersioningConfiguration? ToVersioning(ConfigXmlVersioning? versioning)
    {
        if (versioning == null || string.IsNullOrEmpty(versioning.Type))
        {
            return null;
        }

        return new VersioningConfiguration
        {
            Type = versioning.Type,
            Params = versioning.Params?.ToDictionary(p => p.Key, p => p.Val) ?? new Dictionary<string, string>(),
            CleanupIntervalS = versioning.CleanupIntervalS,
            FSPath = versioning.FsPath,
            FSType = versioning.FsType
        };
    }
}
