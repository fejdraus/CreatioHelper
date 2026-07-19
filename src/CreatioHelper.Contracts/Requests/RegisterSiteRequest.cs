using System.ComponentModel.DataAnnotations;

namespace CreatioHelper.Contracts.Requests;

public class RegisterSiteRequest
{
    [Required]
    [MaxLength(100)]
    public string DisplayName { get; set; } = "";

    [Required]
    [AllowedValues("IIS", "Service", "WindowsService", "Systemd", "Launchd")]
    public string Type { get; set; } = "";

    [Required]
    [MaxLength(100)]
    public string ServiceName { get; set; } = "";

    public List<string>? FolderIds { get; set; }

    public Dictionary<string, string>? Properties { get; set; }
}
