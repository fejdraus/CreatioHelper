using System.ComponentModel.DataAnnotations;

namespace CreatioHelper.Contracts.Requests;

public class AddDeviceRequest
{
    [Required]
    public string DeviceId { get; set; } = string.Empty;
    
    [Required]
    public string Name { get; set; } = string.Empty;
    
    public string? CertificateFingerprint { get; set; }
    
    public List<string>? Addresses { get; set; }
}

public class AddFolderRequest
{
    [Required]
    public string FolderId { get; set; } = string.Empty;
    
    [Required]
    public string Label { get; set; } = string.Empty;
    
    [Required]
    public string Path { get; set; } = string.Empty;
    
    public string Type { get; set; } = "SendReceive";
}

public class ShareFolderRequest
{
    [Required]
    public string DeviceId { get; set; } = string.Empty;
}

public class SyncConfigurationRequest
{
    public string? DeviceName { get; set; }
    public List<string>? ListenAddresses { get; set; }
    public bool? GlobalAnnounceEnabled { get; set; }
    public bool? LocalAnnounceEnabled { get; set; }
    public List<string>? GlobalAnnounceServers { get; set; }
    public bool? RelaysEnabled { get; set; }
    public int? MaxSendKbps { get; set; }
    public int? MaxRecvKbps { get; set; }
    public bool? AutoUpgradeEnabled { get; set; }
}