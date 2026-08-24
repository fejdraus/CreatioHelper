using CreatioHelper.Infrastructure.Services.Configuration.Store.Migrations;
using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Configuration.Store;

public class ConfigDatabaseInitializer
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<ConfigDatabaseInitializer> _logger;

    public ConfigDatabaseInitializer(
        IDbConnectionFactory connectionFactory,
        ILogger<ConfigDatabaseInitializer> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public void Initialize()
    {
        var services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb =>
            {
                var builder = _connectionFactory.Provider == StorageProvider.Postgres
                    ? rb.AddPostgres()
                    : rb.AddSQLite();

                builder
                    .WithGlobalConnectionString(_connectionFactory.ConnectionString)
                    .ScanIn(typeof(M0001_InitialConfigSchema).Assembly).For.Migrations();
            })
            .AddLogging(lb => lb.AddFluentMigratorConsole())
            .BuildServiceProvider(validateScopes: false);

        using var scope = services.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<IMigrationRunner>();
        runner.MigrateUp();

        _logger.LogInformation(
            "Configuration database schema is up to date (provider={Provider})",
            _connectionFactory.Provider);
    }
}
