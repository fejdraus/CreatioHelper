using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CreatioHelper.Infrastructure.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddTransient<ISystemServiceManager, SystemServiceManager>();
        return services;
    }
}
