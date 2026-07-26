using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Application.DTOs;

public static class FolderConfigurationExtensions
{
    public static SyncFolderSettings ToSyncFolderSettings(this FolderConfiguration config) => new()
    {
        Id = config.Id,
        Label = config.Label,
        Path = config.Path,
        Type = config.Type,
        RescanIntervalS = config.RescanIntervalS,
        FsWatcherEnabled = config.FsWatcherEnabled,
        FsWatcherDelayS = (int)config.FsWatcherDelayS,
        IgnorePerms = config.IgnorePerms,
        AutoNormalizeUnicode = config.AutoNormalize,
        MinDiskFree = config.MinDiskFree.ToString(),
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
        Devices = config.Devices.Select(d => d.DeviceId).ToList(),
        Versioning = ToVersioning(config.Versioning),
        PullOrder = config.Order,
        IgnoreDelete = config.IgnoreDelete
    };

    private static VersioningConfiguration? ToVersioning(FolderVersioningConfiguration? versioning)
    {
        if (versioning?.IsEnabled != true)
        {
            return null;
        }

        return new VersioningConfiguration
        {
            Type = versioning.Type,
            Params = versioning.Params,
            CleanupIntervalS = versioning.CleanupIntervalS,
            FSPath = versioning.FsPath,
            FSType = versioning.FsType
        };
    }
}
