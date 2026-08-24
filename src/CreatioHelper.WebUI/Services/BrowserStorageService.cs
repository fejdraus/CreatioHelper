using System.Text.Json;
using Microsoft.JSInterop;

namespace CreatioHelper.WebUI.Services;

public interface IBrowserStorageService
{
    Task<T?> GetItemAsync<T>(string key);
    Task SetItemAsync<T>(string key, T value);
    Task RemoveItemAsync(string key);
}

public class BrowserStorageService : IBrowserStorageService
{
    private readonly IJSRuntime _js;

    public BrowserStorageService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<T?> GetItemAsync<T>(string key)
    {
        string? json;
        try
        {
            json = await _js.InvokeAsync<string?>("localStorage.getItem", key);
        }
        catch
        {
            return default;
        }

        if (string.IsNullOrEmpty(json))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    public async Task SetItemAsync<T>(string key, T value)
    {
        var json = JsonSerializer.Serialize(value);
        await _js.InvokeVoidAsync("localStorage.setItem", key, json);
    }

    public async Task RemoveItemAsync(string key)
    {
        await _js.InvokeVoidAsync("localStorage.removeItem", key);
    }
}
