using Blazored.LocalStorage;
using Microsoft.JSInterop;

namespace CreatioHelper.WebUI.Services;

public enum ThemeMode
{
    Light,
    Dark,
    System
}

/// <summary>
/// Service for managing theme (dark/light/system mode) preferences
/// </summary>
public interface IThemeService
{
    bool IsDarkMode { get; }
    ThemeMode CurrentMode { get; }
    event Action<bool>? OnThemeChanged;

    Task InitializeAsync();
    Task<bool> GetDarkModeAsync();
    Task SetThemeModeAsync(ThemeMode mode);
    Task CycleThemeAsync();
}

public class ThemeService : IThemeService
{
    private const string ThemeModeKey = "themeMode";
    private readonly ILocalStorageService _localStorage;
    private readonly IJSRuntime _jsRuntime;
    private ThemeMode _currentMode = ThemeMode.Dark;
    private bool _isDarkMode = true;
    private bool _initialized;

    public bool IsDarkMode => _isDarkMode;
    public ThemeMode CurrentMode => _currentMode;
    public event Action<bool>? OnThemeChanged;

    public ThemeService(ILocalStorageService localStorage, IJSRuntime jsRuntime)
    {
        _localStorage = localStorage;
        _jsRuntime = jsRuntime;
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        try
        {
            var savedMode = await _localStorage.GetItemAsync<string>(ThemeModeKey);
            _currentMode = savedMode switch
            {
                "light" => ThemeMode.Light,
                "dark" => ThemeMode.Dark,
                "system" => ThemeMode.System,
                _ => ThemeMode.Dark
            };

            await ApplyThemeModeAsync();
        }
        catch
        {
            _currentMode = ThemeMode.Dark;
            _isDarkMode = true;
        }

        _initialized = true;
    }

    public async Task<bool> GetDarkModeAsync()
    {
        if (!_initialized)
        {
            await InitializeAsync();
        }
        return _isDarkMode;
    }

    public async Task SetThemeModeAsync(ThemeMode mode)
    {
        _currentMode = mode;
        var modeString = mode switch
        {
            ThemeMode.Light => "light",
            ThemeMode.Dark => "dark",
            ThemeMode.System => "system",
            _ => "dark"
        };

        await _localStorage.SetItemAsync(ThemeModeKey, modeString);
        await ApplyThemeModeAsync();
    }

    public async Task CycleThemeAsync()
    {
        var nextMode = _currentMode switch
        {
            ThemeMode.Light => ThemeMode.Dark,
            ThemeMode.Dark => ThemeMode.System,
            ThemeMode.System => ThemeMode.Light,
            _ => ThemeMode.Dark
        };

        await SetThemeModeAsync(nextMode);
    }

    private async Task ApplyThemeModeAsync()
    {
        var newDarkMode = _currentMode switch
        {
            ThemeMode.Light => false,
            ThemeMode.Dark => true,
            ThemeMode.System => await GetSystemPreferenceAsync(),
            _ => true
        };

        if (_isDarkMode != newDarkMode || !_initialized)
        {
            _isDarkMode = newDarkMode;
            OnThemeChanged?.Invoke(_isDarkMode);
        }
    }

    private async Task<bool> GetSystemPreferenceAsync()
    {
        try
        {
            return await _jsRuntime.InvokeAsync<bool>("eval",
                "window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches");
        }
        catch
        {
            return true; // Default to dark if can't detect
        }
    }
}
