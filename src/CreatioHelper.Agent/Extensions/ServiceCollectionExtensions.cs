using CreatioHelper.Application.Interfaces;
using CreatioHelper.Agent.Services;
using CreatioHelper.Agent.Services.Windows;

namespace CreatioHelper.Agent.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformServices(this IServiceCollection services)
    {
        services.AddSingleton<IPlatformService, PlatformService>();
        services.AddSingleton<IFileSyncService, FileSyncService>();
        services.AddScoped<IWebServerServiceFactory, WebServerServiceFactory>();
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<WebSiteRegistryService>();
        if (OperatingSystem.IsWindows())
        {
            services.AddTransient<IisManagerService>();
            services.AddScoped<IisStatusService>();
        }
        if (OperatingSystem.IsLinux())
        {
            services.AddTransient<Services.Linux.SystemdServiceManager>();
        }
        if (OperatingSystem.IsMacOS())
        {
            services.AddTransient<Services.MacOS.LaunchdServiceManager>();
        }
        return services;
    }
}