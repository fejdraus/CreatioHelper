using Blazored.LocalStorage;

namespace CreatioHelper.WebUI.Services;

/// <summary>
/// Service for managing theme (dark/light mode) preferences
/// </summary>
public interface IThemeService
{
    bool IsDarkMode { get; }
    event Action<bool>? OnThemeChanged;

    Task<bool> GetDarkModeAsync();
    Task SetDarkModeAsync(bool isDarkMode);
    Task ToggleThemeAsync();
}

public class ThemeService : IThemeService
{
    private const string DarkModeKey = "darkMode";
    private readonly ILocalStorageService _localStorage;
    private bool _isDarkMode = true;
    private bool _initialized;

    public bool IsDarkMode => _isDarkMode;
    public event Action<bool>? OnThemeChanged;

    public ThemeService(ILocalStorageService localStorage)
    {
        _localStorage = localStorage;
    }

    public async Task<bool> GetDarkModeAsync()
    {
        if (!_initialized)
        {
            try
            {
                _isDarkMode = await _localStorage.GetItemAsync<bool?>(DarkModeKey) ?? true;
            }
            catch
            {
                _isDarkMode = true;
            }
            _initialized = true;
        }
        return _isDarkMode;
    }

    public async Task SetDarkModeAsync(bool isDarkMode)
    {
        _isDarkMode = isDarkMode;
        await _localStorage.SetItemAsync(DarkModeKey, isDarkMode);
        OnThemeChanged?.Invoke(isDarkMode);
    }

    public async Task ToggleThemeAsync()
    {
        await SetDarkModeAsync(!_isDarkMode);
    }
}
