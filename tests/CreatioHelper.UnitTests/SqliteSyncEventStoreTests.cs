using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Domain.Entities.Events;
using CreatioHelper.Infrastructure.Services.Sync.Database;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CreatioHelper.UnitTests;

public class SqliteSyncEventStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteSyncEventStore _store;

    public SqliteSyncEventStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"chevt_{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={_dbPath}";
        CreateSchema(connectionString);
        _store = new SqliteSyncEventStore(NullLogger<SqliteSyncEventStore>.Instance, connectionString);
    }

    private static void CreateSchema(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE sync_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_type TEXT NOT NULL,
                folder_id TEXT,
                device_id TEXT,
                file_name TEXT,
                event_data TEXT,
                timestamp TEXT NOT NULL DEFAULT (datetime('now'))
            );";
        command.ExecuteNonQuery();
    }

    private static SyncEvent Event(SyncEventType type, string? folderId = null, string? message = null)
    {
        return new SyncEvent
        {
            Type = type,
            FolderId = folderId,
            Message = message,
            Time = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc)
        };
    }

    [Fact]
    public async Task AppendAsync_IgnoresEmptyBatch()
    {
        await _store.AppendAsync(Array.Empty<SyncEvent>());

        Assert.Empty(await _store.LoadRecentAsync(10));
    }

    [Fact]
    public async Task LoadRecentAsync_ReturnsWhatWasAppended()
    {
        await _store.AppendAsync(new[]
        {
            Event(SyncEventType.FolderScanProgress, "folder-a", "scan started"),
            Event(SyncEventType.FolderErrors, "folder-b", "boom")
        });

        var loaded = await _store.LoadRecentAsync(10);

        Assert.Equal(2, loaded.Count);
        Assert.Equal(SyncEventType.FolderScanProgress, loaded[0].Type);
        Assert.Equal("folder-a", loaded[0].FolderId);
        Assert.Equal("scan started", loaded[0].Message);
        Assert.Equal("folder-b", loaded[1].FolderId);
    }

    [Fact]
    public async Task LoadRecentAsync_KeepsChronologicalOrder()
    {
        var batch = new List<SyncEvent>();
        for (var i = 0; i < 5; i++)
        {
            batch.Add(Event(SyncEventType.FolderScanProgress, $"folder-{i}"));
        }

        await _store.AppendAsync(batch);

        var loaded = await _store.LoadRecentAsync(10);

        Assert.Equal(5, loaded.Count);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal($"folder-{i}", loaded[i].FolderId);
        }
    }

    [Fact]
    public async Task LoadRecentAsync_ReturnsNewestWhenLimited()
    {
        var batch = new List<SyncEvent>();
        for (var i = 0; i < 10; i++)
        {
            batch.Add(Event(SyncEventType.FolderScanProgress, $"folder-{i}"));
        }

        await _store.AppendAsync(batch);

        var loaded = await _store.LoadRecentAsync(3);

        Assert.Equal(3, loaded.Count);
        Assert.Equal("folder-7", loaded[0].FolderId);
        Assert.Equal("folder-9", loaded[2].FolderId);
    }

    [Fact]
    public async Task Roundtrip_PreservesTimestamp()
    {
        var original = Event(SyncEventType.FolderErrors, "folder-a");

        await _store.AppendAsync(new[] { original });

        var loaded = await _store.LoadRecentAsync(1);

        Assert.Single(loaded);
        Assert.Equal(original.Time, loaded[0].Time.ToUniversalTime());
    }

    [Fact]
    public async Task AppendAsync_SurvivesUnserializablePayload()
    {
        var problematic = Event(SyncEventType.FolderErrors, "folder-a", "kept");
        problematic.Data = new NotSerializable();

        await _store.AppendAsync(new[] { problematic });

        var loaded = await _store.LoadRecentAsync(1);

        Assert.Single(loaded);
        Assert.Equal("kept", loaded[0].Message);
    }

    [Fact]
    public async Task AppendAsync_AccumulatesAcrossCalls()
    {
        await _store.AppendAsync(new[] { Event(SyncEventType.FolderScanProgress, "first") });
        await _store.AppendAsync(new[] { Event(SyncEventType.FolderScanProgress, "second") });

        var loaded = await _store.LoadRecentAsync(10);

        Assert.Equal(2, loaded.Count);
        Assert.Equal("first", loaded[0].FolderId);
        Assert.Equal("second", loaded[1].FolderId);
    }

    private class NotSerializable
    {
        public NotSerializable Self => this;
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
