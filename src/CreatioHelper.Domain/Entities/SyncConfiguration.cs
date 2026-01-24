using CreatioHelper.Domain.Common;

namespace CreatioHelper.Domain.Entities;

/// <summary>
/// NAT traversal configuration for UPnP/PMP/STUN
/// </summary>
public class NatTraversalConfiguration
{
    public bool Enabled { get; set; } = true;
    public bool UpnpEnabled { get; set; } = true;
    public bool PmpEnabled { get; set; } = true;
    public bool StunEnabled { get; set; } = true;
    public int DiscoveryIntervalMinutes { get; set; } = 15;
    public int RenewalIntervalMinutes { get; set; } = 30;
    public int LeaseTimeMinutes { get; set; } = 60;
    public int TimeoutSeconds { get; set; } = 10;
    public List<string> PreferredExternalPorts { get; set; } = new();
    public bool AllowPortMapping { get; set; } = true;
    public bool AllowPortForwarding { get; set; } = true;

    /// <summary>
    /// STUN keepalive interval in seconds (default: 10, Syncthing compatible).
    /// </summary>
    public int? StunKeepAliveSeconds { get; set; } = 10;

    /// <summary>
    /// List of STUN servers to use for external IP discovery.
    /// </summary>
    public List<string> StunServers { get; set; } = new()
    {
        "stun.syncthing.net:3478",
        "stun.l.google.com:19302",
        "stun1.l.google.com:19302"
    };
}

