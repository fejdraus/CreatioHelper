using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Infrastructure.Services.Configuration;
using CreatioHelper.Infrastructure.Services.Linux;
using CreatioHelper.Infrastructure.Services.MacOs;
using CreatioHelper.Infrastructure.Services.MacOS;
using CreatioHelper.Infrastructure.Services.Site;
using CreatioHelper.Infrastructure.Services.Redis;
using CreatioHelper.Infrastructure.Services.Network;
using CreatioHelper.Infrastructure.Services.Network.UPnP;
using CreatioHelper.Infrastructure.Services.DeviceManagement;
using CreatioHelper.Infrastructure.Services.Sync.DeviceManagement;
using CreatioHelper.Infrastructure.Services.Sync.Security;
using CreatioHelper.Infrastructure.Logging;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Performance;
using CreatioHelper.Infrastructure.DependencyInjection;
using CreatioHelper.Shared.Interfaces;
using CreatioHelper.Shared.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration? configuration = null)
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
            services.AddSingleton<IIisManager, WindowsIisManager>();
            services.AddSingleton<IRemoteIisManager, WindowsRemoteIisManager>(); // Keep for backward compatibility
            services.AddSingleton<ISiteSynchronizer, WindowsSiteSynchronizer>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IIisManager, WindowsIisManager>(); // Use WindowsIisManager for cross-platform compatibility
            services.AddSingleton<IRemoteIisManager, MacOsRemoteIisManager>();
            services.AddSingleton<ISiteSynchronizer, MacOsSiteSynchronizer>();
        }
        else if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<IIisManager, WindowsIisManager>(); // Use WindowsIisManager for cross-platform compatibility
            services.AddSingleton<IRemoteIisManager, LinuxRemoteIisManager>();
            services.AddSingleton<ISiteSynchronizer, LinuxSiteSynchronizer>();
        }

        // Configuration managers
        services.AddSingleton<IAppSettingsManager, AppSettingsManager>();
        
        // Database services (if configuration is provided)
        if (configuration != null)
        {
            services.AddSyncDatabase(configuration);
        }
        
        // NAT traversal services
        services.AddSingleton<INatService, NatService>();
        services.AddSingleton<IPmpService, PmpService>();
        services.AddSingleton<IUPnPService, SyncthingUPnPService>();
        services.AddSingleton<ICombinedNatService>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<CombinedNatService>>();
            var config = provider.GetRequiredService<IOptions<SyncConfiguration>>();
            var upnpService = provider.GetRequiredService<IUPnPService>();
            var pmpService = provider.GetService<IPmpService>();
            return new CombinedNatService(logger, config, upnpService, pmpService);
        });

        // Pending devices/folders service for Syncthing compatibility
        services.AddSingleton<IPendingService, PendingService>();

        // LDAP authentication service
        services.AddSingleton<ILdapAuthService, LdapAuthService>();

        // Cluster key auto-pairing
        if (configuration != null)
        {
            services.Configure<ClusterKeyConfiguration>(
                configuration.GetSection("ClusterKey"));
        }
        services.AddSingleton<IClusterKeyService, ClusterKeyService>();
        services.AddSingleton<ClusterKeyAutoAcceptHandler>();

        return services;
    }
}
