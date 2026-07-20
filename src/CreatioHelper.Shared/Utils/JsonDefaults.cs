using System.Text.Json;

namespace CreatioHelper.Shared.Utils;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };
}