/// <summary>
/// Sync configuration settings (based on Syncthing configuration)
/// </summary>
public class SyncConfiguration : AggregateRoot
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public int Port { get; set; } = 22000;
    public List<string> ListenAddresses { get; private set; } = new() { "tcp://0.0.0.0:22000" };
    
    /// <summary>
    /// Bandwidth management configuration
    /// </summary>
    public BandwidthConfiguration BandwidthSettings { get; set; } = new();
    
    /// <summary>
    /// Traffic shaping and priority configuration
    /// </summary>
    public TrafficShapingConfiguration TrafficShaping { get; set; } = new();
    public bool GlobalAnnounceEnabled { get; set; } = true;
    public bool LocalAnnounceEnabled { get; set; } = true;
    public int LocalAnnouncePort { get; set; } = 21027;
    public int DiscoveryPort => LocalAnnouncePort;
    public List<string> GlobalAnnounceServers { get; private set; } = new()
    {
        "https://discovery.syncthing.net/v2/",
        "https://discovery-v4.syncthing.net/v2/",
        "https://discovery-v6.syncthing.net/v2/"
    };
    public bool RelaysEnabled { get; set; } = true;
    public List<string> RelayServers { get; set; } = new() { "dynamic+https://relays.syncthing.net/endpoint" };
    public bool NatEnabled { get; set; } = true;
    public bool UpnpEnabled { get; set; } = true;
    public int MaxSendKbps { get; set; } = 0; // 0 = unlimited
    public int MaxRecvKbps { get; set; } = 0; // 0 = unlimited
    public int ReconnectionIntervalSeconds { get; set; } = 60;
    public int RelayReconnectIntervalMinutes { get; set; } = 10;
    public bool StartBrowser { get; set; } = true;
    public int NatLeaseMinutes { get; set; } = 60;
    public int NatRenewalMinutes { get; set; } = 30;
    public int NatTimeoutSeconds { get; set; } = 10;
    public bool CrashReportingEnabled { get; private set; } = false; // Telemetry disabled
    public bool UsageReportingAccepted { get; private set; } = false;
    public bool AutoUpgradeEnabled { get; set; } = true;
    public int AutoUpgradeIntervalHours { get; set; } = 12;
    public bool UpgradeToPreReleases { get; set; } = false;
    public int KeepTemporariesHours { get; set; } = 24;
    public bool CacheIgnoredFiles { get; set; } = false;
    public int ProgressUpdateIntervalSeconds { get; set; } = 5;
    public bool LimitBandwidthInLan { get; set; } = false;
    public int MinHomeDiskFree { get; private set; } = 1; // 1%
    public string DefaultFolderPath { get; set; } = "~/Sync";
    public bool SetLowPriority { get; set; } = true;
    public int MaxFolderConcurrency { get; set; } = 0; // 0 = unlimited
    public int DatabaseTuning { get; private set; } = 0; // 0 = auto, 1 = small, 2 = large
    public int RawUsageReportingUniqueID { get; private set; } = 0;
    
    // Delta sync configuration
    public bool EnableDeltaSync { get; private set; } = true;
    public bool EnableAdvancedDeltaSync { get; private set; } = false; // Rolling hash, more CPU intensive
    public int DeltaSyncMaxWindowSizeMB { get; private set; } = 16; // Maximum window for rolling hash
    
    // Block-level deduplication configuration
    public bool CompressionEnabled { get; private set; } = true;
    public bool EncryptionEnabled { get; private set; } = false;
    public int MaxConcurrentTransfers { get; private set; } = 8;
    
    // NAT traversal configuration
    public NatTraversalConfiguration? NatTraversal { get; private set; }
    
    // GUI configuration properties for Syncthing compatibility
    public bool GuiEnabled { get; set; } = true;
    public string GuiAddress { get; set; } = "127.0.0.1:8384";
    public bool GuiTls { get; set; } = false;
    public string GuiApiKey { get; set; } = "syncthing-compatible-key";
    public string GuiUser { get; set; } = string.Empty;
    public string GuiPassword { get; set; } = string.Empty;
    
    // Extended timing properties - these are already defined above, removed duplicates

    public SyncConfiguration() { } // For EF Core and configuration binding

    public SyncConfiguration(string deviceId, string deviceName)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        NatTraversal = new NatTraversalConfiguration();
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

    public void SetDiscoveryPort(int discoveryPort)
    {
        LocalAnnouncePort = discoveryPort;
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

    public void SetAutoUpgrade(bool enabled, int intervalHours = 12, bool preReleases = false)
    {
        AutoUpgradeEnabled = enabled;
        AutoUpgradeIntervalHours = intervalHours;
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
    
    public void SetDeduplicationOptions(bool compressionEnabled = true, bool encryptionEnabled = false, int maxConcurrentTransfers = 8)
    {
        CompressionEnabled = compressionEnabled;
        EncryptionEnabled = encryptionEnabled;
        MaxConcurrentTransfers = maxConcurrentTransfers;
    }
    
    public void SetNatTraversal(NatTraversalConfiguration? natConfig)
    {
        NatTraversal = natConfig;
    }
    
    public void EnableNatTraversal(bool enabled = true, bool upnpEnabled = true, bool pmpEnabled = true)
    {
        NatTraversal ??= new NatTraversalConfiguration();
        NatTraversal.Enabled = enabled;
        NatTraversal.UpnpEnabled = upnpEnabled;
        NatTraversal.PmpEnabled = pmpEnabled;
    }
    
    // Methods for new properties
    public void AddDevice(SyncDevice device)
    {
        // Implementation would add device to collection
        // For now, just a placeholder
    }
    
    public void AddFolder(SyncFolder folder)
    {
        // Implementation would add folder to collection
        // For now, just a placeholder
    }
    
    public void SetGuiConfiguration(bool enabled = true, string address = "127.0.0.1:8384", bool tls = false, string apiKey = "syncthing-compatible-key", string user = "", string password = "")
    {
        GuiEnabled = enabled;
        GuiAddress = address;
        GuiTls = tls;
        GuiApiKey = apiKey;
        GuiUser = user;
        GuiPassword = password;
    }

    // Additional methods for SyncthingConfigLoader compatibility
    public List<SyncDevice> GetDevices()
    {
        // Return empty list for now - would need actual device storage
        return new List<SyncDevice>();
    }

    public List<SyncFolder> GetFolders()
    {
        // Return empty list for now - would need actual folder storage
        return new List<SyncFolder>();
    }

    public override bool IsValid()
    {
        return !string.IsNullOrEmpty(DeviceId) && 
               !string.IsNullOrEmpty(DeviceName) &&
               LocalAnnouncePort > 0 &&
               ReconnectionIntervalSeconds > 0 &&
               AutoUpgradeIntervalHours > 0 &&
               KeepTemporariesHours >= 0 &&
               ProgressUpdateIntervalSeconds > 0 &&
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

        if (ReconnectionIntervalSeconds <= 0)
            brokenRules.Add("Reconnection interval must be positive");

        if (AutoUpgradeIntervalHours <= 0)
            brokenRules.Add("Auto upgrade interval must be positive");

        if (KeepTemporariesHours < 0)
            brokenRules.Add("Keep temporaries hours cannot be negative");

        if (ProgressUpdateIntervalSeconds <= 0)
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