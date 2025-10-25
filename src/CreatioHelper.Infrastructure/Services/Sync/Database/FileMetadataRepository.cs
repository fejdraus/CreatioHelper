using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CreatioHelper.Infrastructure.Services.Sync.Database;

/// <summary>
/// SQLite implementation of file metadata repository - similar to Syncthing's FileInfoTruncated storage
/// </summary>
public class FileMetadataRepository : IFileMetadataRepository
{
    private readonly Func<SqliteConnection> _getConnection;
    private readonly ILogger _logger;

    public FileMetadataRepository(Func<SqliteConnection> getConnection, ILogger logger)
    {
        _getConnection = getConnection;
        _logger = logger;
    }

    public async Task<FileMetadata?> GetAsync(string folderId, string fileName)
    {
        const string sql = @"
            SELECT folder_id, file_name, size, modified_time, permissions, is_deleted, is_invalid,
                   is_no_permissions, is_symlink, symlink_target, sequence, version_vector,
                   block_hashes, block_size, hash, modified_by, locally_changed, platform_data, updated_at
            FROM file_metadata 
            WHERE folder_id = @folderId AND file_name = @fileName";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@folderId", folderId);
        command.Parameters.AddWithValue("@fileName", fileName);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return MapFromReader(reader);
        }

        return null;
    }

    public async Task<IEnumerable<FileMetadata>> GetAllAsync(string folderId)
    {
        const string sql = @"
            SELECT folder_id, file_name, size, modified_time, permissions, is_deleted, is_invalid,
                   is_no_permissions, is_symlink, symlink_target, sequence, version_vector,
                   block_hashes, block_size, hash, modified_by, locally_changed, platform_data, updated_at
            FROM file_metadata 
            WHERE folder_id = @folderId
            ORDER BY sequence";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@folderId", folderId);

        var results = new List<FileMetadata>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(MapFromReader(reader));
        }

        return results;
    }

    public async Task UpsertAsync(FileMetadata metadata)
    {
        const string sql = @"
            INSERT OR REPLACE INTO file_metadata (
                folder_id, file_name, size, modified_time, permissions, is_deleted, is_invalid,
                is_no_permissions, is_symlink, symlink_target, sequence, version_vector,
                block_hashes, block_size, hash, modified_by, locally_changed, platform_data, updated_at
            ) VALUES (
                @folderId, @fileName, @size, @modifiedTime, @permissions, @isDeleted, @isInvalid,
                @isNoPermissions, @isSymlink, @symlinkTarget, @sequence, @versionVector,
                @blockHashes, @blockSize, @hash, @modifiedBy, @locallyChanged, @platformData, @updatedAt
            )";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        AddParameters(command, metadata);

        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(string folderId, string fileName)
    {
        const string sql = "DELETE FROM file_metadata WHERE folder_id = @folderId AND file_name = @fileName";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@folderId", folderId);
        command.Parameters.AddWithValue("@fileName", fileName);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<IEnumerable<FileMetadata>> GetBySequenceAsync(string folderId, long fromSequence, int limit = 1000)
    {
        const string sql = @"
            SELECT folder_id, file_name, size, modified_time, permissions, is_deleted, is_invalid,
                   is_no_permissions, is_symlink, symlink_target, sequence, version_vector,
                   block_hashes, block_size, hash, modified_by, locally_changed, platform_data, updated_at
            FROM file_metadata 
            WHERE folder_id = @folderId AND sequence > @fromSequence
            ORDER BY sequence
            LIMIT @limit";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@folderId", folderId);
        command.Parameters.AddWithValue("@fromSequence", fromSequence);
        command.Parameters.AddWithValue("@limit", limit);

        var results = new List<FileMetadata>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(MapFromReader(reader));
        }

        return results;
    }

    public async Task<long> GetGlobalSequenceAsync(string folderId)
    {
        const string sql = "SELECT MAX(sequence) FROM file_metadata WHERE folder_id = @folderId";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@folderId", folderId);

        var result = await command.ExecuteScalarAsync();
        return result == DBNull.Value ? 0 : Convert.ToInt64(result);
    }

    public async Task UpdateGlobalSequenceAsync(string folderId, long sequence)
    {
        // In this implementation, sequence is managed per-file
        // This method could be used to update a separate global sequence table if needed
        await Task.CompletedTask;
    }

    public async Task<IEnumerable<FileMetadata>> GetNeededFilesAsync(string folderId)
    {
        const string sql = @"
            SELECT folder_id, file_name, size, modified_time, permissions, is_deleted, is_invalid,
                   is_no_permissions, is_symlink, symlink_target, sequence, version_vector,
                   block_hashes, block_size, hash, modified_by, locally_changed, platform_data, updated_at
            FROM file_metadata 
            WHERE folder_id = @folderId AND locally_changed = 0 AND is_deleted = 0";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@folderId", folderId);

        var results = new List<FileMetadata>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(MapFromReader(reader));
        }

        return results;
    }

    public async Task MarkLocallyChangedAsync(string folderId, string fileName, long sequence)
    {
        const string sql = @"
            UPDATE file_metadata 
            SET locally_changed = 1, sequence = @sequence, updated_at = @updatedAt
            WHERE folder_id = @folderId AND file_name = @fileName";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@folderId", folderId);
        command.Parameters.AddWithValue("@fileName", fileName);
        command.Parameters.AddWithValue("@sequence", sequence);
        command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync();
    }

    private static FileMetadata MapFromReader(SqliteDataReader reader)
    {
        var metadata = new FileMetadata
        {
            FolderId = reader.GetString(reader.GetOrdinal("folder_id")),
            FileName = reader.GetString(reader.GetOrdinal("file_name")),
            Size = reader.GetInt64(reader.GetOrdinal("size")),
            ModifiedTime = DateTime.Parse(reader.GetString(reader.GetOrdinal("modified_time"))),
            Permissions = (int?)reader.GetInt64(reader.GetOrdinal("permissions")),
            IsDeleted = reader.GetBoolean(reader.GetOrdinal("is_deleted")),
            IsInvalid = reader.GetBoolean(reader.GetOrdinal("is_invalid")),
            SymlinkTarget = reader.IsDBNull(reader.GetOrdinal("symlink_target")) ? null : reader.GetString(reader.GetOrdinal("symlink_target")),
            Sequence = reader.GetInt64(reader.GetOrdinal("sequence")),
            VersionVector = reader.GetString(reader.GetOrdinal("version_vector")),
            BlockSize = reader.GetInt32(reader.GetOrdinal("block_size")),
            Hash = System.Text.Encoding.UTF8.GetBytes(reader.GetString(reader.GetOrdinal("hash"))),
            ModifiedBy = reader.GetString(reader.GetOrdinal("modified_by")),
            LocallyChanged = reader.GetBoolean(reader.GetOrdinal("locally_changed")),
            UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("updated_at")))
        };

        // Deserialize block hashes
        var blockHashesOrdinal = reader.GetOrdinal("block_hashes");
        var blockHashesJson = reader.IsDBNull(blockHashesOrdinal) ? null : reader.GetString(blockHashesOrdinal);
        if (!string.IsNullOrEmpty(blockHashesJson))
        {
            metadata.BlockHashes = JsonSerializer.Deserialize<List<string>>(blockHashesJson) ?? new List<string>();
        }

        // Deserialize platform data
        var platformDataOrdinal = reader.GetOrdinal("platform_data");
        var platformDataJson = reader.IsDBNull(platformDataOrdinal) ? null : reader.GetString(platformDataOrdinal);
        if (!string.IsNullOrEmpty(platformDataJson))
        {
            metadata.PlatformData = JsonSerializer.Deserialize<Dictionary<string, object>>(platformDataJson) ?? new Dictionary<string, object>();
        }

        return metadata;
    }

    private static void AddParameters(SqliteCommand command, FileMetadata metadata)
    {
        command.Parameters.AddWithValue("@folderId", metadata.FolderId);
        command.Parameters.AddWithValue("@fileName", metadata.FileName);
        command.Parameters.AddWithValue("@size", metadata.Size);
        command.Parameters.AddWithValue("@modifiedTime", metadata.ModifiedTime.ToString("O"));
        command.Parameters.AddWithValue("@permissions", metadata.Permissions);
        command.Parameters.AddWithValue("@isDeleted", metadata.IsDeleted);
        command.Parameters.AddWithValue("@isInvalid", metadata.IsInvalid);
        command.Parameters.AddWithValue("@isNoPermissions", metadata.IsNoPermissions);
        command.Parameters.AddWithValue("@isSymlink", metadata.IsSymlink);
        command.Parameters.AddWithValue("@symlinkTarget", metadata.SymlinkTarget);
        command.Parameters.AddWithValue("@sequence", metadata.Sequence);
        command.Parameters.AddWithValue("@versionVector", metadata.VersionVector);
        command.Parameters.AddWithValue("@blockHashes", JsonSerializer.Serialize(metadata.BlockHashes));
        command.Parameters.AddWithValue("@blockSize", metadata.BlockSize);
        command.Parameters.AddWithValue("@hash", metadata.Hash != null ? System.Text.Encoding.UTF8.GetString(metadata.Hash) : string.Empty);
        command.Parameters.AddWithValue("@modifiedBy", metadata.ModifiedBy);
        command.Parameters.AddWithValue("@locallyChanged", metadata.LocallyChanged);
        command.Parameters.AddWithValue("@platformData", JsonSerializer.Serialize(metadata.PlatformData));
        command.Parameters.AddWithValue("@updatedAt", metadata.UpdatedAt.ToString("O"));
    }
    
    public void Dispose()
    {
        // No resources to dispose for this implementation
        _logger.LogDebug("FileMetadataRepository disposed");
    }
}