using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Agent.Syncthing;

public static class SyncthingConfigContract
{
    public const int Version = 37;

    public static object Build(
        IEnumerable<SyncDevice> devices,
        IEnumerable<SyncFolder> folders,
        SyncConfiguration syncConfig) => new
        {
            version = Version,
            folders = folders.Select(Folder).ToArray(),
            devices = devices.Select(Device).ToArray(),
            gui = Gui(syncConfig),
            ldap = Ldap(syncConfig),
            options = Options(),
            ignoredDevices = Array.Empty<object>(),
            pendingDevices = Array.Empty<object>(),
            ignoredFolders = Array.Empty<object>()
        };

    public static object Folder(SyncFolder folder) => FolderShape(
        folder.Id,
        folder.Label,
        folder.Path,
        folder.SyncType switch
        {
            SyncFolderType.SendOnly => "sendonly",
            SyncFolderType.ReceiveOnly => "receiveonly",
            SyncFolderType.Master => "receiveencrypted",
            _ => "sendreceive"
        },
        folder.Devices.Select(deviceId => new { deviceID = deviceId }).ToArray(),
        folder.IsPaused);

    public static object DefaultFolder() => FolderShape(
        string.Empty,
        string.Empty,
        string.Empty,
        "sendreceive",
        Array.Empty<object>(),
        paused: false);

    private static object FolderShape(
        string id,
        string label,
        string path,
        string type,
        object[] devices,
        bool paused) => new
    {
        id,
        label,
        filesystemType = "basic",
        path,
        type,
        devices,
        rescanIntervalS = 3600,
        fsWatcherEnabled = true,
        fsWatcherDelayS = 10,
        ignorePerms = false,
        autoNormalize = true,
        minDiskFree = new { value = 1, unit = "%" },
        versioning = new { type = string.Empty, @params = new { } },
        copiers = 0,
        pullerMaxPendingKiB = 0,
        hashers = 0,
        order = "random",
        ignoreDelete = false,
        scanProgressIntervalS = 0,
        pullerPauseS = 0,
        maxConflicts = 10,
        disableSparseFiles = false,
        disableTempIndexes = false,
        paused,
        weakHashThresholdPct = 25,
        markerName = ".stfolder",
        copyOwnershipFromParent = false,
        modTimeWindowS = 0,
        maxConcurrentWrites = 2,
        disableFsync = false,
        blockPullOrder = "standard",
        copyRangeMethod = "standard",
        caseSensitiveFS = false,
        junctionsAsDirs = false,
        syncOwnership = false,
        sendOwnership = false,
        syncXattrs = false,
        sendXattrs = false
    };

    public static object Device(SyncDevice device) => DeviceShape(
        device.DeviceId,
        device.DeviceName,
        device.Addresses.ToArray(),
        device.IsPaused);

    public static object DefaultDevice() => DeviceShape(
        string.Empty,
        string.Empty,
        new[] { "dynamic" },
        paused: false);

    private static object DeviceShape(
        string deviceID,
        string name,
        string[] addresses,
        bool paused) => new
    {
        deviceID,
        name,
        addresses,
        compression = "metadata",
        certName = string.Empty,
        introducer = false,
        skipIntroductionRemovals = false,
        introducedBy = string.Empty,
        paused,
        allowedNetworks = Array.Empty<string>(),
        autoAcceptFolders = false,
        maxSendKbps = 0,
        maxRecvKbps = 0,
        ignoredFolders = Array.Empty<string>(),
        pendingFolders = Array.Empty<string>(),
        maxRequestKiB = 0,
        untrusted = false,
        remoteGUIPort = 0
    };

    public static object Options() => new
    {
        listenAddresses = new[] { "default" },
        globalAnnounceServers = new[] { "default" },
        globalAnnounceEnabled = true,
        localAnnounceEnabled = true,
        localAnnouncePort = 21027,
        localAnnounceMCAddr = "[ff12::8384]:21027",
        maxSendKbps = 0,
        maxRecvKbps = 0,
        reconnectionIntervalS = 60,
        relaysEnabled = true,
        relayReconnectIntervalM = 10,
        startBrowser = true,
        natEnabled = true,
        natLeaseMinutes = 60,
        natRenewalMinutes = 30,
        natTimeoutSeconds = 10,
        urAccepted = -1,
        urSeen = 3,
        urUniqueId = string.Empty,
        urURL = "",
        urPostInsecurely = false,
        urInitialDelayS = 1800,
        autoUpgradeEnabled = false,
        autoUpgradeIntervalH = 12,
        upgradeToPreReleases = false,
        keepTemporariesH = 24,
        cacheIgnoredFiles = false,
        progressUpdateIntervalS = 5,
        limitBandwidthInLan = false,
        minHomeDiskFree = new { value = 1, unit = "%" },
        releasesURL = "https://api.github.com/repos/syncthing/syncthing/releases?per_page=30",
        alwaysLocalNets = Array.Empty<string>(),
        overwriteRemoteDeviceNamesOnConnect = false,
        tempIndexMinBlocks = 10,
        unackedNotificationIDs = Array.Empty<string>(),
        trafficClass = 0,
        defaultFolderPath = "~",
        setLowPriority = true,
        maxFolderConcurrency = 0,
        crURL = "",
        crashReportingEnabled = false,
        stunKeepaliveStartS = 180,
        stunKeepaliveMinS = 20,
        stunServers = new[] { "default" },
        databaseTuning = "auto",
        maxCIRequestKiB = 0,
        announceLANAddresses = true,
        sendFullIndexOnUpgrade = false
    };

    public static object Gui(SyncConfiguration syncConfig) => new
    {
        enabled = syncConfig.GuiEnabled,
        address = syncConfig.GuiAddress,
        unixSocketPermissions = "0700",
        user = syncConfig.GuiUser,
        password = syncConfig.GuiPassword,
        authMode = syncConfig.AuthMode,
        useTLS = syncConfig.GuiTls,
        apiKey = syncConfig.GuiApiKey,
        insecureAdminAccess = false,
        theme = "default",
        debugging = false,
        insecureSkipHostcheck = false,
        insecureAllowFrameLoading = false
    };

    public static object Ldap(SyncConfiguration syncConfig) => new
    {
        address = syncConfig.LdapAddress,
        bindDN = syncConfig.LdapBindDN,
        transport = syncConfig.LdapTransport,
        insecureSkipVerify = syncConfig.LdapInsecureSkipVerify,
        searchBaseDN = syncConfig.LdapSearchBaseDN,
        searchFilter = syncConfig.LdapSearchFilter
    };
}
