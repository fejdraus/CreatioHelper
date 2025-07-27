using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Infrastructure.Services.Configuration;
using CreatioHelper.Infrastructure.Services.Linux;
using CreatioHelper.Infrastructure.Services.MacOs;
using CreatioHelper.Infrastructure.Services.MacOS;
using CreatioHelper.Infrastructure.Services.Site;
using CreatioHelper.Infrastructure.Services.Redis;
using CreatioHelper.Infrastructure.Services.Performance;
using CreatioHelper.Infrastructure.Logging;
using CreatioHelper.Shared.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        // Базовые сервисы
        services.AddSingleton<IMetricsService, MetricsService>();
        
        // OutputWriter для логирования
        services.AddSingleton<IOutputWriter>(provider => 
        {
            // Создаем BufferingOutputWriter который будет использоваться всеми сервисами
            return new BufferingOutputWriter(
                line => Console.WriteLine(line), // Выводим в консоль
                () => Console.Clear()
            );
        });
        
        // Сервис копирования файлов
        services.AddSingleton<IFileCopyHelper, RobocopyFileCopyHelper>();
        
        // Redis Manager Factory для работы с Redis
        services.AddSingleton<IRedisManagerFactory, RedisManagerFactory>();
        
        // Системные метрики - фоновый сервис для мониторинга ресурсов (только для Windows)
        if (OperatingSystem.IsWindows())
        {
            services.AddHostedService<SystemMetricsCollector>();
        }
        
        // Настройки - простая реализация без кэширования для актуальных данных
        services.AddSingleton<ISettingsService, SettingsService>();

        // Статус серверов - получение актуальных данных без кэширования
        services.AddSingleton<IServerStatusService, ServerStatusService>();

        // Менеджер системных сервисов
        services.AddSingleton<ISystemServiceManager, SystemServiceManager>();

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
        else if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<IRemoteIisManager, LinuxRemoteIisManager>();
            services.AddSingleton<ISiteSynchronizer, LinuxSiteSynchronizer>();
        }

        // Менеджеры конфигурации
        services.AddSingleton<IAppSettingsManager, AppSettingsManager>();
        
        return services;
    }
}
