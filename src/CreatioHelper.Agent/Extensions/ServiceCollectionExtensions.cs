using CreatioHelper.Agent.Services;
using CreatioHelper.Agent.Services.Windows;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Infrastructure.Services.Performance;

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
            // Always register Windows Service Manager as fallback
            services.AddTransient<WindowsServiceManager>();
            
            // Only register IIS services if not in sync testing mode
            var disableIis = Environment.GetEnvironmentVariable("DISABLE_IIS_SERVICE");
            if (string.IsNullOrEmpty(disableIis) || !bool.Parse(disableIis))
            {
                services.AddTransient<IisManagerService>();
                services.AddScoped<IisStatusService>();
            }
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

    /// <summary>
    /// Adds metrics and performance monitoring services
    /// </summary>
    public static IServiceCollection AddPerformanceServices(this IServiceCollection services)
    {
        // Core metric services
        services.AddSingleton<IMetricsService, MetricsService>();
        services.AddSingleton<IConnectionPoolManager, ConnectionPoolManager>();

        // Monitoring services - use full type names to avoid resolution issues
        if (OperatingSystem.IsWindows())
        {
            services.AddHostedService<SystemMetricsCollector>();
        }
        services.AddSingleton<MonitoringService>();
        services.AddHostedService<MonitoringService>(provider => provider.GetRequiredService<MonitoringService>());

        // Health Check
        services.AddScoped<CreatioHelperHealthCheck>();

        return services;
    }
}
