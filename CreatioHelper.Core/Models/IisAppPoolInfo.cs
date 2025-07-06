using System.Text.Json.Serialization;

namespace CreatioHelper.Core.Models;

public class IisAppPoolInfo
{
    [JsonPropertyName("Name")]
    public string Name { get; set; } = "";
    
    [JsonPropertyName("Value")]
    public string Value { get; set; } = "";
}