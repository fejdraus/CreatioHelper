using System;
using System.IO;
using System.Threading.Tasks;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CreatioHelper.UnitTests;

public class FileMetadataRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteConnection _connection;
    private readonly FileMetadataRepository _repository;

    public FileMetadataRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"chfmtest_{Guid.NewGuid():N}.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        CreateSchema();
        _repository = new FileMetadataRepository(() => _connection, NullLogger.Instance);
    }

    private void CreateSchema()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS file_metadata (
                folder_id TEXT NOT NULL,
                file_name TEXT NOT NULL,
                size INTEGER NOT NULL DEFAULT 0,
                modified_time TEXT NOT NULL,
                permissions INTEGER,
                is_deleted BOOLEAN NOT NULL DEFAULT 0,
                is_invalid BOOLEAN NOT NULL DEFAULT 0,
                is_no_permissions BOOLEAN NOT NULL DEFAULT 0,
                is_symlink BOOLEAN NOT NULL DEFAULT 0,
                symlink_target TEXT,
                sequence INTEGER NOT NULL DEFAULT 0,
                version_vector TEXT NOT NULL DEFAULT '[]',
                block_hashes TEXT,
                block_size INTEGER NOT NULL DEFAULT 0,
                hash TEXT,
                modified_by TEXT NOT NULL DEFAULT '',
                locally_changed BOOLEAN NOT NULL DEFAULT 0,
                local_flags INTEGER NOT NULL DEFAULT 0,
                platform_data TEXT,
                updated_at TEXT NOT NULL DEFAULT (datetime('now')),
                PRIMARY KEY (folder_id, file_name)
            );";
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task Upsert_WithUnsetScanDefaults_DoesNotThrow_AndPersists()
    {
        var version = new BepVectorClock();
        version.Increment(BepVectorClock.ShortIdFromString("dev-a"));

        var metadata = new FileMetadata
        {
            FolderId = "folder1",
            FileName = "Pkg/Some/file.txt",
            Size = 123,
            ModifiedTime = DateTime.UtcNow,
            DeviceId = "dev-a",
            Sequence = 1000,
            VersionVector = version
        };

        await _repository.UpsertAsync(metadata);

        var loaded = await _repository.GetAsync("folder1", "Pkg/Some/file.txt");

        Assert.NotNull(loaded);
        Assert.Equal(123, loaded!.Size);
        Assert.Equal(0, loaded.BlockSize ?? 0);
        Assert.Equal(version, loaded.VersionVector);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try
        {
            File.Delete(_dbPath);
        }
        catch
        {
        }
    }
}
