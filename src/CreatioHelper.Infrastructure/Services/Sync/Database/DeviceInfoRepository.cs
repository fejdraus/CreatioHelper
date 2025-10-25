using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace CreatioHelper.Infrastructure.Services.Sync.Database;

/// <summary>
/// SQLite implementation of device info repository
/// </summary>
public class DeviceInfoRepository : IDeviceInfoRepository
{
    private readonly Func<SqliteConnection> _getConnection;
    private readonly ILogger _logger;

    public DeviceInfoRepository(Func<SqliteConnection> getConnection, ILogger logger)
    {
        _getConnection = getConnection;
        _logger = logger;
    }

    public async Task<SyncDevice?> GetAsync(string deviceId)
    {
        const string sql = @"
            SELECT device_id, device_name, addresses, compression, introducer, skip_introduction_removals,
                   introduced_by, paused, allowed_networks, auto_accept_folders, max_send_kbps, max_recv_kbps,
                   ignored_folders, pending_folders, max_request_kib, untrusted, remote_gui_port, num_connections,
                   certificate_name, last_seen, bytes_received, bytes_sent, last_activity
            FROM device_info 
            WHERE device_id = @deviceId";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@deviceId", deviceId);

        using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return MapFromReader(reader);
        }

        return null;
    }

    public async Task<IEnumerable<SyncDevice>> GetAllAsync()
    {
        const string sql = @"
            SELECT device_id, device_name, addresses, compression, introducer, skip_introduction_removals,
                   introduced_by, paused, allowed_networks, auto_accept_folders, max_send_kbps, max_recv_kbps,
                   ignored_folders, pending_folders, max_request_kib, untrusted, remote_gui_port, num_connections,
                   certificate_name, last_seen, bytes_received, bytes_sent, last_activity
            FROM device_info
            ORDER BY device_name";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;

        var results = new List<SyncDevice>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(MapFromReader(reader));
        }

        return results;
    }

    public async Task UpsertAsync(SyncDevice device)
    {
        const string sql = @"
            INSERT OR REPLACE INTO device_info (
                device_id, device_name, addresses, compression, introducer, skip_introduction_removals,
                introduced_by, paused, allowed_networks, auto_accept_folders, max_send_kbps, max_recv_kbps,
                ignored_folders, pending_folders, max_request_kib, untrusted, remote_gui_port, num_connections,
                certificate_name, last_seen, bytes_received, bytes_sent, last_activity
            ) VALUES (
                @deviceId, @deviceName, @addresses, @compression, @introducer, @skipIntroductionRemovals,
                @introducedBy, @paused, @allowedNetworks, @autoAcceptFolders, @maxSendKbps, @maxRecvKbps,
                @ignoredFolders, @pendingFolders, @maxRequestKib, @untrusted, @remoteGuiPort, @numConnections,
                @certificateName, @lastSeen, @bytesReceived, @bytesSent, @lastActivity
            )";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        AddParameters(command, device);

        await command.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(string deviceId)
    {
        const string sql = "DELETE FROM device_info WHERE device_id = @deviceId";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@deviceId", deviceId);

        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdateLastSeenAsync(string deviceId, DateTime lastSeen)
    {
        const string sql = "UPDATE device_info SET last_seen = @lastSeen WHERE device_id = @deviceId";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@deviceId", deviceId);
        command.Parameters.AddWithValue("@lastSeen", lastSeen.ToString("O"));

        await command.ExecuteNonQueryAsync();
    }

    public async Task<IEnumerable<SyncDevice>> GetDevicesForFolderAsync(string folderId)
    {
        // This requires a more complex query that joins with folder configurations
        // For now, return all devices (simplified implementation)
        return await GetAllAsync();
    }

    public async Task UpdateStatisticsAsync(string deviceId, long bytesReceived, long bytesSent, DateTime lastActivity)
    {
        const string sql = @"
            UPDATE device_info 
            SET bytes_received = @bytesReceived, bytes_sent = @bytesSent, last_activity = @lastActivity
            WHERE device_id = @deviceId";

        using var command = _getConnection().CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@deviceId", deviceId);
        command.Parameters.AddWithValue("@bytesReceived", bytesReceived);
        command.Parameters.AddWithValue("@bytesSent", bytesSent);
        command.Parameters.AddWithValue("@lastActivity", lastActivity.ToString("O"));

        await command.ExecuteNonQueryAsync();
    }

    private static SyncDevice MapFromReader(SqliteDataReader reader)
    {
        // Create device with internal constructor
        var device = new SyncDevice(
            reader.GetString(reader.GetOrdinal("device_id")),
            reader.GetString(reader.GetOrdinal("device_name")),
            reader.IsDBNull(reader.GetOrdinal("compression")) ? "metadata" : reader.GetString(reader.GetOrdinal("compression")),
            reader.GetBoolean(reader.GetOrdinal("introducer")),
            reader.GetBoolean(reader.GetOrdinal("skip_introduction_removals")),
            reader.IsDBNull(reader.GetOrdinal("introduced_by")) ? string.Empty : reader.GetString(reader.GetOrdinal("introduced_by")),
            reader.GetBoolean(reader.GetOrdinal("paused")),
            reader.GetBoolean(reader.GetOrdinal("auto_accept_folders")),
            reader.GetInt32(reader.GetOrdinal("max_send_kbps")),
            reader.GetInt32(reader.GetOrdinal("max_recv_kbps")),
            reader.GetInt32(reader.GetOrdinal("max_request_kib")),
            reader.GetBoolean(reader.GetOrdinal("untrusted")),
            reader.GetInt32(reader.GetOrdinal("remote_gui_port")),
            reader.GetInt32(reader.GetOrdinal("num_connections")),
            reader.IsDBNull(reader.GetOrdinal("certificate_name")) ? string.Empty : reader.GetString(reader.GetOrdinal("certificate_name"))
        );

        // Deserialize arrays
        var addressesOrdinal = reader.GetOrdinal("addresses");
        var addressesJson = reader.IsDBNull(addressesOrdinal) ? null : reader.GetString(addressesOrdinal);
        if (!string.IsNullOrEmpty(addressesJson))
        {
            device.Addresses = JsonSerializer.Deserialize<List<string>>(addressesJson) ?? new List<string>();
        }

        var allowedNetworksOrdinal = reader.GetOrdinal("allowed_networks");
        var allowedNetworksJson = reader.IsDBNull(allowedNetworksOrdinal) ? null : reader.GetString(allowedNetworksOrdinal);
        if (!string.IsNullOrEmpty(allowedNetworksJson))
        {
            device.AllowedNetworks = JsonSerializer.Deserialize<List<string>>(allowedNetworksJson) ?? new List<string>();
        }

        var ignoredFoldersOrdinal = reader.GetOrdinal("ignored_folders");
        var ignoredFoldersJson = reader.IsDBNull(ignoredFoldersOrdinal) ? null : reader.GetString(ignoredFoldersOrdinal);
        if (!string.IsNullOrEmpty(ignoredFoldersJson))
        {
            device.IgnoredFolders = JsonSerializer.Deserialize<List<string>>(ignoredFoldersJson) ?? new List<string>();
        }

        var pendingFoldersOrdinal = reader.GetOrdinal("pending_folders");
        var pendingFoldersJson = reader.IsDBNull(pendingFoldersOrdinal) ? null : reader.GetString(pendingFoldersOrdinal);
        if (!string.IsNullOrEmpty(pendingFoldersJson))
        {
            device.PendingFolders = JsonSerializer.Deserialize<List<string>>(pendingFoldersJson) ?? new List<string>();
        }

        // Set runtime properties
        var lastSeenOrdinal = reader.GetOrdinal("last_seen");
        if (!reader.IsDBNull(lastSeenOrdinal))
        {
            device.LastSeen = DateTime.Parse(reader.GetString(lastSeenOrdinal));
        }

        var lastActivityOrdinal = reader.GetOrdinal("last_activity");
        if (!reader.IsDBNull(lastActivityOrdinal))
        {
            device.LastActivity = DateTime.Parse(reader.GetString(lastActivityOrdinal));
        }

        return device;
    }

    private static void AddParameters(SqliteCommand command, SyncDevice device)
    {
        command.Parameters.AddWithValue("@deviceId", device.DeviceId);
        command.Parameters.AddWithValue("@deviceName", device.DeviceName);
        command.Parameters.AddWithValue("@addresses", JsonSerializer.Serialize(device.Addresses));
        command.Parameters.AddWithValue("@compression", device.Compression);
        command.Parameters.AddWithValue("@introducer", device.Introducer);
        command.Parameters.AddWithValue("@skipIntroductionRemovals", device.SkipIntroductionRemovals);
        command.Parameters.AddWithValue("@introducedBy", device.IntroducedBy);
        command.Parameters.AddWithValue("@paused", device.Paused);
        command.Parameters.AddWithValue("@allowedNetworks", JsonSerializer.Serialize(device.AllowedNetworks));
        command.Parameters.AddWithValue("@autoAcceptFolders", device.AutoAcceptFolders);
        command.Parameters.AddWithValue("@maxSendKbps", device.MaxSendKbps);
        command.Parameters.AddWithValue("@maxRecvKbps", device.MaxRecvKbps);
        command.Parameters.AddWithValue("@ignoredFolders", JsonSerializer.Serialize(device.IgnoredFolders));
        command.Parameters.AddWithValue("@pendingFolders", JsonSerializer.Serialize(device.PendingFolders));
        command.Parameters.AddWithValue("@maxRequestKib", device.MaxRequestKib);
        command.Parameters.AddWithValue("@untrusted", device.Untrusted);
        command.Parameters.AddWithValue("@remoteGuiPort", device.RemoteGUIPort);
        command.Parameters.AddWithValue("@numConnections", device.NumConnections);
        command.Parameters.AddWithValue("@certificateName", device.CertificateName);
        command.Parameters.AddWithValue("@lastSeen", device.LastSeen?.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@bytesReceived", 0L);  // Will be updated separately
        command.Parameters.AddWithValue("@bytesSent", 0L);     // Will be updated separately  
        command.Parameters.AddWithValue("@lastActivity", device.LastActivity?.ToString("O") ?? (object)DBNull.Value);
    }
    
    public void Dispose()
    {
        // No resources to dispose for this implementation
        _logger.LogDebug("DeviceInfoRepository disposed");
    }
}