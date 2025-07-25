using System.Text.Json.Serialization;

namespace CreatioHelper.Domain.Entities;

public class IisAppPoolInfo
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";
    
    [JsonPropertyName("Value")]
    public string Value { get; set; } = "";
}