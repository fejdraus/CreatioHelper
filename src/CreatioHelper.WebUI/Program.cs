using System.Globalization;
using Blazored.LocalStorage;
using CreatioHelper.WebUI;
using CreatioHelper.WebUI.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Add localization services
builder.Services.AddLocalization();

// Configure HttpClient for API calls
builder.Services.AddScoped(sp =>
{
    var httpClient = new HttpClient
    {
        BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
    };
    return httpClient;
});

// MudBlazor services
builder.Services.AddMudServices(config =>
{
    config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight;
    config.SnackbarConfiguration.PreventDuplicates = false;
    config.SnackbarConfiguration.NewestOnTop = true;
    config.SnackbarConfiguration.ShowCloseIcon = true;
    config.SnackbarConfiguration.VisibleStateDuration = 5000;
    config.SnackbarConfiguration.HideTransitionDuration = 200;
    config.SnackbarConfiguration.ShowTransitionDuration = 200;
    config.SnackbarConfiguration.SnackbarVariant = Variant.Filled;
});

// Blazored LocalStorage for settings persistence
builder.Services.AddBlazoredLocalStorage();

// Application services
builder.Services.AddScoped<CreatioHelper.WebUI.Services.IConfiguration, SignalRConfiguration>();
builder.Services.AddScoped<IApiClient, ApiClient>();
builder.Services.AddScoped<ISignalRService, SignalRService>();
builder.Services.AddScoped<IThemeService, ThemeService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ILocalizationService, LocalizationService>();
builder.Services.AddScoped<IErrorMessageHelper, ErrorMessageHelper>();
builder.Services.AddScoped<StateContainer>();

var host = builder.Build();

// Initialize localization from saved settings before app runs
await SetCultureFromStorageAsync(host.Services);

await host.RunAsync();

// Helper method to set culture from LocalStorage before app starts
static async Task SetCultureFromStorageAsync(IServiceProvider services)
{
    var jsRuntime = services.GetRequiredService<Microsoft.JSInterop.IJSRuntime>();

    try
    {
        // Read language from localStorage using JS interop
        var savedLanguage = await jsRuntime.InvokeAsync<string?>("localStorage.getItem", new object[] { "gui_language" });

        if (!string.IsNullOrEmpty(savedLanguage))
        {
            // Remove quotes if JSON serialized
            savedLanguage = savedLanguage.Trim('"');

            var cultureMap = new Dictionary<string, string>
            {
                { "en", "en-US" },
                { "ru", "ru-RU" },
                { "de", "de-DE" },
                { "fr", "fr-FR" },
                { "es", "es-ES" }
            };

            if (cultureMap.TryGetValue(savedLanguage, out var fullCulture))
            {
                var culture = new CultureInfo(fullCulture);
                CultureInfo.DefaultThreadCurrentCulture = culture;
                CultureInfo.DefaultThreadCurrentUICulture = culture;
            }
        }
    }
    catch
    {
        // Use default culture if localStorage is not available
    }
}
