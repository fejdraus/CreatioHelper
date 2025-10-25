using CreatioHelper.Domain.Common;

namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Represents a sync folder configuration (based on Syncthing FolderConfiguration)
/// </summary>
public class SyncFolder : AggregateRoot
{
    // Core Syncthing FolderConfiguration properties
    public new string Id { get; private set; } = string.Empty;
    public string Label { get; private set; } = string.Empty;
    public string Path { get; private set; } = string.Empty;
    public string Type { get; private set; } = "sendreceive";
    public SyncFolderType SyncType { get; private set; } = SyncFolderType.SendReceive;
    public ConflictResolutionPolicy ConflictPolicy { get; private set; } = ConflictResolutionPolicy.CreateCopies;
    public List<string> Devices { get; set; } = new();
    public int RescanIntervalS { get; set; } = 3600;
    public bool FSWatcherEnabled { get; set; } = true;
    public int FSWatcherDelayS { get; set; } = 10;
    public bool IgnorePerms { get; set; }
    public bool AutoNormalizeUnicode { get; set; } = true;
    
    // Additional properties for SyncthingConfigLoader compatibility
    public int FsWatcherDelaySeconds { get; set; } = 10;
    public bool IgnorePermissions { get; set; }
    public bool AutoNormalize { get; set; } = true;
    public bool IgnoreDelete { get; set; }
    public int ScanProgressIntervalSeconds { get; set; } = 0;
    public string FilesystemType { get; set; } = "basic";
    public int RescanIntervalSeconds { get; set; } = 3600;
    public bool FsWatcherEnabled { get; set; } = true;
    public string MinDiskFree { get; private set; } = "1%";
    public VersioningConfiguration? Versioning { get; private set; }
    public bool CopyOwnershipFromParent { get; private set; }
    public int ModTimeWindowS { get; private set; }
    public int MaxConflicts { get; set; } = 10;
    public bool DisableSparseFiles { get; set; }
    public bool DisableTempIndexes { get; set; }
    public bool Paused { get; private set; }
    public int WeakHashThresholdPct { get; set; } = 25;
    public string MarkerName { get; set; } = ".stfolder";
    public string CopyRangeMethod { get; private set; } = "standard";
    public bool CaseSensitiveFS { get; private set; } = true;
    public bool JunctionedAsDirectory { get; private set; }
    public bool SyncOwnership { get; private set; }
    public bool SendOwnership { get; private set; }
    public bool SyncXattrs { get; private set; }
    public bool SendXattrs { get; private set; }
    
    // Advanced sync mode properties
    public bool AllowRevert { get; private set; } = true;
    public bool AllowOverride { get; private set; } = true;
    
    // Runtime statistics (not in Syncthing FolderConfiguration)
    public DateTime? LastScan { get; private set; }
    public long FileCount { get; private set; }
    public long TotalSize { get; private set; }
    
    // Compatibility properties for old code
    public string FolderId => Id;
    public bool IsPaused => Paused;
    
    // Advanced sync mode capabilities
    public bool CanReceiveChanges => SyncType == SyncFolderType.SendReceive || 
                                     SyncType == SyncFolderType.ReceiveOnly || 
                                     SyncType == SyncFolderType.Slave;
    public bool CanSendChanges => SyncType == SyncFolderType.SendReceive || 
                                  SyncType == SyncFolderType.SendOnly || 
                                  SyncType == SyncFolderType.Master;

    private SyncFolder() { } // For EF Core

    public SyncFolder(string id, string label, string path, string type = "sendreceive")
    {
        Id = id;
        Label = label;
        Path = path;
        Type = type;
        SyncType = ParseSyncFolderType(type);
        ConflictPolicy = GetDefaultPolicyForType(SyncType);
        LastScan = DateTime.UtcNow;
    }

    // Public constructor for database mapping (25 parameters total)
    public SyncFolder(string id, string label, string path, string type,
        int rescanIntervalS, bool fsWatcherEnabled, int fsWatcherDelayS,
        bool ignorePerms, bool autoNormalizeUnicode, string minDiskFree,
        bool copyOwnershipFromParent, int modTimeWindowS, int maxConflicts,
        bool disableSparseFiles, bool disableTempIndexes, bool paused,
        int weakHashThresholdPct, string markerName, string copyRangeMethod,
        bool caseSensitiveFS, bool junctionedAsDirectory,
        bool syncOwnership, bool sendOwnership, bool syncXattrs, bool sendXattrs)
    {
        Id = id;
        Label = label;
        Path = path;
        Type = type;
        RescanIntervalS = rescanIntervalS;
        FSWatcherEnabled = fsWatcherEnabled;
        FSWatcherDelayS = fsWatcherDelayS;
        IgnorePerms = ignorePerms;
        AutoNormalizeUnicode = autoNormalizeUnicode;
        MinDiskFree = minDiskFree;
        CopyOwnershipFromParent = copyOwnershipFromParent;
        ModTimeWindowS = modTimeWindowS;
        MaxConflicts = maxConflicts;
        DisableSparseFiles = disableSparseFiles;
        DisableTempIndexes = disableTempIndexes;
        Paused = paused;
        WeakHashThresholdPct = weakHashThresholdPct;
        MarkerName = markerName;
        CopyRangeMethod = copyRangeMethod;
        CaseSensitiveFS = caseSensitiveFS;
        JunctionedAsDirectory = junctionedAsDirectory;
        SyncOwnership = syncOwnership;
        SendOwnership = sendOwnership;
        SyncXattrs = syncXattrs;
        SendXattrs = sendXattrs;
        SyncType = ParseSyncFolderType(type);
        ConflictPolicy = GetDefaultPolicyForType(SyncType);
        LastScan = DateTime.UtcNow;
    }

