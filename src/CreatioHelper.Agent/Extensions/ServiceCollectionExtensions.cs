using CreatioHelper.Agent.Services;
using CreatioHelper.Agent.Services.Windows;
using CreatioHelper.Agent.Configuration;
using CreatioHelper.Infrastructure.Services;
using CreatioHelper.Infrastructure.Services.Performance;
using Microsoft.Extensions.Options;

namespace CreatioHelper.Agent.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPlatformServices(this IServiceCollection services)
    {
        services.AddSingleton<IPlatformService, PlatformService>();
        services.AddSingleton<IFileSyncService, FileSyncService>();
        services.AddScoped<IWebServerServiceFactory, WebServerServiceFactory>();
        services.AddSingleton<IConfigurationService, ConfigurationService>();
        services.AddSingleton<WebSiteRegistryService>();
        if (OperatingSystem.IsWindows())
        {
            // Always register Windows Service Manager as fallback
            services.AddTransient<WindowsServiceManager>();
            
            // Only register IIS services if not in sync testing mode
            var disableIis = Environment.GetEnvironmentVariable("DISABLE_IIS_SERVICE");
            var isDisabled = bool.TryParse(disableIis, out var disabled) && disabled;
            if (!isDisabled)
            {
                services.AddTransient<IisManagerService>();
                services.AddScoped<IisStatusService>();
            }
        }
        if (OperatingSystem.IsLinux())
        {
            services.AddTransient<Services.Linux.SystemdServiceManager>();
        }
        if (OperatingSystem.IsMacOS())
        {
            services.AddTransient<Services.MacOS.LaunchdServiceManager>();
        }
        return services;
    }

    /// <summary>
    /// Adds metrics and performance monitoring services
    /// </summary>
    public static IServiceCollection AddPerformanceServices(this IServiceCollection services)
    {
        // Core metric services
        services.AddSingleton<IMetricsService, MetricsService>();
        services.AddSingleton<IConnectionPoolManager, ConnectionPoolManager>();

        // Monitoring services - use full type names to avoid resolution issues
        if (OperatingSystem.IsWindows())
        {
            services.AddHostedService<SystemMetricsCollector>();
        }
        // MonitoringService is registered as Singleton AND as HostedService because:
        // 1. Singleton allows it to be injected into controllers/services
        // 2. HostedService ensures it starts/stops with the application lifecycle
        // This is the recommended pattern per Microsoft docs for injectable hosted services
        services.AddSingleton<MonitoringService>();
        services.AddHostedService(provider => provider.GetRequiredService<MonitoringService>());

        // Health Check
        services.AddScoped<CreatioHelperHealthCheck>();

        return services;
    }

    /// <summary>
    /// Adds Syncthing automatic service stop/start functionality
    /// </summary>
    public static IServiceCollection AddSyncthingAutoStop(this IServiceCollection services, IConfiguration configuration)
    {
        // Bind configuration with validation
        services.AddOptions<SyncthingAutoStopSettings>()
            .Bind(configuration.GetSection("SyncthingAutoStop"))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Register HttpClient for Syncthing API
        services.AddHttpClient("Syncthing", (provider, client) =>
        {
            var settings = provider.GetRequiredService<IOptions<SyncthingAutoStopSettings>>().Value;

            // Validate URL before creating Uri to prevent UriFormatException at startup
            if (!string.IsNullOrWhiteSpace(settings.SyncthingApiUrl) &&
                Uri.TryCreate(settings.SyncthingApiUrl, UriKind.Absolute, out var baseUri))
            {
                client.BaseAddress = baseUri;
            }

            if (!string.IsNullOrEmpty(settings.SyncthingApiKey))
            {
                client.DefaultRequestHeaders.Add("X-API-Key", settings.SyncthingApiKey);
            }
        });

        // Register monitoring and management services
        // Singleton because it's stateless and creates HttpClient per-request
        services.AddSingleton<SyncthingCompletionMonitor>();
        services.AddScoped(provider =>
        {
            var settings = provider.GetRequiredService<IOptions<SyncthingAutoStopSettings>>().Value;
            var webServerFactory = provider.GetRequiredService<IWebServerServiceFactory>();
            var logger = provider.GetRequiredService<ILogger<ServiceStateManager>>();

            return new ServiceStateManager(
                webServerFactory,
                logger,
                settings);
        });

        // Register background service
        var settings = configuration.GetSection("SyncthingAutoStop").Get<SyncthingAutoStopSettings>();
        if (settings?.Enabled == true)
        {
            services.AddHostedService<SyncthingAutoStopService>();
        }

        return services;
    }
}
