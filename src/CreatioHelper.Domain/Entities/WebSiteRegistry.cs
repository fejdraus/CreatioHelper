using CreatioHelper.Domain.Enums;

namespace CreatioHelper.Domain.Entities;

public class WebSiteRegistry
{
    public List<WebSiteInfo> Sites { get; set; } = new();
    public Dictionary<string, WebServerKind> WebServerTypeOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public string Version { get; set; } = "1.0";
}