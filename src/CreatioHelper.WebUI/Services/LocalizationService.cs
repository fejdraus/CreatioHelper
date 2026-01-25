using System.Globalization;
using Blazored.LocalStorage;
using Microsoft.JSInterop;

namespace CreatioHelper.WebUI.Services;

/// <summary>
/// Service for managing application localization
/// </summary>
public interface ILocalizationService
{
    /// <summary>
    /// Current culture code (e.g., "en", "ru")
    /// </summary>
    string CurrentCulture { get; }

    /// <summary>
    /// Set the application language and reload
    /// </summary>
    Task SetLanguageAsync(string cultureCode);

    /// <summary>
    /// Initialize localization from saved settings
    /// </summary>
    Task InitializeAsync();
}

public class LocalizationService : ILocalizationService
{
    private readonly ILocalStorageService _localStorage;
    private readonly IJSRuntime _jsRuntime;
    private const string StorageKey = "gui_language";
    private const string DefaultCulture = "en";

    private static readonly Dictionary<string, string> SupportedCultures = new()
    {
        { "en", "en-US" },
        { "ru", "ru-RU" },
        { "de", "de-DE" },
        { "fr", "fr-FR" },
        { "es", "es-ES" }
    };

    public string CurrentCulture { get; private set; } = DefaultCulture;

    public LocalizationService(ILocalStorageService localStorage, IJSRuntime jsRuntime)
    {
        _localStorage = localStorage;
        _jsRuntime = jsRuntime;
    }

    public async Task InitializeAsync()
    {
        try
        {
            var savedCulture = await _localStorage.GetItemAsync<string>(StorageKey);
            if (!string.IsNullOrEmpty(savedCulture) && SupportedCultures.ContainsKey(savedCulture))
            {
                CurrentCulture = savedCulture;
                ApplyCulture(savedCulture);
            }
        }
        catch
        {
            // Use default if localStorage is not available
        }
    }

    public async Task SetLanguageAsync(string cultureCode)
    {
        if (!SupportedCultures.ContainsKey(cultureCode))
        {
            cultureCode = DefaultCulture;
        }

        await _localStorage.SetItemAsync(StorageKey, cultureCode);
        CurrentCulture = cultureCode;

        // Reload the page to apply the new culture
        await _jsRuntime.InvokeVoidAsync("location.reload");
    }

    private void ApplyCulture(string cultureCode)
    {
        if (SupportedCultures.TryGetValue(cultureCode, out var fullCulture))
        {
            var culture = new CultureInfo(fullCulture);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
    }
}
