using System.Text.Json;

namespace CreatioHelper.Agent.Services;

public class ConfigurationService : IConfigurationService
{
    private readonly string _configPath;
    private readonly ILogger<ConfigurationService> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public ConfigurationService(ILogger<ConfigurationService> logger, IWebHostEnvironment environment)
    {
        _logger = logger;
        _configPath = Path.Combine(environment.ContentRootPath, "agent-settings.json");
    }

    public async Task<string> GetWebServerTypeAsync()
    {
        return await GetSettingAsync("WebServer:PreferredType", "IIS");
    }

    public async Task SetWebServerTypeAsync(string type)
    {
        await SetSettingAsync("WebServer:PreferredType", type);
    }

    public async Task<T> GetSettingAsync<T>(string key, T? defaultValue = default)
    {
        await _semaphore.WaitAsync();
        try
        {
            var settings = await LoadSettingsAsync();
            
            if (TryGetNestedValue(settings, key, out var value) && value is JsonElement element)
            {
                if (typeof(T) == typeof(string))
                    return (T)(object?)element.GetString();
                if (typeof(T) == typeof(int))
                    return (T)(object)element.GetInt32();
                if (typeof(T) == typeof(bool))
                    return (T)(object)element.GetBoolean();

                return JsonSerializer.Deserialize<T>(element.GetRawText())!;
            }
            
            return defaultValue;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task SetSettingAsync<T>(string key, T value)
    {
        await _semaphore.WaitAsync();
        try
        {
            var settings = await LoadSettingsAsync();
            SetNestedValue(settings, key, value);
            await SaveSettingsAsync(settings);
            
            _logger.LogInformation("Setting {Key} set to {Value}", key, value);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task<Dictionary<string, object>> LoadSettingsAsync()
    {
        if (!File.Exists(_configPath))
            return new Dictionary<string, object>();

        try
        {
            var json = await File.ReadAllTextAsync(_configPath);
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings from {Path}", _configPath);
            return new Dictionary<string, object>();
        }
    }

    private async Task SaveSettingsAsync(Dictionary<string, object> settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            await File.WriteAllTextAsync(_configPath, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings to {Path}", _configPath);
            throw;
        }
    }

    private static bool TryGetNestedValue(Dictionary<string, object> dict, string key, out object? value)
    {
        value = null;
        var keys = key.Split(':');
        object current = dict;

        foreach (var k in keys)
        {
            if (current is Dictionary<string, object> currentDict && currentDict.TryGetValue(k, out var nextValue))
            {
                current = nextValue;
            }
            else if (current is JsonElement element && element.ValueKind == JsonValueKind.Object && element.TryGetProperty(k, out var property))
            {
                current = property;
            }
            else
            {
                return false;
            }
        }

        value = current;
        return true;
    }

    private static void SetNestedValue<T>(Dictionary<string, object> dict, string key, T value)
    {
        var keys = key.Split(':');
        var current = dict;

        for (int i = 0; i < keys.Length - 1; i++)
        {
            Dictionary<string, object> nextDict;

            if (!current.TryGetValue(keys[i], out var next) || next is not Dictionary<string, object> dictValue)
            {
                nextDict = new Dictionary<string, object>();
                current[keys[i]] = nextDict;
            }
            else
            {
                nextDict = dictValue;
            }

            current = nextDict;
        }

        current[keys[^1]] = value!;
    }
}