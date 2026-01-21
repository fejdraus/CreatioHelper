using System.Xml.Serialization;

namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Syncthing-compatible configuration XML structure.
/// Matches Syncthing's config.xml format version 51 for full compatibility.
/// </summary>
[XmlRoot("configuration")]
public class ConfigXml
{
    [XmlAttribute("version")]
    public int Version { get; set; } = 51;

    [XmlElement("folder")]
    public List<ConfigXmlFolder> Folders { get; set; } = new();

    [XmlElement("device")]
    public List<ConfigXmlDevice> Devices { get; set; } = new();

    [XmlElement("gui")]
    public ConfigXmlGui Gui { get; set; } = new();

    [XmlElement("ldap")]
    public string? Ldap { get; set; }

    [XmlElement("options")]
    public ConfigXmlOptions Options { get; set; } = new();

    [XmlElement("remoteIgnoredDevices")]
    public ConfigXmlRemoteIgnoredDevices? RemoteIgnoredDevices { get; set; }

    [XmlElement("defaults")]
    public ConfigXmlDefaults? Defaults { get; set; }
}

/// <summary>
/// Folder configuration element
/// </summary>
public class ConfigXmlFolder
{
    // === Attributes ===
    [XmlAttribute("id")]
    public string Id { get; set; } = string.Empty;

    [XmlAttribute("label")]
    public string Label { get; set; } = string.Empty;

    [XmlAttribute("path")]
    public string Path { get; set; } = string.Empty;

    [XmlAttribute("type")]
    public string Type { get; set; } = "sendreceive"; // sendreceive, sendonly, receiveonly, receiveencrypted

    [XmlAttribute("rescanIntervalS")]
    public int RescanIntervalS { get; set; } = 3600;

    [XmlAttribute("fsWatcherEnabled")]
    public bool FsWatcherEnabled { get; set; } = true;

    [XmlAttribute("fsWatcherDelayS")]
    public int FsWatcherDelayS { get; set; } = 10;

    [XmlAttribute("fsWatcherTimeoutS")]
    public int FsWatcherTimeoutS { get; set; } = 0;

    [XmlAttribute("ignorePerms")]
    public bool IgnorePerms { get; set; } = false;

    [XmlAttribute("autoNormalize")]
    public bool AutoNormalize { get; set; } = true;

    // === Elements ===
    [XmlElement("filesystemType")]
    public string FilesystemType { get; set; } = "basic";

    [XmlElement("device")]
    public List<ConfigXmlFolderDevice> Devices { get; set; } = new();

    [XmlElement("minDiskFree")]
    public ConfigXmlMinDiskFree MinDiskFree { get; set; } = new();

    [XmlElement("versioning")]
    public ConfigXmlVersioning Versioning { get; set; } = new();

    [XmlElement("copiers")]
    public int Copiers { get; set; } = 0;

    [XmlElement("pullerMaxPendingKiB")]
    public int PullerMaxPendingKiB { get; set; } = 0;

    [XmlElement("hashers")]
    public int Hashers { get; set; } = 0;

    [XmlElement("order")]
    public string Order { get; set; } = "random";

    [XmlElement("ignoreDelete")]
    public bool IgnoreDelete { get; set; } = false;

    [XmlElement("scanProgressIntervalS")]
    public int ScanProgressIntervalS { get; set; } = 0;

    [XmlElement("pullerPauseS")]
    public int PullerPauseS { get; set; } = 0;

    [XmlElement("pullerDelayS")]
    public int PullerDelayS { get; set; } = 1;

    [XmlElement("maxConflicts")]
    public int MaxConflicts { get; set; } = 10;

    [XmlElement("disableSparseFiles")]
    public bool DisableSparseFiles { get; set; } = false;

    [XmlElement("paused")]
    public bool Paused { get; set; } = false;

    [XmlElement("markerName")]
    public string MarkerName { get; set; } = ".stfolder";

