using System.Text.Json;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Database;

/// <summary>
/// SQLite implementation of block info repository compatible with Syncthing's blocks table structure
/// Provides efficient block metadata storage and retrieval for deduplication and sync operations
/// </summary>
public class SqliteBlockInfoRepository : IBlockInfoRepository
{
    private readonly ILogger<SqliteBlockInfoRepository> _logger;
    private readonly string _connectionString;

    public SqliteBlockInfoRepository(ILogger<SqliteBlockInfoRepository> logger, string connectionString)
    {
        _logger = logger;
        _connectionString = connectionString;
    }

    public async Task<BlockMetadata?> GetAsync(byte[] hash)
    {
        try
        {
            const string sql = @"
                SELECT hash, size, weak_hash, reference_count, is_local, compression_type, compressed_size,
                       created_at, last_accessed
                FROM block_info 
                WHERE hash = @hash
                LIMIT 1";

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@hash", hash);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return new BlockMetadata
                {
                    Hash = Convert.FromHexString(reader.GetString(reader.GetOrdinal("hash"))),
                    Size = reader.GetInt32(reader.GetOrdinal("size")),
                    WeakHash = (uint)reader.GetInt64(reader.GetOrdinal("weak_hash")),
                    DeviceIdx = 1, // Default to local device
                    FolderId = "", // Not stored in block_info table
                    FileName = "", // Not stored in block_info table
                    BlockIndex = 0, // Not stored in block_info table
                    CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
                    LastAccessed = DateTime.Parse(reader.GetString(reader.GetOrdinal("last_accessed")))
                };
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get block metadata for hash: {Hash}", Convert.ToHexString(hash));
            throw;
        }
    }

    public async Task<IEnumerable<BlockMetadata>> GetByFileAsync(string folderId, string fileName)
    {
        try
        {
            const string sql = @"
                SELECT bi.hash, bi.size, bi.weak_hash, 0 as device_idx, '' as folder_id, '' as file_name, 0 as block_index,
                       bi.created_at, bi.last_accessed
                FROM block_info bi
                WHERE bi.hash IN (
                    SELECT DISTINCT substr(fm.block_hashes, 1 + (seq.value * 64), 64)
                    FROM file_metadata fm,
                         json_each('[' || replace(fm.block_hashes, ',', '],[') || ']') seq
                    WHERE fm.folder_id = @folderId AND fm.file_name = @fileName
                )
                ORDER BY bi.hash";

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@folderId", folderId);
            command.Parameters.AddWithValue("@fileName", fileName);

            var results = new List<BlockMetadata>();
            using var reader = await command.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                results.Add(new BlockMetadata
                {
                    Hash = Convert.FromHexString(reader.GetString(reader.GetOrdinal("hash"))),
                    Size = reader.GetInt32(reader.GetOrdinal("size")),
                    WeakHash = (uint)reader.GetInt64(reader.GetOrdinal("weak_hash")),
                    DeviceIdx = reader.GetInt32(reader.GetOrdinal("device_idx")),
                    FolderId = reader.GetString(reader.GetOrdinal("folder_id")),
                    FileName = reader.GetString(reader.GetOrdinal("file_name")),
                    BlockIndex = reader.GetInt32(reader.GetOrdinal("block_index")),
                    CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
                    LastAccessed = DateTime.Parse(reader.GetString(reader.GetOrdinal("last_accessed")))
                });
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get blocks for file {FolderId}/{FileName}", folderId, fileName);
            throw;
        }
    }

