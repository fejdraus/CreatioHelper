using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CreatioHelper.Infrastructure.Services.Sync.Database;

/// <summary>
/// SQLite implementation of folder configuration repository
/// </summary>
public class FolderConfigRepository : IFolderConfigRepository
{
    private readonly Func<SqliteConnection> _getConnection;
    private readonly ILogger _logger;

    public FolderConfigRepository(Func<SqliteConnection> getConnection, ILogger logger)
    {
        _getConnection = getConnection;
        _logger = logger;
    }

    public async Task<SyncFolder?> GetAsync(string folderId)
    {
        const string sql = @"
            SELECT folder_id, folder_label, filesystem_type, path, type, devices, rescan_interval_s,
                   fs_watcher_enabled, fs_watcher_delay_s, ignore_perms, auto_normalize_unicode, min_disk_free,
                   versioning, copy_ownership_from_parent, mod_time_window_s, max_conflicts, disable_sparse_files,
                   disable_temp_indexes, paused, weak_hash_threshold_pct, marker_name, copy_range_method,
                   case_sensitive_fs, junctioned_as_directory, sync_ownership, send_ownership, sync_xattrs,
                   send_xattrs, file_count, total_size, last_scan
            FROM folder_config 
            WHERE folder_id = @folderId";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@folderId", folderId);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return MapFromReader(reader);
        }

        return null;
    }

    public async Task<IEnumerable<SyncFolder>> GetAllAsync()
    {
        const string sql = @"
            SELECT folder_id, folder_label, filesystem_type, path, type, devices, rescan_interval_s,
                   fs_watcher_enabled, fs_watcher_delay_s, ignore_perms, auto_normalize_unicode, min_disk_free,
                   versioning, copy_ownership_from_parent, mod_time_window_s, max_conflicts, disable_sparse_files,
                   disable_temp_indexes, paused, weak_hash_threshold_pct, marker_name, copy_range_method,
                   case_sensitive_fs, junctioned_as_directory, sync_ownership, send_ownership, sync_xattrs,
                   send_xattrs, file_count, total_size, last_scan
            FROM folder_config
            ORDER BY folder_label";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;

        var results = new List<SyncFolder>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(MapFromReader(reader));
        }

        return results;
    }

    public async Task UpsertAsync(SyncFolder folder)
    {
        const string sql = @"
            INSERT OR REPLACE INTO folder_config (
                folder_id, folder_label, filesystem_type, path, type, devices, rescan_interval_s,
                fs_watcher_enabled, fs_watcher_delay_s, ignore_perms, auto_normalize_unicode, min_disk_free,
                versioning, copy_ownership_from_parent, mod_time_window_s, max_conflicts, disable_sparse_files,
                disable_temp_indexes, paused, weak_hash_threshold_pct, marker_name, copy_range_method,
                case_sensitive_fs, junctioned_as_directory, sync_ownership, send_ownership, sync_xattrs,
                send_xattrs, file_count, total_size, last_scan
            ) VALUES (
                @folderId, @folderLabel, @filesystemType, @path, @type, @devices, @rescanIntervalS,
                @fsWatcherEnabled, @fsWatcherDelayS, @ignorePerms, @autoNormalizeUnicode, @minDiskFree,
                @versioning, @copyOwnershipFromParent, @modTimeWindowS, @maxConflicts, @disableSparseFiles,
                @disableTempIndexes, @paused, @weakHashThresholdPct, @markerName, @copyRangeMethod,
                @caseSensitiveFs, @junctionedAsDirectory, @syncOwnership, @sendOwnership, @syncXattrs,
                @sendXattrs, @fileCount, @totalSize, @lastScan
            )";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        AddParameters(command, folder);

        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(string folderId)
    {
        const string sql = "DELETE FROM folder_config WHERE folder_id = @folderId";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@folderId", folderId);

        await command.ExecuteNonQueryAsync();
    }

    public async Task<IEnumerable<SyncFolder>> GetFoldersSharedWithDeviceAsync(string deviceId)
    {
        // For now, return all folders (simplified implementation)
        // In a full implementation, this would parse the devices JSON field
        return await GetAllAsync();
    }

    public async Task UpdateStatisticsAsync(string folderId, long fileCount, long totalSize, DateTime lastScan)
    {
        const string sql = @"
            UPDATE folder_config 
            SET file_count = @fileCount, total_size = @totalSize, last_scan = @lastScan
            WHERE folder_id = @folderId";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@folderId", folderId);
        command.Parameters.AddWithValue("@fileCount", fileCount);
        command.Parameters.AddWithValue("@totalSize", totalSize);
        command.Parameters.AddWithValue("@lastScan", lastScan.ToString("O"));

        await command.ExecuteNonQueryAsync();
    }

    private static SyncFolder MapFromReader(SqliteDataReader reader)
    {
        var folder = new SyncFolder(
            reader.GetString(reader.GetOrdinal("folder_id")),
            reader.GetString(reader.GetOrdinal("folder_label")),
            reader.GetString(reader.GetOrdinal("path")),
            reader.GetString(reader.GetOrdinal("type")),
            reader.GetInt32(reader.GetOrdinal("rescan_interval_s")),
            reader.GetBoolean(reader.GetOrdinal("fs_watcher_enabled")),
            reader.GetInt32(reader.GetOrdinal("fs_watcher_delay_s")),
            reader.GetBoolean(reader.GetOrdinal("ignore_perms")),
            reader.GetBoolean(reader.GetOrdinal("auto_normalize_unicode")),
            reader.GetString(reader.GetOrdinal("min_disk_free")),
            reader.GetBoolean(reader.GetOrdinal("copy_ownership_from_parent")),
            reader.GetInt32(reader.GetOrdinal("mod_time_window_s")),
            reader.GetInt32(reader.GetOrdinal("max_conflicts")),
            reader.GetBoolean(reader.GetOrdinal("disable_sparse_files")),
            reader.GetBoolean(reader.GetOrdinal("disable_temp_indexes")),
            reader.GetBoolean(reader.GetOrdinal("paused")),
            reader.GetInt32(reader.GetOrdinal("weak_hash_threshold_pct")),
            reader.GetString(reader.GetOrdinal("marker_name")),
            reader.GetString(reader.GetOrdinal("copy_range_method")),
            reader.GetBoolean(reader.GetOrdinal("case_sensitive_fs")),
            reader.GetBoolean(reader.GetOrdinal("junctioned_as_directory")),
            reader.GetBoolean(reader.GetOrdinal("sync_ownership")),
            reader.GetBoolean(reader.GetOrdinal("send_ownership")),
            reader.GetBoolean(reader.GetOrdinal("sync_xattrs")),
            reader.GetBoolean(reader.GetOrdinal("send_xattrs"))
        );

        // Deserialize devices
        var devicesOrdinal = reader.GetOrdinal("devices");
        var devicesJson = reader.IsDBNull(devicesOrdinal) ? null : reader.GetString(devicesOrdinal);
        if (!string.IsNullOrEmpty(devicesJson))
        {
            folder.Devices = JsonSerializer.Deserialize<List<string>>(devicesJson) ?? new List<string>();
        }

        // Deserialize versioning config
        var versioningOrdinal = reader.GetOrdinal("versioning");
        var versioningJson = reader.IsDBNull(versioningOrdinal) ? null : reader.GetString(versioningOrdinal);
        if (!string.IsNullOrEmpty(versioningJson))
        {
            folder.SetVersioning(JsonSerializer.Deserialize<VersioningConfiguration>(versioningJson) ?? new VersioningConfiguration());
        }

        // Note: LastScan is set in constructor and can be updated via UpdateLastScan() method

        return folder;
    }

    private static void AddParameters(SqliteCommand command, SyncFolder folder)
    {
        command.Parameters.AddWithValue("@folderId", folder.Id);
        command.Parameters.AddWithValue("@folderLabel", folder.Label);
        command.Parameters.AddWithValue("@filesystemType", "basic");
        command.Parameters.AddWithValue("@path", folder.Path);
        command.Parameters.AddWithValue("@type", folder.Type);
        command.Parameters.AddWithValue("@devices", JsonSerializer.Serialize(folder.Devices));
        command.Parameters.AddWithValue("@rescanIntervalS", folder.RescanIntervalS);
        command.Parameters.AddWithValue("@fsWatcherEnabled", folder.FSWatcherEnabled);
        command.Parameters.AddWithValue("@fsWatcherDelayS", folder.FSWatcherDelayS);
        command.Parameters.AddWithValue("@ignorePerms", folder.IgnorePerms);
        command.Parameters.AddWithValue("@autoNormalizeUnicode", folder.AutoNormalizeUnicode);
        command.Parameters.AddWithValue("@minDiskFree", folder.MinDiskFree);
        command.Parameters.AddWithValue("@versioning", folder.Versioning != null ? JsonSerializer.Serialize(folder.Versioning) : (object)DBNull.Value);
        command.Parameters.AddWithValue("@copyOwnershipFromParent", folder.CopyOwnershipFromParent);
        command.Parameters.AddWithValue("@modTimeWindowS", folder.ModTimeWindowS);
        command.Parameters.AddWithValue("@maxConflicts", folder.MaxConflicts);
        command.Parameters.AddWithValue("@disableSparseFiles", folder.DisableSparseFiles);
        command.Parameters.AddWithValue("@disableTempIndexes", folder.DisableTempIndexes);
        command.Parameters.AddWithValue("@paused", folder.Paused);
        command.Parameters.AddWithValue("@weakHashThresholdPct", folder.WeakHashThresholdPct);
        command.Parameters.AddWithValue("@markerName", folder.MarkerName);
        command.Parameters.AddWithValue("@copyRangeMethod", folder.CopyRangeMethod);
        command.Parameters.AddWithValue("@caseSensitiveFs", folder.CaseSensitiveFS);
        command.Parameters.AddWithValue("@junctionedAsDirectory", folder.JunctionedAsDirectory);
        command.Parameters.AddWithValue("@syncOwnership", folder.SyncOwnership);
        command.Parameters.AddWithValue("@sendOwnership", folder.SendOwnership);
        command.Parameters.AddWithValue("@syncXattrs", folder.SyncXattrs);
        command.Parameters.AddWithValue("@sendXattrs", folder.SendXattrs);
        command.Parameters.AddWithValue("@fileCount", 0L);  // Will be updated separately
        command.Parameters.AddWithValue("@totalSize", 0L);  // Will be updated separately
        command.Parameters.AddWithValue("@lastScan", folder.LastScan?.ToString("O") ?? (object)DBNull.Value);
    }
    
    public void Dispose()
    {
        // No resources to dispose for this implementation
        _logger.LogDebug("FolderConfigRepository disposed");
    }
}