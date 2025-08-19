using CreatioHelper.Domain.Common;

namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Sync configuration settings (based on Syncthing configuration)
/// </summary>
public class SyncConfiguration : AggregateRoot
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public int Port { get; set; } = 22000;
    public List<string> ListenAddresses { get; private set; } = new() { "tcp://0.0.0.0:22000" };
    public bool GlobalAnnounceEnabled { get; private set; } = true;
    public bool LocalAnnounceEnabled { get; private set; } = true;
    public int LocalAnnouncePort { get; private set; } = 21027;
    public List<string> GlobalAnnounceServers { get; private set; } = new()
    {
        "https://discovery.syncthing.net/v2/",
        "https://discovery-v4.syncthing.net/v2/",
        "https://discovery-v6.syncthing.net/v2/"
    };
    public bool RelaysEnabled { get; private set; } = true;
    public List<string> RelayServers { get; private set; } = new() { "dynamic+https://relays.syncthing.net/endpoint" };
    public bool NatEnabled { get; private set; } = true;
    public bool UpnpEnabled { get; private set; } = true;
    public int MaxSendKbps { get; private set; } = 0; // 0 = unlimited
    public int MaxRecvKbps { get; private set; } = 0; // 0 = unlimited
    public int ReconnectionIntervalS { get; private set; } = 60;
    public bool RelaysReconnectIntervalM { get; private set; } = true;
    public int StartBrowser { get; private set; } = 1; // 0 = never, 1 = default browser
    public bool UPnPEnabled { get; private set; } = true;
    public int UPnPLeaseMinutes { get; private set; } = 60;
    public int UPnPRenewalMinutes { get; private set; } = 30;
    public int UPnPTimeoutSeconds { get; private set; } = 10;
    public bool CrashReportingEnabled { get; private set; } = true;
    public bool UsageReportingAccepted { get; private set; } = false;
    public bool AutoUpgradeEnabled { get; private set; } = true;
    public int AutoUpgradeIntervalH { get; private set; } = 12;
    public string UpgradeToPreReleases { get; private set; } = "stable";
    public int KeepTemporariesH { get; private set; } = 24;
    public bool CacheIgnoredFiles { get; private set; } = false;
    public int ProgressUpdateIntervalS { get; private set; } = 5;
    public int LimitBandwidthInLan { get; private set; } = 0;
    public int MinHomeDiskFree { get; private set; } = 1; // 1%
    public string DefaultFolderPath { get; private set; } = "~/Sync";
    public bool SetLowPriority { get; private set; } = true;
    public int DatabaseTuning { get; private set; } = 0; // 0 = auto, 1 = small, 2 = large
    public int RawUsageReportingUniqueID { get; private set; } = 0;
    
    // Delta sync configuration
    public bool EnableDeltaSync { get; private set; } = true;
    public bool EnableAdvancedDeltaSync { get; private set; } = false; // Rolling hash, more CPU intensive
    public int DeltaSyncMaxWindowSizeMB { get; private set; } = 16; // Maximum window for rolling hash

    public SyncConfiguration() { } // For EF Core and configuration binding

    public SyncConfiguration(string deviceId, string deviceName)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
    }

    public void SetPort(int port)
    {
        Port = port;
        SetListenAddresses(new List<string> { $"tcp://0.0.0.0:{port}" });
    }

    public void UpdateDeviceName(string name)
    {
        DeviceName = name;
    }

    public void SetListenAddresses(List<string> addresses)
    {
        ListenAddresses = addresses;
    }

    public void SetGlobalAnnounceEnabled(bool enabled)
    {
        GlobalAnnounceEnabled = enabled;
    }

    public void SetLocalAnnounceEnabled(bool enabled)
    {
        LocalAnnounceEnabled = enabled;
    }

    public void SetGlobalAnnounceServers(List<string> servers)
    {
        GlobalAnnounceServers = servers;
    }

    public void SetRelaysEnabled(bool enabled)
    {
        RelaysEnabled = enabled;
    }

    public void SetBandwidthLimits(int maxSendKbps, int maxRecvKbps)
    {
        MaxSendKbps = maxSendKbps;
        MaxRecvKbps = maxRecvKbps;
    }

    public void SetAutoUpgrade(bool enabled, int intervalHours = 12, string preReleases = "stable")
    {
        AutoUpgradeEnabled = enabled;
        AutoUpgradeIntervalH = intervalHours;
        UpgradeToPreReleases = preReleases;
    }

    public void SetUsageReporting(bool accepted)
    {
        UsageReportingAccepted = accepted;
    }

    public void SetDefaultFolderPath(string path)
    {
        DefaultFolderPath = path;
    }

    public void SetDeltaSync(bool enabled, bool advancedEnabled = false, int maxWindowSizeMB = 16)
    {
        EnableDeltaSync = enabled;
        EnableAdvancedDeltaSync = advancedEnabled;
        DeltaSyncMaxWindowSizeMB = maxWindowSizeMB;
    }

    public override bool IsValid()
    {
        return !string.IsNullOrEmpty(DeviceId) && 
               !string.IsNullOrEmpty(DeviceName) &&
               LocalAnnouncePort > 0 &&
               ReconnectionIntervalS > 0 &&
               AutoUpgradeIntervalH > 0 &&
               KeepTemporariesH >= 0 &&
               ProgressUpdateIntervalS > 0 &&
               MinHomeDiskFree >= 0 &&
               MaxSendKbps >= 0 &&
               MaxRecvKbps >= 0;
    }

    public override IEnumerable<string> GetBrokenRules()
    {
        var brokenRules = new List<string>();

        if (string.IsNullOrEmpty(DeviceId))
            brokenRules.Add("Device ID cannot be empty");

        if (string.IsNullOrEmpty(DeviceName))
            brokenRules.Add("Device name cannot be empty");

        if (LocalAnnouncePort <= 0)
            brokenRules.Add("Local announce port must be positive");

        if (ReconnectionIntervalS <= 0)
            brokenRules.Add("Reconnection interval must be positive");

        if (AutoUpgradeIntervalH <= 0)
            brokenRules.Add("Auto upgrade interval must be positive");

        if (KeepTemporariesH < 0)
            brokenRules.Add("Keep temporaries hours cannot be negative");

        if (ProgressUpdateIntervalS <= 0)
            brokenRules.Add("Progress update interval must be positive");

        if (MinHomeDiskFree < 0)
            brokenRules.Add("Min home disk free cannot be negative");

        if (MaxSendKbps < 0)
            brokenRules.Add("Max send speed cannot be negative");

        if (MaxRecvKbps < 0)
            brokenRules.Add("Max receive speed cannot be negative");

        return brokenRules;
    }
}