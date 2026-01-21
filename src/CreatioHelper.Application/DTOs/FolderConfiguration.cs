namespace CreatioHelper.Application.DTOs;

/// <summary>
/// DTO for folder configuration - matches Syncthing FolderConfiguration structure
/// </summary>
public class FolderConfiguration
{
    // Core identification
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Type { get; set; } = "sendreceive";

    // Devices
    public List<FolderDeviceConfiguration> Devices { get; set; } = new();

    // Scanning
    public int RescanIntervalS { get; set; } = 3600;
    public bool FsWatcherEnabled { get; set; } = true;
    public double FsWatcherDelayS { get; set; } = 10;

    // Permissions & behavior
    public bool IgnorePerms { get; set; }
    public bool IgnoreDelete { get; set; }
    public bool AutoNormalize { get; set; } = true;

    // Disk space
    public FolderMinDiskFree MinDiskFree { get; set; } = new();

    // Versioning
    public FolderVersioningConfiguration? Versioning { get; set; }

    // Pull order
    public string Order { get; set; } = "random";

    // Conflicts
    public int MaxConflicts { get; set; } = 10;

    // Advanced
    public int Copiers { get; set; }
    public int PullerMaxPendingKiB { get; set; }
    public int Hashers { get; set; }
    public bool DisableSparseFiles { get; set; }
    public bool DisableTempIndexes { get; set; }
    public bool DisableFsync { get; set; }
    public int MaxConcurrentWrites { get; set; } = 16;
    public bool CaseSensitiveFS { get; set; }
    public bool JunctionsAsDirs { get; set; }

    // Ownership & extended attributes
    public bool SyncOwnership { get; set; }
    public bool SendOwnership { get; set; }
    public bool SyncXattrs { get; set; }
    public bool SendXattrs { get; set; }
    public bool CopyOwnershipFromParent { get; set; }

    // Other
    public string MarkerName { get; set; } = ".stfolder";
    public int ModTimeWindowS { get; set; }
    public string CopyRangeMethod { get; set; } = "standard";
    public int WeakHashThresholdPct { get; set; } = 25;
    public bool Paused { get; set; }
}

/// <summary>
/// Device configuration within a folder
/// </summary>
public class FolderDeviceConfiguration
{
    public string DeviceId { get; set; } = string.Empty;
    public string IntroducedBy { get; set; } = string.Empty;
    public string EncryptionPassword { get; set; } = string.Empty;
}

/// <summary>
/// Minimum disk free space configuration
/// </summary>
public class FolderMinDiskFree
{
    public double Value { get; set; } = 1;
    public string Unit { get; set; } = "%";

    public override string ToString()
    {
        return $"{Value}{Unit}";
    }

    public static FolderMinDiskFree Parse(string value)
    {
        if (string.IsNullOrEmpty(value))
            return new FolderMinDiskFree();

        // Parse values like "1%", "100MB", "1GB"
        var result = new FolderMinDiskFree();

        if (value.EndsWith("%"))
        {
            result.Unit = "%";
            if (double.TryParse(value[..^1], out var pct))
                result.Value = pct;
        }
        else if (value.EndsWith("TB", StringComparison.OrdinalIgnoreCase))
        {
            result.Unit = "TB";
            if (double.TryParse(value[..^2], out var tb))
                result.Value = tb;
        }
        else if (value.EndsWith("GB", StringComparison.OrdinalIgnoreCase))
        {
            result.Unit = "GB";
            if (double.TryParse(value[..^2], out var gb))
                result.Value = gb;
        }
        else if (value.EndsWith("MB", StringComparison.OrdinalIgnoreCase))
        {
            result.Unit = "MB";
            if (double.TryParse(value[..^2], out var mb))
                result.Value = mb;
        }
        else if (value.EndsWith("kB", StringComparison.OrdinalIgnoreCase))
        {
            result.Unit = "kB";
            if (double.TryParse(value[..^2], out var kb))
                result.Value = kb;
        }

        return result;
    }
}

/// <summary>
/// Folder versioning configuration
/// </summary>
public class FolderVersioningConfiguration
{
    public string Type { get; set; } = string.Empty;
    public Dictionary<string, string> Params { get; set; } = new();
    public int CleanupIntervalS { get; set; } = 3600;
    public string FsPath { get; set; } = string.Empty;
    public string FsType { get; set; } = "basic";

    public bool IsEnabled => !string.IsNullOrEmpty(Type) && Type != "none";
}
