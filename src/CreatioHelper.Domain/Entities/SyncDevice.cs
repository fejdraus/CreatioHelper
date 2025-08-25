using CreatioHelper.Domain.Common;

namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Represents a sync device in the network (based on Syncthing device concept)
/// </summary>
public class SyncDevice : AggregateRoot
{
    public string DeviceId { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Address { get; private set; }
    public bool IsConnected { get; private set; }
    public DateTime LastSeen { get; private set; }
    public string CertificateFingerprint { get; private set; } = string.Empty;
    public bool AutoConnect { get; private set; } = true;
    public bool IsPaused { get; private set; }
    public List<string> Addresses { get; private set; } = new();
    public DeviceStatus Status { get; private set; } = DeviceStatus.Disconnected;

    private SyncDevice() { } // For EF Core

    public SyncDevice(string deviceId, string name, string? certificateFingerprint = null)
    {
        DeviceId = deviceId;
        Name = name;
        CertificateFingerprint = certificateFingerprint ?? string.Empty;
        LastSeen = DateTime.UtcNow;
    }

    public void UpdateConnection(bool isConnected, string? address = null)
    {
        IsConnected = isConnected;
        if (address != null)
        {
            Address = address;
        }
        
        if (isConnected)
        {
            LastSeen = DateTime.UtcNow;
            Status = DeviceStatus.Connected;
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

    public void SetPaused(bool paused)
    {
        IsPaused = paused;
        Status = paused ? DeviceStatus.Paused : (IsConnected ? DeviceStatus.Connected : DeviceStatus.Disconnected);
    }

    public void UpdateName(string name)
    {
        Name = name;
    }

    public override bool IsValid()
    {
        return !string.IsNullOrEmpty(DeviceId) && 
               !string.IsNullOrEmpty(Name) && 
               !string.IsNullOrEmpty(CertificateFingerprint);
    }

    public override IEnumerable<string> GetBrokenRules()
    {
        var brokenRules = new List<string>();

        if (string.IsNullOrEmpty(DeviceId))
            brokenRules.Add("Device ID cannot be empty");

        if (string.IsNullOrEmpty(Name))
            brokenRules.Add("Device name cannot be empty");

        if (string.IsNullOrEmpty(CertificateFingerprint))
            brokenRules.Add("Certificate fingerprint cannot be empty");

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