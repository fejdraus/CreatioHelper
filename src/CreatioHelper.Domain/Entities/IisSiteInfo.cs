using System.Text.Json.Serialization;

namespace CreatioHelper.Domain.Entities;

public class IisSiteInfo
{
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("state")]
    public string State { get; set; } = "";

    [JsonPropertyName("Port")]
    public string Port { get; set; } = "";

    public string Path { get; set; } = "";

    public string PoolName { get; set; } = "";

    public Version Version { get; set; } = new();

    public override string ToString() => Name;
}