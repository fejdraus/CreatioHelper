using System.ComponentModel.DataAnnotations;

namespace CreatioHelper.Agent.Models;

public class RegisterSiteRequest
{
    [Required]
    [MaxLength(100)]
    public string DisplayName { get; set; } = "";
    
    [Required]
    [AllowedValues("IIS", "WindowsService", "Systemd", "Launchd")]
    public string Type { get; set; } = "";
    
    [Required]
    [MaxLength(100)]
    public string ServiceName { get; set; } = "";
    
    public Dictionary<string, string>? Properties { get; set; }
}