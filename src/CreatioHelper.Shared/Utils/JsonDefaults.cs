using System.Text.Json;

namespace CreatioHelper.Shared.Utils;

/// <summary>
/// Shared serializer options. A JsonSerializerOptions instance caches the serialization metadata
/// it builds, so creating a new one per call throws that cache away every time.
/// </summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions CaseInsensitive = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static readonly JsonSerializerOptions Indented = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}
