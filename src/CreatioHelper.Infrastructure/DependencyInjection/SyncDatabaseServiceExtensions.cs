using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Configuration.Store;
using CreatioHelper.Infrastructure.Services.Sync.Database;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SyncConfigManager = CreatioHelper.Infrastructure.Services.Configuration.ConfigurationManager;
using ISyncConfigManager = CreatioHelper.Application.Interfaces.IConfigurationManager;

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
        
        // Configuration store (Dapper + FluentMigrator): SQLite standalone by default, shared Postgres later
        services.AddSingleton<IDbConnectionFactory>(provider =>
        {
            var storage = configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();
            var storageProvider = storage.ResolveProvider();
            var connectionString = storage.ConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                var dir = provider.GetRequiredService<IConfigXmlService>().GetConfigDirectory();
                connectionString = DbConnectionFactory.BuildDefaultSqliteConnectionString(dir);
            }
            return new DbConnectionFactory(storageProvider, connectionString);
        });
        services.AddSingleton<ConfigDatabaseInitializer>();
        services.AddSingleton<IConfigurationStore, ConfigurationStore>();

        // ConfigurationManager - backed by the transactional store; imports legacy config.xml once
        services.AddSingleton<ISyncConfigManager>(provider =>
        {
            var configXmlService = provider.GetRequiredService<IConfigXmlService>();
            var store = provider.GetRequiredService<IConfigurationStore>();
            var clusterMode = CreatioHelper.Infrastructure.Services.Configuration.ClusterModeSettings.IsClusterMode(configuration);
            var logger = provider.GetRequiredService<ILogger<SyncConfigManager>>();
            return new SyncConfigManager(configXmlService, store, clusterMode, logger);
        });

        // File metadata repository - still uses SQLite for file index
        services.AddSingleton<IFileMetadataRepository>(provider =>
            provider.GetRequiredService<ISyncDatabase>().FileMetadata);
        services.AddSingleton<IGlobalStateRepository>(provider =>
            provider.GetRequiredService<ISyncDatabase>().GlobalState);

        return services;
    }
}