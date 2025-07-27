using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Infrastructure.Services.Configuration;
using CreatioHelper.Infrastructure.Services.Linux;
using CreatioHelper.Infrastructure.Services.MacOs;
using CreatioHelper.Infrastructure.Services.MacOS;
using CreatioHelper.Infrastructure.Services.Site;
using CreatioHelper.Shared.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        // Базовые сервисы
        services.AddSingleton<IMetricsService, MetricsService>();
        
        // Простое in-memory кэширование для desktop приложения
        services.AddMemoryCache();
        services.AddSingleton<ICacheService, MemoryCacheService>();
        
        // Настройки с кэшированием
        services.AddSingleton<SettingsService>();
        services.AddSingleton<ISettingsService>(provider =>
        {
            var settingsService = provider.GetRequiredService<SettingsService>();
            var cache = provider.GetRequiredService<ICacheService>();
            var metrics = provider.GetRequiredService<IMetricsService>();
            return new CachedSettingsService(settingsService, cache, metrics, provider.GetRequiredService<ILogger<CachedSettingsService>>());
        });

        // Статус серверов с метриками
        services.AddSingleton<IServerStatusService>(provider =>
        {
            var remoteManager = provider.GetRequiredService<IRemoteIisManager>();
            var cache = provider.GetRequiredService<ICacheService>();
            var metrics = provider.GetRequiredService<IMetricsService>();
            return new ServerStatusService(remoteManager, cache, metrics);
        });

        // Платформо-зависимые сервисы IIS
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IRemoteIisManager, WindowsRemoteIisManager>();
            services.AddSingleton<ISiteSynchronizer, WindowsSiteSynchronizer>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IRemoteIisManager, MacOsRemoteIisManager>();
            services.AddSingleton<ISiteSynchronizer, MacOsSiteSynchronizer>();
        }
        else
        {
            services.AddSingleton<IRemoteIisManager, LinuxRemoteIisManager>();
            services.AddSingleton<ISiteSynchronizer, LinuxSiteSynchronizer>();
        }

        // Конфигурационные сервисы
        services.AddSingleton<SiteConfigEditor>();
        services.AddSingleton<IAppSettingsManager, AppSettingsManager>();
        
        // Output writer
        services.AddSingleton<IOutputWriter, ConsoleOutputWriter>();

        return services;
    }
}
