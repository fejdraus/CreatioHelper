using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Infrastructure.Services.Sync.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CreatioHelper.UnitTests;

public class DatabaseMaintenanceOrphanCleanupTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;

    public DatabaseMaintenanceOrphanCleanupTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"chdbmtest_{Guid.NewGuid():N}.db");
        _connectionString = $"Data Source={_dbPath}";
        CreateSchema();
    }

    private void CreateSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE file_metadata (
                folder_id TEXT NOT NULL,
                file_name TEXT NOT NULL,
                size INTEGER NOT NULL DEFAULT 0,
                modified_time TEXT NOT NULL,
                is_deleted BOOLEAN NOT NULL DEFAULT 0
            );
            CREATE TABLE sync_events (
                timestamp TEXT NOT NULL
            );";
        command.ExecuteNonQuery();
    }

    private void SeedFiles(params string[] folderIds)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        foreach (var folderId in folderIds)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO file_metadata (folder_id, file_name, modified_time) VALUES (@folder, @name, @modified)";
            command.Parameters.AddWithValue("@folder", folderId);
            command.Parameters.AddWithValue("@name", $"{folderId}.txt");
            command.Parameters.AddWithValue("@modified", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    private long CountFiles()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM file_metadata";
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    [Fact]
    public async Task Maintenance_KeepsFilesOfConfiguredFolders()
    {
        SeedFiles("folder-a", "folder-b");

        var service = new DatabaseMaintenanceService(
            NullLogger<DatabaseMaintenanceService>.Instance,
            _connectionString,
            _ => Task.FromResult<IReadOnlyCollection<string>>(new[] { "folder-a", "folder-b" }));

        await service.RunMaintenanceNowAsync(CancellationToken.None);

        Assert.Equal(2, CountFiles());
    }

    [Fact]
    public async Task Maintenance_RemovesOnlyFilesOfUnconfiguredFolders()
    {
        SeedFiles("folder-a", "folder-gone");

        var service = new DatabaseMaintenanceService(
            NullLogger<DatabaseMaintenanceService>.Instance,
            _connectionString,
            _ => Task.FromResult<IReadOnlyCollection<string>>(new[] { "folder-a" }));

        await service.RunMaintenanceNowAsync(CancellationToken.None);

        Assert.Equal(1, CountFiles());
    }

    [Fact]
    public async Task Maintenance_KeepsEverything_WhenNoFolderIsKnown()
    {
        SeedFiles("folder-a", "folder-b");

        var service = new DatabaseMaintenanceService(
            NullLogger<DatabaseMaintenanceService>.Instance,
            _connectionString,
            _ => Task.FromResult<IReadOnlyCollection<string>>(Array.Empty<string>()));

        await service.RunMaintenanceNowAsync(CancellationToken.None);

        Assert.Equal(2, CountFiles());
    }

    [Fact]
    public async Task Maintenance_KeepsEverything_WhenFolderLookupFails()
    {
        SeedFiles("folder-a", "folder-b");

        var service = new DatabaseMaintenanceService(
            NullLogger<DatabaseMaintenanceService>.Instance,
            _connectionString,
            _ => throw new InvalidOperationException("config unavailable"));

        await service.RunMaintenanceNowAsync(CancellationToken.None);

        Assert.Equal(2, CountFiles());
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
