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
builder.Services.AddScoped<StateContainer>();

await builder.Build().RunAsync();
