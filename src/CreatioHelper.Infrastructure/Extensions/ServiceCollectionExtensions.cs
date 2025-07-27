using CreatioHelper.Shared.Interfaces;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Infrastructure.Services.Configuration;
using CreatioHelper.Infrastructure.Services.Site;
using Microsoft.Extensions.DependencyInjection;
using CreatioHelper.Infrastructure.Services.Redis;
using CreatioHelper.Infrastructure.Services.Workspace;
using CreatioHelper.Infrastructure.Services.Linux;
using CreatioHelper.Infrastructure.Services.MacOs;
using CreatioHelper.Infrastructure.Services.MacOS;

namespace CreatioHelper.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IOutputWriter, ConsoleOutputWriter>();
        services.AddSingleton<IAppSettingsManager, AppSettingsManager>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddTransient<ISiteConfigEditor, SiteConfigEditor>();
        
        if (OperatingSystem.IsWindows())
        {
            services.AddTransient<IIisConfigEditor, WindowsIisConfigEditor>();
            services.AddSingleton<IRemoteIisManager, WindowsRemoteIisManager>();
            services.AddTransient<ISiteSynchronizer, WindowsSiteSynchronizer>();
        }
        else if (OperatingSystem.IsMacOS())
        {
            services.AddSingleton<IRemoteIisManager, MacOsRemoteIisManager>();
            services.AddTransient<ISiteSynchronizer, MacOsSiteSynchronizer>();
        }
        else
        {
            services.AddSingleton<IRemoteIisManager, LinuxRemoteIisManager>();
            services.AddTransient<ISiteSynchronizer, LinuxSiteSynchronizer>();
        }

        services.AddTransient<ISystemServiceManager, SystemServiceManager>();
        services.AddSingleton<IFileCopyHelper, RobocopyFileCopyHelper>();
        services.AddTransient<IWorkspacePreparer, WorkspacePreparer>();
        services.AddTransient<IRedisManagerFactory, RedisManagerFactory>();
        services.AddTransient<ServerStatusService>();
        return services;
    }
}
