using System.Text.Json.Serialization;

namespace CreatioHelper.Domain.Entities;

public class IisSiteInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
    
    [JsonPropertyName("state")]
    public string State { get; set; } = "";
    
    [JsonPropertyName("Port")]
    public string Port { get; set; } = "";
}