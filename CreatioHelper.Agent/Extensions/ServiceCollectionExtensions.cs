using CreatioHelper.Agent.Abstractions;
using CreatioHelper.Agent.Services;
using CreatioHelper.Agent.Services.Windows;

namespace CreatioHelper.Agent.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformServices(this IServiceCollection services)
    {
        // Основные сервисы
        services.AddSingleton<IPlatformService, PlatformService>();
        services.AddSingleton<IFileSyncService, FileSyncService>();
        services.AddScoped<IWebServerServiceFactory, WebServerServiceFactory>();
        services.AddSingleton<MonitoringService>();
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddScoped<IWebServerServiceFactory, WebServerServiceFactory>();
        services.AddSingleton<WebSiteRegistryService>();

        // Windows-специфичные сервисы
        if (OperatingSystem.IsWindows())
        {
            services.AddTransient<IisManagerService>();
            services.AddScoped<IisStatusService>();
        }

        // Linux-специфичные сервисы
        if (OperatingSystem.IsLinux())
        {
            services.AddTransient<Services.Linux.SystemdServiceManager>();
        }
        
        // macOS-специфичные сервисы
        if (OperatingSystem.IsMacOS())
        {
            services.AddTransient<Services.MacOS.LaunchdServiceManager>();
        }

        return services;
    }
}