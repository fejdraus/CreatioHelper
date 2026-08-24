using System.Data;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace CreatioHelper.Infrastructure.Services.Configuration.Store;

public class DbConnectionFactory : IDbConnectionFactory
{
    public DbConnectionFactory(StorageProvider provider, string connectionString)
    {
        Provider = provider;
        ConnectionString = connectionString;
    }

    public StorageProvider Provider { get; }

    public string ConnectionString { get; }

    public IDbConnection Create()
    {
        return Provider switch
        {
            StorageProvider.Postgres => new NpgsqlConnection(ConnectionString),
            _ => new SqliteConnection(ConnectionString)
        };
    }

    public static string BuildDefaultSqliteConnectionString(string configDirectory)
    {
        var path = Path.Combine(configDirectory, "config.db");
        return $"Data Source={path};Cache=Shared;Foreign Keys=True;";
    }
}