    public void AddDevice(string deviceId)
    {
        if (!Devices.Contains(deviceId))
        {
            Devices.Add(deviceId);
        }
    }

    public void RemoveDevice(string deviceId)
    {
        Devices.Remove(deviceId);
    }

    public void SetDevices(List<string> devices)
    {
        Devices.Clear();
        Devices.AddRange(devices);
    }

    public void SetPaused(bool paused)
    {
        Paused = paused;
    }

    public void UpdateStatistics(long fileCount, long totalSize)
    {
        FileCount = fileCount;
        TotalSize = totalSize;
        LastScan = DateTime.UtcNow;
    }

    public void UpdateLastScan()
    {
        LastScan = DateTime.UtcNow;
    }

    public void SetVersioning(VersioningConfiguration versioning)
    {
        Versioning = versioning;
    }

    public void SetFSWatcher(bool enabled, int delayS = 10)
    {
        FSWatcherEnabled = enabled;
        FSWatcherDelayS = delayS;
    }

    public void SetMinDiskFree(string minDiskFree)
    {
        MinDiskFree = minDiskFree;
    }
    
    /// <summary>
    /// Установить режим синхронизации и политику разрешения конфликтов
    /// </summary>
    public void SetSyncMode(SyncFolderType syncType, ConflictResolutionPolicy? customPolicy = null)
    {
        SyncType = syncType;
        Type = ConvertSyncFolderTypeToString(syncType);
        ConflictPolicy = customPolicy ?? GetDefaultPolicyForType(syncType);
        
        // Валидация совместимости политик
        ValidatePolicyCompatibility();
    }
    
    /// <summary>
    /// Установить возможности Override и Revert для продвинутых режимов
    /// </summary>
    public void SetAdvancedCapabilities(bool allowRevert, bool allowOverride)
    {
        AllowRevert = allowRevert;
        AllowOverride = allowOverride;
    }
    
    /// <summary>
    /// Проверить, совместима ли политика разрешения конфликтов с типом папки
    /// </summary>
    private void ValidatePolicyCompatibility()
    {
        switch (SyncType)
        {
            case SyncFolderType.SendOnly:
            case SyncFolderType.Master:
                if (ConflictPolicy == ConflictResolutionPolicy.Revert)
                    throw new InvalidOperationException("Send-only folders cannot use revert policy");
                break;
                
            case SyncFolderType.ReceiveOnly:
            case SyncFolderType.Slave:
                if (ConflictPolicy == ConflictResolutionPolicy.Override)
                    throw new InvalidOperationException("Receive-only folders cannot use override policy");
                break;
        }
    }
    
    /// <summary>
    /// Парсинг строкового типа папки в enum (совместимость с Syncthing)
    /// </summary>
    private static SyncFolderType ParseSyncFolderType(string type)
    {
        return type.ToLowerInvariant() switch
        {
            "sendreceive" => SyncFolderType.SendReceive,
            "sendonly" => SyncFolderType.SendOnly,
            "receiveonly" => SyncFolderType.ReceiveOnly,
            "master" => SyncFolderType.Master,
            "slave" => SyncFolderType.Slave,
            _ => SyncFolderType.SendReceive
        };
    }
    
    /// <summary>
    /// Конвертация enum в строковый тип (совместимость с Syncthing)
    /// </summary>
    private static string ConvertSyncFolderTypeToString(SyncFolderType syncType)
    {
        return syncType switch
        {
            SyncFolderType.SendReceive => "sendreceive",
            SyncFolderType.SendOnly => "sendonly",
            SyncFolderType.ReceiveOnly => "receiveonly",
            SyncFolderType.Master => "master",
            SyncFolderType.Slave => "slave",
            _ => "sendreceive"
        };
    }
    
    /// <summary>
    /// Получить политику разрешения конфликтов по умолчанию для типа папки
    /// </summary>
    private static ConflictResolutionPolicy GetDefaultPolicyForType(SyncFolderType folderType)
    {
        return folderType switch
        {
            SyncFolderType.SendReceive => ConflictResolutionPolicy.CreateCopies,
            SyncFolderType.SendOnly => ConflictResolutionPolicy.UseLocal,
            SyncFolderType.ReceiveOnly => ConflictResolutionPolicy.UseRemote,
            SyncFolderType.Master => ConflictResolutionPolicy.Override,
            SyncFolderType.Slave => ConflictResolutionPolicy.Revert,
            _ => ConflictResolutionPolicy.CreateCopies
        };
    }

    public override bool IsValid()
    {
        return !string.IsNullOrEmpty(Id) && 
               !string.IsNullOrEmpty(Label) && 
               !string.IsNullOrEmpty(Path) &&
               RescanIntervalS >= 0;
    }

    public override IEnumerable<string> GetBrokenRules()
    {
        var brokenRules = new List<string>();

        if (string.IsNullOrEmpty(Id))
            brokenRules.Add("Folder ID cannot be empty");

        if (string.IsNullOrEmpty(Label))
            brokenRules.Add("Folder label cannot be empty");

        if (string.IsNullOrEmpty(Path))
            brokenRules.Add("Folder path cannot be empty");

        if (RescanIntervalS < 0)
            brokenRules.Add("Rescan interval cannot be negative");

        if (MaxConflicts < 0)
            brokenRules.Add("Max conflicts cannot be negative");
            
        // Валидация режимов синхронизации
        try
        {
            ValidatePolicyCompatibility();
        }
        catch (InvalidOperationException ex)
        {
            brokenRules.Add(ex.Message);
        }

        return brokenRules;
    }
}

public enum VersioningType
{
    None,
    Simple,
    Staggered,
    External,
    TrashCan
}