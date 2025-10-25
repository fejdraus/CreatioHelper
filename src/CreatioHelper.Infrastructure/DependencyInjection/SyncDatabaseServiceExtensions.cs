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
        
        services.AddSingleton<ISyncDatabase, SqliteSyncDatabase>();
        
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
        
        // Temporary stub implementations for compilation
        services.AddTransient<IDeviceInfoRepository, StubDeviceInfoRepository>();
        services.AddTransient<IFolderConfigRepository, StubFolderConfigRepository>();
        services.AddTransient<IFileMetadataRepository, StubFileMetadataRepository>();
        services.AddTransient<IGlobalStateRepository, StubGlobalStateRepository>();
        
        return services;
    }
}