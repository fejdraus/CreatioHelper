using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Database;

/// <summary>
/// Stub implementations of repository interfaces for compilation
/// These will be replaced with proper SQLite implementations
/// </summary>
public class StubDeviceInfoRepository : IDeviceInfoRepository
{
    public Task<SyncDevice?> GetAsync(string deviceId) => Task.FromResult<SyncDevice?>(null);
    public Task SaveAsync(SyncDevice device) => Task.CompletedTask;
    public Task<IEnumerable<SyncDevice>> GetAllAsync() => Task.FromResult(Enumerable.Empty<SyncDevice>());
    public Task DeleteAsync(string deviceId) => Task.CompletedTask;
    public Task UpsertAsync(SyncDevice device) => Task.CompletedTask;
    public Task<IEnumerable<SyncDevice>> GetDevicesForFolderAsync(string folderId) => Task.FromResult(Enumerable.Empty<SyncDevice>());
    public Task UpdateStatisticsAsync(string deviceId, long bytesTransferred, long filesTransferred, DateTime lastSeen) => Task.CompletedTask;
    public Task UpdateLastSeenAsync(string deviceId, DateTime lastSeen) => Task.CompletedTask;
    public void Dispose() { }
}

public class StubFileMetadataRepository : IFileMetadataRepository
{
    public Task<FileMetadata?> GetAsync(string folderId, string fileName) => Task.FromResult<FileMetadata?>(null);
    public Task<IEnumerable<FileMetadata>> GetAllAsync(string folderId) => Task.FromResult(Enumerable.Empty<FileMetadata>());
    public Task SaveAsync(FileMetadata fileMetadata) => Task.CompletedTask;
    public Task DeleteAsync(string folderId, string fileName) => Task.CompletedTask;
    public Task UpsertAsync(FileMetadata fileMetadata) => Task.CompletedTask;
    public Task<IEnumerable<FileMetadata>> GetBySequenceAsync(string folderId, long fromSequence, int limit) => Task.FromResult(Enumerable.Empty<FileMetadata>());
    public Task<long> GetGlobalSequenceAsync(string folderId) => Task.FromResult(0L);
    public Task UpdateGlobalSequenceAsync(string folderId, long sequence) => Task.CompletedTask;
    public Task<IEnumerable<FileMetadata>> GetNeededFilesAsync(string folderId) => Task.FromResult(Enumerable.Empty<FileMetadata>());
    public Task MarkLocallyChangedAsync(string folderId, string fileName, long sequence) => Task.CompletedTask;
    public void Dispose() { }
}

public class StubFolderConfigRepository : IFolderConfigRepository
{
    public Task<SyncFolder?> GetAsync(string folderId) => Task.FromResult<SyncFolder?>(null);
    public Task<IEnumerable<SyncFolder>> GetAllAsync() => Task.FromResult(Enumerable.Empty<SyncFolder>());
    public Task SaveAsync(SyncFolder folder) => Task.CompletedTask;
    public Task DeleteAsync(string folderId) => Task.CompletedTask;
    public Task UpsertAsync(SyncFolder folder) => Task.CompletedTask;
    public Task<IEnumerable<SyncFolder>> GetFoldersSharedWithDeviceAsync(string deviceId) => Task.FromResult(Enumerable.Empty<SyncFolder>());
    public Task UpdateStatisticsAsync(string folderId, long bytesTransferred, long filesTransferred, DateTime lastSync) => Task.CompletedTask;
    public void Dispose() { }
}

public class StubGlobalStateRepository : IGlobalStateRepository
{
    public Task<string?> GetValueAsync(string key) => Task.FromResult<string?>(null);
    public Task SetValueAsync(string key, string value) => Task.CompletedTask;
    public Task DeleteValueAsync(string key) => Task.CompletedTask;
    public Task<Dictionary<string, string>> GetValuesByPrefixAsync(string prefix) => Task.FromResult(new Dictionary<string, string>());
    public Task<long> IncrementCounterAsync(string key, long increment) => Task.FromResult(increment);
    public Task<bool> TryLockAsync(string lockKey, TimeSpan timeout) => Task.FromResult(true);
    public Task ReleaseLockAsync(string lockKey) => Task.CompletedTask;
    public Task<int> GetSchemaVersionAsync() => Task.FromResult(1);
    public Task SetSchemaVersionAsync(int version) => Task.CompletedTask;
    public void Dispose() { }
}