    public async Task SaveAsync(BlockMetadata blockMetadata)
    {
        try
        {
            const string upsertSql = @"
                INSERT INTO block_info (hash, size, weak_hash, reference_count, is_local, compression_type, compressed_size, created_at, last_accessed)
                VALUES (@hash, @size, @weakHash, 1, @isLocal, 'none', @size, @createdAt, @lastAccessed)
                ON CONFLICT(hash) 
                DO UPDATE SET
                    size = @size,
                    weak_hash = @weakHash,
                    reference_count = reference_count + 1,
                    last_accessed = @lastAccessed";

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = upsertSql;
            command.Parameters.AddWithValue("@hash", Convert.ToHexString(blockMetadata.Hash));
            command.Parameters.AddWithValue("@size", blockMetadata.Size);
            command.Parameters.AddWithValue("@weakHash", (long)blockMetadata.WeakHash);
            command.Parameters.AddWithValue("@isLocal", true);
            command.Parameters.AddWithValue("@createdAt", blockMetadata.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("@lastAccessed", blockMetadata.LastAccessed.ToString("O"));

            await command.ExecuteNonQueryAsync();

            _logger.LogDebug("Saved block metadata: {Hash} for {FolderId}/{FileName}[{BlockIndex}]", 
                Convert.ToHexString(blockMetadata.Hash), blockMetadata.FolderId, blockMetadata.FileName, blockMetadata.BlockIndex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save block metadata for {Hash}", Convert.ToHexString(blockMetadata.Hash));
            throw;
        }
    }

    public async Task DeleteAsync(byte[] hash)
    {
        try
        {
            const string sql = "DELETE FROM block_info WHERE hash = @hash";

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@hash", Convert.ToHexString(hash));

            var affected = await command.ExecuteNonQueryAsync();
            
            _logger.LogDebug("Deleted {Count} block(s) with hash: {Hash}", affected, Convert.ToHexString(hash));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete block metadata for hash: {Hash}", Convert.ToHexString(hash));
            throw;
        }
    }

    public async Task DeleteByFileAsync(string folderId, string fileName)
    {
        try
        {
            const string sql = @"
                DELETE FROM block_info WHERE hash IN (
                    SELECT DISTINCT substr(fm.block_hashes, 1 + (seq.value * 64), 64)
                    FROM file_metadata fm,
                         json_each('[' || replace(fm.block_hashes, ',', '],[') || ']') seq
                    WHERE fm.folder_id = @folderId AND fm.file_name = @fileName
                )";

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@folderId", folderId);
            command.Parameters.AddWithValue("@fileName", fileName);

            var affected = await command.ExecuteNonQueryAsync();
            
            _logger.LogDebug("Deleted {Count} block(s) for file {FolderId}/{FileName}", affected, folderId, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete blocks for file {FolderId}/{FileName}", folderId, fileName);
            throw;
        }
    }

    public async Task<IEnumerable<BlockMetadata>> FindDuplicateBlocksAsync(byte[] hash)
    {
        try
        {
            const string sql = @"
                SELECT hash, size, weak_hash, 0 as device_idx, '' as folder_id, '' as file_name, 0 as block_index,
                       created_at, last_accessed
                FROM block_info 
                WHERE hash = @hash
                ORDER BY last_accessed DESC";

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@hash", Convert.ToHexString(hash));

            var results = new List<BlockMetadata>();
            using var reader = await command.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                results.Add(new BlockMetadata
                {
                    Hash = Convert.FromHexString(reader.GetString(reader.GetOrdinal("hash"))),
                    Size = reader.GetInt32(reader.GetOrdinal("size")),
                    WeakHash = (uint)reader.GetInt64(reader.GetOrdinal("weak_hash")),
                    DeviceIdx = reader.GetInt32(reader.GetOrdinal("device_idx")),
                    FolderId = reader.GetString(reader.GetOrdinal("folder_id")),
                    FileName = reader.GetString(reader.GetOrdinal("file_name")),
                    BlockIndex = reader.GetInt32(reader.GetOrdinal("block_index")),
                    CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
                    LastAccessed = DateTime.Parse(reader.GetString(reader.GetOrdinal("last_accessed")))
                });
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to find duplicate blocks for hash: {Hash}", Convert.ToHexString(hash));
            throw;
        }
    }

    public async Task UpdateLastAccessedAsync(byte[] hash)
    {
        try
        {
            const string sql = @"
                UPDATE block_info 
                SET last_accessed = datetime('now')
                WHERE hash = @hash";

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@hash", Convert.ToHexString(hash));

            var affected = await command.ExecuteNonQueryAsync();
            
            _logger.LogDebug("Updated last_accessed for {Count} block(s) with hash: {Hash}", affected, Convert.ToHexString(hash));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update last_accessed for hash: {Hash}", Convert.ToHexString(hash));
            throw;
        }
    }

    public async Task<long> GetTotalBlockCountAsync()
    {
        try
        {
            const string sql = "SELECT COUNT(*) FROM block_info";

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = sql;

            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt64(result ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get total block count");
            throw;
        }
    }

    public async Task<long> GetTotalBlockSizeAsync()
    {
        try
        {
            const string sql = "SELECT COALESCE(SUM(size), 0) FROM block_info";

            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();

            using var command = connection.CreateCommand();
            command.CommandText = sql;

            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt64(result ?? 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get total block size");
            throw;
        }
    }

    public async Task<BlockMetadata?> GetByHashAsync(byte[] hash)
    {
        // Alias for GetAsync
        return await GetAsync(hash);
    }

    public async Task<IEnumerable<BlockMetadata>> GetBlocksByStrongHashAsync(byte[] hash)
    {
        // Same as FindDuplicateBlocksAsync for strong hash matching
        return await FindDuplicateBlocksAsync(hash);
    }

    public void Dispose()
    {
        _logger.LogDebug("BlockInfoRepository disposed");
    }
}