using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CreatioHelper.Infrastructure.Services.Sync.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CreatioHelper.UnitTests;

public class SchemaMigrationTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    private static readonly string[] RemovedTables =
    {
        "folder_config", "device_info", "folder_devices", "sync_statistics"
    };

    public SchemaMigrationTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"chmig_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
    }

    private void CreateLegacyDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE schema_migrations (
                schema_version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL,
                agent_version TEXT
            );
            INSERT INTO schema_migrations (schema_version, applied_at, agent_version)
                VALUES (1, datetime('now'), 'legacy');

            CREATE TABLE device_info (device_id TEXT PRIMARY KEY, device_name TEXT);
            CREATE TABLE folder_config (folder_id TEXT PRIMARY KEY, folder_label TEXT);
            CREATE TABLE folder_devices (
                folder_id TEXT NOT NULL,
                device_id TEXT NOT NULL,
                PRIMARY KEY (folder_id, device_id),
                FOREIGN KEY (folder_id) REFERENCES folder_config(folder_id) ON DELETE CASCADE,
                FOREIGN KEY (device_id) REFERENCES device_info(device_id) ON DELETE CASCADE
            );
            CREATE TABLE sync_statistics (metric_name TEXT PRIMARY KEY, value INTEGER);";
        command.ExecuteNonQuery();
    }

    private List<string> ListTables()
    {
        var tables = new List<string>();

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private int ReadSchemaVersion()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MAX(schema_version) FROM schema_migrations";
        var result = command.ExecuteScalar();
        return result == null || result == DBNull.Value ? 0 : Convert.ToInt32(result);
    }

    private async Task InitializeAsync()
    {
        var database = new SqliteSyncDatabase(
            NullLogger<SqliteSyncDatabase>.Instance,
            NullLoggerFactory.Instance,
            _dbPath);
        await database.InitializeAsync();
        database.Dispose();
    }

    [Fact]
    public async Task Migration_DropsTablesThatWereNeverWrittenTo()
    {
        CreateLegacyDatabase();
        Assert.All(RemovedTables, t => Assert.Contains(t, ListTables()));

        await InitializeAsync();

        var tables = ListTables();
        Assert.All(RemovedTables, t => Assert.DoesNotContain(t, tables));
    }

    [Fact]
    public async Task Migration_KeepsTablesThatHoldData()
    {
        CreateLegacyDatabase();

        await InitializeAsync();

        var tables = ListTables();
        Assert.Contains("file_metadata", tables);
        Assert.Contains("block_info", tables);
        Assert.Contains("sync_events", tables);
        Assert.Contains("global_state", tables);
    }

    [Fact]
    public async Task Migration_RecordsTheNewSchemaVersion()
    {
        CreateLegacyDatabase();
        Assert.Equal(1, ReadSchemaVersion());

        await InitializeAsync();

        Assert.Equal(2, ReadSchemaVersion());
    }

    [Fact]
    public async Task Migration_IsIdempotent()
    {
        CreateLegacyDatabase();

        await InitializeAsync();
        await InitializeAsync();

        var tables = ListTables();
        Assert.All(RemovedTables, t => Assert.DoesNotContain(t, tables));
        Assert.Equal(2, ReadSchemaVersion());
    }

    [Fact]
    public async Task FreshDatabase_NeverCreatesTheRemovedTables()
    {
        await InitializeAsync();

        var tables = ListTables();
        Assert.All(RemovedTables, t => Assert.DoesNotContain(t, tables));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch (IOException)
        {
        }
    }
}
