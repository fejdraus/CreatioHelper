using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Infrastructure.Services.Site;
using CreatioHelper.Core;
using CreatioHelper.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CreatioHelper.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IOutputWriter, ConsoleOutputWriter>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddTransient<ISystemServiceManager, SystemServiceManager>();
        services.AddSingleton<IRemoteIisManager, RemoteIisManager>();
        services.AddSingleton<IFileCopyHelper, RobocopyFileCopyHelper>();
        services.AddTransient<ServerStatusService>();
        services.AddTransient<ISiteSynchronizer, SiteSynchronizer>();
        return services;
    }
}
