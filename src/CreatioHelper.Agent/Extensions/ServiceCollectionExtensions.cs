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

    /// <summary>
    /// Добавляет сервисы системы метрик и мониторинга производительности
    /// </summary>
    public static IServiceCollection AddPerformanceServices(this IServiceCollection services)
    {
        // Основные сервисы метрик
        services.AddSingleton<CreatioHelper.Application.Interfaces.IMetricsService, MetricsService>();
        services.AddSingleton<CreatioHelper.Application.Interfaces.IConnectionPoolManager, ConnectionPoolManager>();

        // Сервисы мониторинга - используем полные имена типов для избежания проблем с поиском
        if (OperatingSystem.IsWindows())
        {
            services.AddHostedService<SystemMetricsCollector>();
        }
        services.AddHostedService<MonitoringService>();

        // Health Check
        services.AddScoped<CreatioHelperHealthCheck>();

        return services;
    }
}
