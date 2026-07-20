using System.Text.Json.Serialization;
using CreatioHelper.Domain.Enums;

namespace CreatioHelper.Domain.Entities;

public class WebSiteInfo
{
    public string Name { get; set; } = "";           // Display name
    public string Type { get; set; } = "";           // IIS, WindowsService, Systemd
    public WebServerKind WebServerType { get; set; } = WebServerKind.Auto;
    public string ServiceName { get; set; } = "";    // Actual service/site name
    public string AppPool { get; set; } = "";        // IIS application pool name
    public bool AutoDiscovered { get; set; }         // true for IIS, false for manual
    public List<string> FolderIds { get; set; } = new();
    public Dictionary<string, string> Properties { get; set; } = new();
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Unknown";

    [JsonIgnore]
    public WebServerKind EffectiveKind =>
        WebServerType != WebServerKind.Auto
            ? WebServerType
            : string.Equals(Type, "IIS", StringComparison.OrdinalIgnoreCase)
                ? WebServerKind.Iis
                : WebServerKind.Service;
}