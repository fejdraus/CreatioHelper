namespace CreatioHelper.Agent.Abstractions;

public interface IConfigurationService
{
    Task<string> GetWebServerTypeAsync();
    Task SetWebServerTypeAsync(string type);
    Task<T> GetSettingAsync<T>(string key, T? defaultValue = default);
    Task SetSettingAsync<T>(string key, T value);
}