using CreatioHelper.Application.Mediator;
using CreatioHelper.Application.Settings;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

// Provides registration helpers for application layer services

namespace CreatioHelper.Application.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<IMediator, CreatioHelper.Application.Mediator.Mediator>();
        services.AddTransient<IRequestHandler<LoadSettingsQuery, AppSettings>, LoadSettingsHandler>();
        services.AddTransient<IRequestHandler<SaveSettingsCommand, Unit>, SaveSettingsHandler>();
        return services;
    }
}