    [XmlElement("copyOwnershipFromParent")]
    public bool CopyOwnershipFromParent { get; set; } = false;

    [XmlElement("modTimeWindowS")]
    public int ModTimeWindowS { get; set; } = 0;

    [XmlElement("maxConcurrentWrites")]
    public int MaxConcurrentWrites { get; set; } = 16;

    [XmlElement("disableFsync")]
    public bool DisableFsync { get; set; } = false;

    [XmlElement("blockPullOrder")]
    public string BlockPullOrder { get; set; } = "standard";

    [XmlElement("copyRangeMethod")]
    public string CopyRangeMethod { get; set; } = "standard";

    [XmlElement("caseSensitiveFS")]
    public bool CaseSensitiveFS { get; set; } = false;

    [XmlElement("junctionsAsDirs")]
    public bool JunctionsAsDirs { get; set; } = false;

    [XmlElement("syncOwnership")]
    public bool SyncOwnership { get; set; } = false;

    [XmlElement("sendOwnership")]
    public bool SendOwnership { get; set; } = false;

    [XmlElement("syncXattrs")]
    public bool SyncXattrs { get; set; } = false;

    [XmlElement("sendXattrs")]
    public bool SendXattrs { get; set; } = false;

    [XmlElement("xattrFilter")]
    public ConfigXmlXattrFilter XattrFilter { get; set; } = new();
}

/// <summary>
/// Device reference within a folder
/// </summary>
public class ConfigXmlFolderDevice
{
    [XmlAttribute("id")]
    public string Id { get; set; } = string.Empty;

    [XmlAttribute("introducedBy")]
    public string IntroducedBy { get; set; } = string.Empty;

    [XmlElement("encryptionPassword")]
    public string EncryptionPassword { get; set; } = string.Empty;
}

/// <summary>
/// Extended attributes filter configuration
/// </summary>
public class ConfigXmlXattrFilter
{
    [XmlElement("maxSingleEntrySize")]
    public int MaxSingleEntrySize { get; set; } = 1024;

    [XmlElement("maxTotalSize")]
    public int MaxTotalSize { get; set; } = 4096;
}

/// <summary>
/// Minimum disk free space configuration
/// </summary>
public class ConfigXmlMinDiskFree
{
    [XmlAttribute("unit")]
    public string Unit { get; set; } = "%";

    [XmlText]
    public double Value { get; set; } = 1;
}

/// <summary>
/// Versioning configuration
/// </summary>
public class ConfigXmlVersioning
{
    [XmlAttribute("type")]
    public string Type { get; set; } = string.Empty; // simple, staggered, external, trashcan

    [XmlElement("cleanupIntervalS")]
    public int CleanupIntervalS { get; set; } = 3600;

    [XmlElement("fsPath")]
    public string FsPath { get; set; } = string.Empty;

    [XmlElement("fsType")]
    public string FsType { get; set; } = "basic";

    [XmlElement("param")]
    public List<ConfigXmlParam> Params { get; set; } = new();
}

/// <summary>
/// Generic parameter element
/// </summary>
public class ConfigXmlParam
{
    [XmlAttribute("key")]
    public string Key { get; set; } = string.Empty;

    [XmlAttribute("val")]
    public string Val { get; set; } = string.Empty;
}

/// <summary>
/// Device configuration element
/// </summary>
public class ConfigXmlDevice
{
    [XmlAttribute("id")]
    public string Id { get; set; } = string.Empty;

    [XmlAttribute("name")]
    public string Name { get; set; } = string.Empty;

    [XmlAttribute("compression")]
    public string Compression { get; set; } = "metadata"; // always, metadata, never

    [XmlAttribute("introducer")]
    public bool Introducer { get; set; } = false;

    [XmlAttribute("skipIntroductionRemovals")]
    public bool SkipIntroductionRemovals { get; set; } = false;

    [XmlAttribute("introducedBy")]
    public string IntroducedBy { get; set; } = string.Empty;

