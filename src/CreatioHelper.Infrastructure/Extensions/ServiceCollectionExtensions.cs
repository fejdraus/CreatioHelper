using CreatioHelper.Shared.Interfaces;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Infrastructure.Services.Configuration;
using CreatioHelper.Infrastructure.Services.Site;
using Microsoft.Extensions.DependencyInjection;
using CreatioHelper.Infrastructure.Services.Redis;
using CreatioHelper.Infrastructure.Services.Workspace;

namespace CreatioHelper.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IOutputWriter, ConsoleOutputWriter>();
        services.AddSingleton<IAppSettingsManager, AppSettingsManager>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddTransient<ISiteConfigEditor, SiteConfigEditor>();
        services.AddTransient<IIisConfigEditor, IisConfigEditor>();
        services.AddTransient<ISystemServiceManager, SystemServiceManager>();
        services.AddSingleton<IRemoteIisManager, RemoteIisManager>();
        services.AddSingleton<IFileCopyHelper, RobocopyFileCopyHelper>();
        services.AddTransient<IWorkspacePreparer, WorkspacePreparer>();
        services.AddTransient<IRedisManagerFactory, RedisManagerFactory>();
        services.AddTransient<ServerStatusService>();
        services.AddTransient<ISiteSynchronizer, SiteSynchronizer>();
        return services;
    }
}
