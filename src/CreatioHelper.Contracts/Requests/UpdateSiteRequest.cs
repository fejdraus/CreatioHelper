using System.ComponentModel.DataAnnotations;

namespace CreatioHelper.Contracts.Requests;

public class UpdateSiteRequest
{
    [Required]
    [AllowedValues("IIS", "WindowsService", "Systemd", "Launchd")]
    public string Type { get; set; } = "";

    [Required]
    [MaxLength(100)]
    public string ServiceName { get; set; } = "";

    public Dictionary<string, string>? Properties { get; set; }
}
