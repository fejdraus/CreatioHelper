using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Core;
using Microsoft.Extensions.DependencyInjection;

namespace CreatioHelper.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IOutputWriter, ConsoleOutputWriter>();
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddTransient<ISystemServiceManager, SystemServiceManager>();
        return services;
    }
}