    [XmlElement("address")]
    public List<string> Addresses { get; set; } = new() { "dynamic" };

    [XmlElement("paused")]
    public bool Paused { get; set; } = false;

    [XmlElement("autoAcceptFolders")]
    public bool AutoAcceptFolders { get; set; } = false;

    [XmlElement("maxSendKbps")]
    public int MaxSendKbps { get; set; } = 0;

    [XmlElement("maxRecvKbps")]
    public int MaxRecvKbps { get; set; } = 0;

    [XmlElement("maxRequestKiB")]
    public int MaxRequestKiB { get; set; } = 0;

    [XmlElement("untrusted")]
    public bool Untrusted { get; set; } = false;

    [XmlElement("remoteGUIPort")]
    public int RemoteGUIPort { get; set; } = 0;

    [XmlElement("numConnections")]
    public int NumConnections { get; set; } = 0;

    [XmlElement("ignoredFolder")]
    public List<ConfigXmlIgnoredFolder> IgnoredFolders { get; set; } = new();
}

/// <summary>
/// Ignored folder reference
/// </summary>
public class ConfigXmlIgnoredFolder
{
    [XmlAttribute("id")]
    public string Id { get; set; } = string.Empty;

    [XmlAttribute("label")]
    public string Label { get; set; } = string.Empty;

    [XmlAttribute("time")]
    public DateTime Time { get; set; }
}

/// <summary>
/// GUI configuration element
/// </summary>
public class ConfigXmlGui
{
    [XmlAttribute("enabled")]
    public bool Enabled { get; set; } = true;

    [XmlAttribute("tls")]
    public bool Tls { get; set; } = false;

    [XmlAttribute("debugging")]
    public bool Debugging { get; set; } = false;

    [XmlAttribute("sendBasicAuthPrompt")]
    public bool SendBasicAuthPrompt { get; set; } = false;

    [XmlElement("address")]
    public string Address { get; set; } = "127.0.0.1:8384";

    [XmlElement("user")]
    public string User { get; set; } = string.Empty;

    [XmlElement("password")]
    public string Password { get; set; } = string.Empty;

    [XmlElement("metricsWithoutAuth")]
    public bool MetricsWithoutAuth { get; set; } = false;

    [XmlElement("apikey")]
    public string ApiKey { get; set; } = string.Empty;

    [XmlElement("theme")]
    public string Theme { get; set; } = "default";

    [XmlElement("unackedNotificationID")]
    public List<string> UnackedNotificationIds { get; set; } = new();

    [XmlElement("insecureAdminAccess")]
    public bool InsecureAdminAccess { get; set; } = false;

    [XmlElement("insecureSkipHostcheck")]
    public bool InsecureSkipHostcheck { get; set; } = false;

    [XmlElement("insecureAllowFrameLoading")]
    public bool InsecureAllowFrameLoading { get; set; } = false;
}

/// <summary>
/// LDAP configuration element
/// </summary>
public class ConfigXmlLdap
{
    [XmlAttribute("address")]
    public string Address { get; set; } = string.Empty;

    [XmlAttribute("bindDN")]
    public string BindDN { get; set; } = string.Empty;

    [XmlAttribute("transport")]
    public string Transport { get; set; } = "plain";

    [XmlAttribute("insecureSkipVerify")]
    public bool InsecureSkipVerify { get; set; } = false;

    [XmlAttribute("searchBaseDN")]
    public string SearchBaseDN { get; set; } = string.Empty;

    [XmlAttribute("searchFilter")]
    public string SearchFilter { get; set; } = string.Empty;
}

/// <summary>
/// Options configuration element
/// </summary>
public class ConfigXmlOptions
{
    [XmlElement("listenAddress")]
    public List<string> ListenAddresses { get; set; } = new() { "default" };

    [XmlElement("globalAnnounceServer")]
    public List<string> GlobalAnnounceServers { get; set; } = new() { "default" };

