using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Infrastructure.Services.Configuration;
using CreatioHelper.Infrastructure.Services.Linux;
using CreatioHelper.Infrastructure.Services.MacOs;
using CreatioHelper.Infrastructure.Services.MacOS;
using CreatioHelper.Infrastructure.Services.Site;
using CreatioHelper.Infrastructure.Services.Redis;
using CreatioHelper.Infrastructure.Logging;
using CreatioHelper.Infrastructure.Services.Performance;
using CreatioHelper.Shared.Interfaces;
using CreatioHelper.Shared.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace CreatioHelper.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services)
    {
        // Core services
        services.AddSingleton<IMetricsService, MetricsService>();
        
        // Health check services
        services.AddSingleton<IHealthCheckService, HealthCheckService>();
        services.AddSingleton<CreatioHelperHealthCheck>();
        
        // OutputWriter for logging
        services.AddSingleton<IOutputWriter>(_ =>
        {
            return new BufferingOutputWriter(
                line => OutputWriterHandlers.WriteAction(line),
                () => OutputWriterHandlers.ClearAction()
            );
        });
        
        // File copy service
        services.AddSingleton<IFileCopyHelper, RobocopyFileCopyHelper>();
        
        // Redis Manager Factory for Redis operations
        services.AddSingleton<IRedisManagerFactory, RedisManagerFactory>();
        
        // Remove SystemMetricsCollector registration here - it is registered in the Agent project
        
        // Settings service - simple implementation without caching for up-to-date data
        services.AddSingleton<ISettingsService, SettingsService>();

        // Server status - fetch actual data without caching
        services.AddSingleton<IServerStatusService, ServerStatusService>();

        // System service manager
        services.AddSingleton<ISystemServiceManager, SystemServiceManager>();

        // Platform-specific IIS services
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

        // Configuration managers
        services.AddSingleton<IAppSettingsManager, AppSettingsManager>();
        
        return services;
    }
}
