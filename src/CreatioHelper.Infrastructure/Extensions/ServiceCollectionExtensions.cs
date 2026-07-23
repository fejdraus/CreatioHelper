using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Infrastructure.Services.Configuration;
using CreatioHelper.Infrastructure.Services.Database;
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
using CreatioHelper.Infrastructure.Services.Updates;
using CreatioHelper.Infrastructure.Services.Workspace;
using CreatioHelper.Infrastructure.Logging;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Performance;
using CreatioHelper.Infrastructure.DependencyInjection;
using CreatioHelper.Shared.Interfaces;
using CreatioHelper.Shared.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration? configuration = null)
    {
        services.AddSingleton<IMetricsService, MetricsService>();
        services.AddSingleton<IWebConfigEditor, WebConfigEditor>();
        services.AddSingleton<IConnectionStringsEditor, ConnectionStringsEditor>();
        services.AddSingleton<IHealthCheckService, HealthCheckService>();
        services.AddSingleton<CreatioHelperHealthCheck>();
        services.AddSingleton<IOutputWriter>(_ =>
        {
            return new BufferingOutputWriter(
                line => OutputWriterHandlers.WriteAction(line),
                () => OutputWriterHandlers.ClearAction()
            );
        });
        services.AddSingleton<IFileCopyHelper, SftpFileCopyHelper>();
        services.AddSingleton<IRedisManagerFactory, RedisManagerFactory>();
        services.AddTransient<IWorkspacePreparer, WorkspacePreparer>();
        services.AddTransient<IConfigurationBackupService, ConfigurationBackupService>();
        services.AddTransient<ICustomDescriptorUpdater, CustomDescriptorUpdater>();
        services.AddTransient<IPackageFlagsResetter, PackageFlagsResetter>();
        services.AddTransient<IModuleCleanupService, ModuleCleanupService>();
        if (OperatingSystem.IsWindows())
        {
            services.AddTransient<IWindowsFeaturesService, WindowsFeaturesService>();
        }
        services.AddTransient<ITerrasoftSvnCleanupService, TerrasoftSvnCleanupService>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IServerStatusService, ServerStatusService>();
        services.AddSingleton<ISystemServiceManager, SystemServiceManager>();
        if (OperatingSystem.IsWindows())
        {
            services.AddSingleton<IIisManager, WindowsIisManager>();
            services.AddSingleton<IRemoteIisManager, WindowsRemoteIisManager>();
            services.AddSingleton<ISiteSynchronizer, WindowsSiteSynchronizer>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IIisManager, WindowsIisManager>();
            services.AddSingleton<IRemoteIisManager, MacOsRemoteIisManager>();
            services.AddSingleton<ISiteSynchronizer, MacOsSiteSynchronizer>();
        }
        else if (OperatingSystem.IsLinux())
        {
            services.AddSingleton<IIisManager, WindowsIisManager>();
            services.AddSingleton<IRemoteIisManager, LinuxRemoteIisManager>();
            services.AddSingleton<ISiteSynchronizer, LinuxSiteSynchronizer>();
        }
        services.AddSingleton<IAppSettingsManager, AppSettingsManager>();
        services.AddHttpClient(nameof(UpdateService))
            .ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<IUpdateService, UpdateService>();
        if (configuration != null)
        {
            services.AddSyncDatabase(configuration);
        }
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
        services.AddSingleton<IPendingService, PendingService>();
        services.AddSingleton<ILdapAuthService, LdapAuthService>();
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
