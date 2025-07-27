namespace CreatioHelper.Domain.Entities;

public class WebSiteInfo
{
    public string Name { get; set; } = "";           // Display name
    public string Type { get; set; } = "";           // IIS, WindowsService, Systemd
    public string ServiceName { get; set; } = "";    // Actual service/site name
    public bool AutoDiscovered { get; set; }         // true for IIS, false for manual
    public Dictionary<string, string> Properties { get; set; } = new();
    public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Unknown";
}