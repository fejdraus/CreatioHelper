using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.DependencyInjection;

/// <summary>
/// Extension methods for configuring database services
/// </summary>
public static class SyncDatabaseServiceExtensions
{
    public static IServiceCollection AddSyncDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SyncDatabaseOptions>(configuration.GetSection("SyncDatabase"));

        // Register ISyncDatabase with factory pattern to provide databasePath parameter
        services.AddSingleton<ISyncDatabase>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<SqliteSyncDatabase>>();
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            var syncConfig = provider.GetRequiredService<SyncConfiguration>();
            var databasePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CreatioHelper", "Sync", $"sync_{syncConfig.DeviceId}.db");
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            return new SqliteSyncDatabase(logger, loggerFactory, databasePath);
        });

        // Repository services - using working implementations
        services.AddSingleton<IBlockInfoRepository>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<SqliteBlockInfoRepository>>();
            var syncConfig = provider.GetRequiredService<SyncConfiguration>();
            var dbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
                "CreatioHelper", "Sync", $"sync_{syncConfig.DeviceId}.db");
            var connectionString = $"Data Source={dbPath}";
            return new SqliteBlockInfoRepository(logger, connectionString);
        });
        
        // WARNING: Stub implementations are used for repositories that are not yet implemented
        // These return empty/null results and should NOT be used in production for critical operations
        // TODO: Implement proper SQLite repositories for production use
        services.AddTransient<IDeviceInfoRepository>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<StubDeviceInfoRepository>>();
            logger.LogWarning("Using StubDeviceInfoRepository - this is not suitable for production use");
            return new StubDeviceInfoRepository();
        });
        services.AddTransient<IFolderConfigRepository>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<StubFolderConfigRepository>>();
            logger.LogWarning("Using StubFolderConfigRepository - this is not suitable for production use");
            return new StubFolderConfigRepository();
        });
        services.AddTransient<IFileMetadataRepository>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<StubFileMetadataRepository>>();
            logger.LogWarning("Using StubFileMetadataRepository - this is not suitable for production use");
            return new StubFileMetadataRepository();
        });
        services.AddTransient<IGlobalStateRepository>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<StubGlobalStateRepository>>();
            logger.LogWarning("Using StubGlobalStateRepository - this is not suitable for production use");
            return new StubGlobalStateRepository();
        });

        return services;
    }
}