    [XmlElement("globalAnnounceEnabled")]
    public bool GlobalAnnounceEnabled { get; set; } = true;

    [XmlElement("localAnnounceEnabled")]
    public bool LocalAnnounceEnabled { get; set; } = true;

    [XmlElement("localAnnouncePort")]
    public int LocalAnnouncePort { get; set; } = 21027;

    [XmlElement("localAnnounceMCAddr")]
    public string LocalAnnounceMCAddr { get; set; } = "[ff12::8384]:21027";

    [XmlElement("maxSendKbps")]
    public int MaxSendKbps { get; set; } = 0;

    [XmlElement("maxRecvKbps")]
    public int MaxRecvKbps { get; set; } = 0;

    [XmlElement("reconnectionIntervalS")]
    public int ReconnectionIntervalS { get; set; } = 60;

    [XmlElement("relaysEnabled")]
    public bool RelaysEnabled { get; set; } = true;

    [XmlElement("relayReconnectIntervalM")]
    public int RelayReconnectIntervalM { get; set; } = 10;

    [XmlElement("startBrowser")]
    public bool StartBrowser { get; set; } = true;

    [XmlElement("natEnabled")]
    public bool NatEnabled { get; set; } = true;

    [XmlElement("natLeaseMinutes")]
    public int NatLeaseMinutes { get; set; } = 60;

    [XmlElement("natRenewalMinutes")]
    public int NatRenewalMinutes { get; set; } = 30;

    [XmlElement("natTimeoutSeconds")]
    public int NatTimeoutSeconds { get; set; } = 10;

    [XmlElement("urAccepted")]
    public int UrAccepted { get; set; } = -1;

    [XmlElement("urSeen")]
    public int UrSeen { get; set; } = 3;

    [XmlElement("urUniqueID")]
    public string UrUniqueId { get; set; } = string.Empty;

    [XmlElement("urURL")]
    public string UrURL { get; set; } = "https://data.syncthing.net/newdata";

    [XmlElement("urPostInsecurely")]
    public bool UrPostInsecurely { get; set; } = false;

    [XmlElement("urInitialDelayS")]
    public int UrInitialDelayS { get; set; } = 1800;

    [XmlElement("autoUpgradeIntervalH")]
    public int AutoUpgradeIntervalH { get; set; } = 12;

    [XmlElement("upgradeToPreReleases")]
    public bool UpgradeToPreReleases { get; set; } = false;

    [XmlElement("keepTemporariesH")]
    public int KeepTemporariesH { get; set; } = 24;

    [XmlElement("cacheIgnoredFiles")]
    public bool CacheIgnoredFiles { get; set; } = false;

    [XmlElement("progressUpdateIntervalS")]
    public int ProgressUpdateIntervalS { get; set; } = 5;

    [XmlElement("limitBandwidthInLan")]
    public bool LimitBandwidthInLan { get; set; } = false;

    [XmlElement("minHomeDiskFree")]
    public ConfigXmlMinDiskFree MinHomeDiskFree { get; set; } = new();

    [XmlElement("releasesURL")]
    public string ReleasesURL { get; set; } = "https://upgrades.syncthing.net/meta.json";

    [XmlElement("overwriteRemoteDeviceNamesOnConnect")]
    public bool OverwriteRemoteDeviceNamesOnConnect { get; set; } = false;

    [XmlElement("tempIndexMinBlocks")]
    public int TempIndexMinBlocks { get; set; } = 10;

    [XmlElement("trafficClass")]
    public int TrafficClass { get; set; } = 0;

    [XmlElement("setLowPriority")]
    public bool SetLowPriority { get; set; } = false;

    [XmlElement("maxFolderConcurrency")]
    public int MaxFolderConcurrency { get; set; } = 0;

    [XmlElement("crashReportingURL")]
    public string CrashReportingURL { get; set; } = "https://crash.syncthing.net/newcrash";

