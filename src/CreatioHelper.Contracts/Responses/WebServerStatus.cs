namespace CreatioHelper.Contracts.Responses;

public class WebServerStatus
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public string Type { get; set; } = "";
    public string Port { get; set; } = "";
    public bool IsRunning { get; set; }
    public DateTime LastChecked { get; set; }
    public string? ErrorMessage { get; set; }
    public Dictionary<string, object> Properties { get; set; } = new();
}
