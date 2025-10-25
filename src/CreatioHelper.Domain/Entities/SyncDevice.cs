using CreatioHelper.Domain.Common;

namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Represents a sync device in the network (based on Syncthing DeviceConfiguration)
/// </summary>
public class SyncDevice : AggregateRoot
{
    public string DeviceId { get; private set; } = string.Empty;
    public string DeviceName { get; private set; } = string.Empty;
    public List<string> Addresses { get; set; } = new();
    public string Compression { get; set; } = "metadata";
    public bool Introducer { get; set; }
    public bool SkipIntroductionRemovals { get; set; }
    public string IntroducedBy { get; private set; } = string.Empty;
    public bool Paused { get; set; }
    public List<string> AllowedNetworks { get; set; } = new();
    public bool AutoAcceptFolders { get; set; }
    public int MaxSendKbps { get; set; }
    public int MaxRecvKbps { get; set; }
    public List<string> IgnoredFolders { get; set; } = new();
    public List<string> PendingFolders { get; set; } = new();
    public int MaxRequestKib { get; set; }
    public int MaxRequestKiB { get; set; } // Compatibility property for SyncthingConfigLoader
    public bool Untrusted { get; set; }
    public int RemoteGUIPort { get; private set; }
    public int NumConnections { get; private set; } = 3;
    public string CertificateName { get; private set; } = string.Empty;
    public string CertificateFingerprint { get; set; } = string.Empty;
    
    // Runtime properties not in Syncthing DeviceConfiguration
    public DateTime? LastSeen { get; set; }
    public DateTime? LastActivity { get; set; }
    public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;
    public string? ConnectionType { get; set; } // TCP, QUIC, Relay etc.
    public string? LastAddress { get; set; } // Last known connection address
    public DateTime? LastConnected { get; set; } // Last successful connection
    
    // Compatibility properties for old code
    public bool IsConnected => Status == DeviceStatus.Connected;
    public bool IsPaused => Paused;

    private SyncDevice() { } // For EF Core

    public SyncDevice(string deviceId, string deviceName)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        LastSeen = DateTime.UtcNow;
    }

    // Public constructor for database mapping
    public SyncDevice(string deviceId, string deviceName, string compression, bool introducer, 
        bool skipIntroductionRemovals, string introducedBy, bool paused, bool autoAcceptFolders,
        int maxSendKbps, int maxRecvKbps, int maxRequestKib, bool untrusted, int remoteGuiPort, 
        int numConnections, string certificateName)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;
        Compression = compression;
        Introducer = introducer;
        SkipIntroductionRemovals = skipIntroductionRemovals;
        IntroducedBy = introducedBy;
        Paused = paused;
        AutoAcceptFolders = autoAcceptFolders;
        MaxSendKbps = maxSendKbps;
        MaxRecvKbps = maxRecvKbps;
        MaxRequestKib = maxRequestKib;
        MaxRequestKiB = maxRequestKib; // Sync both properties
        Untrusted = untrusted;
        RemoteGUIPort = remoteGuiPort;
        NumConnections = numConnections;
        CertificateName = certificateName;
        LastSeen = DateTime.UtcNow;
    }

    public void UpdateConnection(bool isConnected, string? address = null, string? connectionType = null)
    {
        if (address != null && !Addresses.Contains(address))
        {
            Addresses.Add(address);
        }
        
        if (isConnected)
        {
            LastSeen = DateTime.UtcNow;
            LastActivity = DateTime.UtcNow;
            LastConnected = DateTime.UtcNow;
            Status = DeviceStatus.Connected;
            
            if (address != null)
                LastAddress = address;
            if (connectionType != null)
                ConnectionType = connectionType;
        }
        else
        {
            Status = DeviceStatus.Disconnected;
        }
    }

    public void AddAddress(string address)
    {
        if (!Addresses.Contains(address))
        {
            Addresses.Add(address);
        }
    }

    public void UpdateAddresses(List<string> addresses)
    {
        Addresses.Clear();
        Addresses.AddRange(addresses);
    }

    public void SetPaused(bool paused)
    {
        Paused = paused;
        Status = paused ? DeviceStatus.Paused : (Status == DeviceStatus.Connected ? DeviceStatus.Connected : DeviceStatus.Disconnected);
    }

    public void UpdateName(string deviceName)
    {
        DeviceName = deviceName;
    }

    public void SetCompression(string compression)
    {
        Compression = compression;
    }

    public void SetIntroducer(bool introducer)
    {
        Introducer = introducer;
    }

    public void SetAutoAcceptFolders(bool autoAccept)
    {
        AutoAcceptFolders = autoAccept;
    }

    public void SetBandwidthLimits(int maxSendKbps, int maxRecvKbps)
    {
        MaxSendKbps = maxSendKbps;
        MaxRecvKbps = maxRecvKbps;
    }

    public override bool IsValid()
    {
        return !string.IsNullOrEmpty(DeviceId) && 
               !string.IsNullOrEmpty(DeviceName);
    }

    public override IEnumerable<string> GetBrokenRules()
    {
        var brokenRules = new List<string>();

        if (string.IsNullOrEmpty(DeviceId))
            brokenRules.Add("Device ID cannot be empty");

        if (string.IsNullOrEmpty(DeviceName))
            brokenRules.Add("Device name cannot be empty");

        return brokenRules;
    }
}

public enum DeviceStatus
{
    Disconnected,
    Connected,
    Paused,
    Error
}