    [XmlElement("crashReportingEnabled")]
    public bool CrashReportingEnabled { get; set; } = true;

    [XmlElement("stunKeepaliveStartS")]
    public int StunKeepaliveStartS { get; set; } = 180;

    [XmlElement("stunKeepaliveMinS")]
    public int StunKeepaliveMinS { get; set; } = 20;

    [XmlElement("stunServer")]
    public List<string> StunServers { get; set; } = new() { "default" };

    [XmlElement("maxConcurrentIncomingRequestKiB")]
    public int MaxConcurrentIncomingRequestKiB { get; set; } = 0;

    [XmlElement("announceLANAddresses")]
    public bool AnnounceLANAddresses { get; set; } = true;

    [XmlElement("sendFullIndexOnUpgrade")]
    public bool SendFullIndexOnUpgrade { get; set; } = false;

    [XmlElement("auditEnabled")]
    public bool AuditEnabled { get; set; } = false;

    [XmlElement("auditFile")]
    public string AuditFile { get; set; } = string.Empty;

    [XmlElement("connectionLimitEnough")]
    public int ConnectionLimitEnough { get; set; } = 0;

    [XmlElement("connectionLimitMax")]
    public int ConnectionLimitMax { get; set; } = 0;

    [XmlElement("connectionPriorityTcpLan")]
    public int ConnectionPriorityTcpLan { get; set; } = 10;

    [XmlElement("connectionPriorityQuicLan")]
    public int ConnectionPriorityQuicLan { get; set; } = 20;

    [XmlElement("connectionPriorityTcpWan")]
    public int ConnectionPriorityTcpWan { get; set; } = 30;

    [XmlElement("connectionPriorityQuicWan")]
    public int ConnectionPriorityQuicWan { get; set; } = 40;

    [XmlElement("connectionPriorityRelay")]
    public int ConnectionPriorityRelay { get; set; } = 50;

    [XmlElement("connectionPriorityUpgradeThreshold")]
    public int ConnectionPriorityUpgradeThreshold { get; set; } = 0;

    [XmlElement("unackedNotificationID")]
    public List<string> UnackedNotificationIds { get; set; } = new();

    [XmlElement("alwaysLocalNets")]
    public List<string> AlwaysLocalNets { get; set; } = new();

    [XmlElement("featureFlags")]
    public List<string> FeatureFlags { get; set; } = new();

    [XmlElement("insecureAllowOldTLSVersions")]
    public bool InsecureAllowOldTLSVersions { get; set; } = false;

    [XmlElement("databaseTuning")]
    public string DatabaseTuning { get; set; } = "auto";

    [XmlElement("maxConcurrentScans")]
    public int MaxConcurrentScans { get; set; } = 0;
}

/// <summary>
/// Remote ignored devices configuration
/// </summary>
public class ConfigXmlRemoteIgnoredDevices
{
    [XmlElement("device")]
    public List<ConfigXmlIgnoredDevice> Devices { get; set; } = new();
}

/// <summary>
/// Ignored device reference
/// </summary>
public class ConfigXmlIgnoredDevice
{
    [XmlAttribute("id")]
    public string Id { get; set; } = string.Empty;

    [XmlAttribute("name")]
    public string Name { get; set; } = string.Empty;

    [XmlAttribute("time")]
    public DateTime Time { get; set; }

    [XmlAttribute("address")]
    public string Address { get; set; } = string.Empty;
}

/// <summary>
/// Default configurations
/// </summary>
public class ConfigXmlDefaults
{
    [XmlElement("folder")]
    public ConfigXmlFolder? Folder { get; set; }

    [XmlElement("device")]
    public ConfigXmlDevice? Device { get; set; }

    [XmlElement("ignores")]
    public ConfigXmlIgnores? Ignores { get; set; }
}

/// <summary>
/// Default ignore patterns
/// </summary>
public class ConfigXmlIgnores
{
    [XmlElement("line")]
    public List<string> Lines { get; set; } = new();
}
