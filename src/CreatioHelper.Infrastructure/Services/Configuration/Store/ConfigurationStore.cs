using System.Data.Common;
using System.Text.Json;
using CreatioHelper.Domain.Entities;
using Dapper;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Configuration.Store;

public class ConfigurationStore : IConfigurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ConfigDatabaseInitializer _initializer;
    private readonly ILogger<ConfigurationStore> _logger;

    public ConfigurationStore(
        IDbConnectionFactory connectionFactory,
        ConfigDatabaseInitializer initializer,
        ILogger<ConfigurationStore> logger)
    {
        _connectionFactory = connectionFactory;
        _initializer = initializer;
        _logger = logger;
    }

    public Task InitializeAsync()
    {
        _initializer.Initialize();
        return Task.CompletedTask;
    }

    private async Task<DbConnection> OpenAsync()
    {
        var connection = (DbConnection)_connectionFactory.Create();
        await connection.OpenAsync();
        return connection;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static T? Deserialize<T>(string? json) =>
        string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, JsonOptions);

    private static DateTime? ToNullable(DateTime value) => value == default ? null : value;

    #region Lifecycle / bulk

    public async Task<bool> IsEmptyAsync()
    {
        await using var connection = await OpenAsync();
        var count = await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM config_singletons WHERE key = 'version'");
        return count == 0;
    }

    public async Task<ConfigXml> LoadFullAsync()
    {
        var config = new ConfigXml
        {
            Version = await GetVersionAsync(),
            Devices = (await GetDevicesAsync()).ToList(),
            Folders = (await GetFoldersAsync()).ToList(),
            Gui = await GetGuiAsync() ?? new ConfigXmlGui(),
            Ldap = await GetLdapAsync(),
            Options = await GetOptionsAsync() ?? new ConfigXmlOptions(),
            Defaults = await GetDefaultsAsync()
        };

        var tombstones = await GetIgnoredDevicesAsync();
        if (tombstones.Count > 0)
        {
            config.RemoteIgnoredDevices = new ConfigXmlRemoteIgnoredDevices
            {
                Devices = tombstones.ToList()
            };
        }

        return config;
    }

    public async Task ImportAsync(ConfigXml config)
    {
        foreach (var device in config.Devices)
        {
            await UpsertDeviceAsync(device);
        }

        foreach (var folder in config.Folders)
        {
            await UpsertFolderAsync(folder);
        }

        if (config.RemoteIgnoredDevices?.Devices != null)
        {
            foreach (var tombstone in config.RemoteIgnoredDevices.Devices)
            {
                await AddIgnoredDeviceAsync(tombstone);
            }
        }

        await SetOptionsAsync(config.Options);
        await SetGuiAsync(config.Gui);
        await SetLdapAsync(config.Ldap);
        await SetDefaultsAsync(config.Defaults);
        await SetVersionAsync(config.Version);
    }

    #endregion

    #region Devices

    public async Task<IReadOnlyList<ConfigXmlDevice>> GetDevicesAsync()
    {
        await using var connection = await OpenAsync();
        var rows = (await connection.QueryAsync<(string Data, long StateVersion)>(
            "SELECT data AS Data, state_version AS StateVersion FROM config_devices")).ToList();
        var devices = new List<ConfigXmlDevice>();
        foreach (var row in rows)
        {
            var device = Deserialize<ConfigXmlDevice>(row.Data);
            if (device == null)
            {
                continue;
            }
            device.StateVersion = row.StateVersion;
            devices.Add(device);
        }
        return devices;
    }

    public async Task<long> GetMaxDeviceVersionAsync(string deviceId)
    {
        await using var connection = await OpenAsync();
        var value = await connection.ExecuteScalarAsync<long?>(
            @"SELECT MAX(v) FROM (
                  SELECT state_version AS v FROM config_devices WHERE device_id = @deviceId
                  UNION ALL
                  SELECT state_version AS v FROM config_ignored_devices WHERE device_id = @deviceId
              ) AS t",
            new { deviceId });
        return value ?? 0;
    }

    public async Task UpsertDeviceAsync(ConfigXmlDevice device)
    {
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            @"INSERT INTO config_devices (device_id, name, admitted_at, state_version, paused, introducer, data)
              VALUES (@DeviceId, @Name, @AdmittedAt, @StateVersion, @Paused, @Introducer, @Data)
              ON CONFLICT (device_id) DO UPDATE SET
                  name = excluded.name,
                  admitted_at = excluded.admitted_at,
                  state_version = CASE WHEN excluded.state_version > config_devices.state_version
                                       THEN excluded.state_version ELSE config_devices.state_version END,
                  paused = excluded.paused,
                  introducer = excluded.introducer,
                  data = excluded.data",
            new
            {
                DeviceId = device.Id,
                Name = device.Name,
                AdmittedAt = ToNullable(device.AdmittedAt),
                StateVersion = device.StateVersion,
                device.Paused,
                device.Introducer,
                Data = Serialize(device)
            });
    }

    public async Task<bool> DeleteDeviceAsync(string deviceId)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM config_folder_devices WHERE device_id = @deviceId", new { deviceId }, transaction);
        var affected = await connection.ExecuteAsync(
            "DELETE FROM config_devices WHERE device_id = @deviceId", new { deviceId }, transaction);
        await transaction.CommitAsync();
        return affected > 0;
    }

    #endregion

    #region Folders

    public async Task<IReadOnlyList<ConfigXmlFolder>> GetFoldersAsync()
    {
        await using var connection = await OpenAsync();
        var rows = (await connection.QueryAsync<(string FolderId, string Data)>(
            "SELECT folder_id AS FolderId, data AS Data FROM config_folders")).ToList();

        var folders = new List<ConfigXmlFolder>();
        foreach (var row in rows)
        {
            var folder = Deserialize<ConfigXmlFolder>(row.Data);
            if (folder == null)
            {
                continue;
            }

            folder.Devices = (await connection.QueryAsync<ConfigXmlFolderDevice>(
                @"SELECT device_id AS Id, introduced_by AS IntroducedBy, encryption_password AS EncryptionPassword
                  FROM config_folder_devices WHERE folder_id = @folderId",
                new { folderId = row.FolderId })).ToList();

            folders.Add(folder);
        }

        return folders;
    }

    public async Task UpsertFolderAsync(ConfigXmlFolder folder)
    {
        var devices = folder.Devices ?? new List<ConfigXmlFolderDevice>();
        var savedDevices = folder.Devices;
        folder.Devices = new List<ConfigXmlFolderDevice>();
        string data;
        try
        {
            data = Serialize(folder);
        }
        finally
        {
            folder.Devices = savedDevices ?? new List<ConfigXmlFolderDevice>();
        }

        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await connection.ExecuteAsync(
            @"INSERT INTO config_folders (folder_id, label, path, type, paused, data)
              VALUES (@FolderId, @Label, @Path, @Type, @Paused, @Data)
              ON CONFLICT (folder_id) DO UPDATE SET
                  label = excluded.label,
                  path = excluded.path,
                  type = excluded.type,
                  paused = excluded.paused,
                  data = excluded.data",
            new { FolderId = folder.Id, folder.Label, folder.Path, folder.Type, folder.Paused, Data = data },
            transaction);

        await connection.ExecuteAsync(
            "DELETE FROM config_folder_devices WHERE folder_id = @folderId",
            new { folderId = folder.Id }, transaction);

        if (devices.Count > 0)
        {
            await connection.ExecuteAsync(
                @"INSERT INTO config_folder_devices (folder_id, device_id, introduced_by, encryption_password)
                  VALUES (@FolderId, @DeviceId, @IntroducedBy, @EncryptionPassword)",
                devices.Select(d => new
                {
                    FolderId = folder.Id,
                    DeviceId = d.Id,
                    d.IntroducedBy,
                    d.EncryptionPassword
                }),
                transaction);
        }

        await transaction.CommitAsync();
    }

    public async Task<bool> DeleteFolderAsync(string folderId)
    {
        await using var connection = await OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync(
            "DELETE FROM config_folder_devices WHERE folder_id = @folderId", new { folderId }, transaction);
        var affected = await connection.ExecuteAsync(
            "DELETE FROM config_folders WHERE folder_id = @folderId", new { folderId }, transaction);
        await transaction.CommitAsync();
        return affected > 0;
    }

    #endregion

    #region Ignored devices (tombstones)

    public async Task<IReadOnlyList<ConfigXmlIgnoredDevice>> GetIgnoredDevicesAsync()
    {
        await using var connection = await OpenAsync();
        var rows = await connection.QueryAsync<ConfigXmlIgnoredDevice>(
            @"SELECT device_id AS Id, name AS Name, deleted_at AS Time, address AS Address,
                     state_version AS StateVersion
              FROM config_ignored_devices");
        return rows.ToList();
    }

    public async Task AddIgnoredDeviceAsync(ConfigXmlIgnoredDevice tombstone)
    {
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            @"INSERT INTO config_ignored_devices (device_id, name, deleted_at, address, state_version)
              VALUES (@Id, @Name, @Time, @Address, @StateVersion)
              ON CONFLICT (device_id) DO UPDATE SET
                  name = excluded.name,
                  deleted_at = CASE WHEN excluded.deleted_at > config_ignored_devices.deleted_at
                                    THEN excluded.deleted_at ELSE config_ignored_devices.deleted_at END,
                  state_version = CASE WHEN excluded.state_version > config_ignored_devices.state_version
                                       THEN excluded.state_version ELSE config_ignored_devices.state_version END,
                  address = excluded.address",
            new { tombstone.Id, tombstone.Name, tombstone.Time, tombstone.Address, tombstone.StateVersion });
    }

    public async Task<bool> RemoveIgnoredDeviceAsync(string deviceId)
    {
        await using var connection = await OpenAsync();
        var affected = await connection.ExecuteAsync(
            "DELETE FROM config_ignored_devices WHERE device_id = @deviceId", new { deviceId });
        return affected > 0;
    }

    public async Task<int> PruneIgnoredDevicesAsync(DateTime olderThanUtc)
    {
        await using var connection = await OpenAsync();
        return await connection.ExecuteAsync(
            "DELETE FROM config_ignored_devices WHERE deleted_at < @olderThanUtc", new { olderThanUtc });
    }

    #endregion

    #region Singletons

    private async Task<T?> GetSingletonAsync<T>(string key)
    {
        await using var connection = await OpenAsync();
        var json = await connection.ExecuteScalarAsync<string?>(
            "SELECT data FROM config_singletons WHERE key = @key", new { key });
        return Deserialize<T>(json);
    }

    private async Task SetSingletonAsync<T>(string key, T value)
    {
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(
            @"INSERT INTO config_singletons (key, data) VALUES (@key, @data)
              ON CONFLICT (key) DO UPDATE SET data = excluded.data",
            new { key, data = Serialize(value) });
    }

    public Task<ConfigXmlOptions?> GetOptionsAsync() => GetSingletonAsync<ConfigXmlOptions>("options");

    public Task SetOptionsAsync(ConfigXmlOptions options) => SetSingletonAsync("options", options);

    public Task<ConfigXmlGui?> GetGuiAsync() => GetSingletonAsync<ConfigXmlGui>("gui");

    public Task SetGuiAsync(ConfigXmlGui gui) => SetSingletonAsync("gui", gui);

    public Task<ConfigXmlLdap?> GetLdapAsync() => GetSingletonAsync<ConfigXmlLdap>("ldap");

    public Task SetLdapAsync(ConfigXmlLdap? ldap) => SetSingletonAsync("ldap", ldap);

    public Task<ConfigXmlDefaults?> GetDefaultsAsync() => GetSingletonAsync<ConfigXmlDefaults>("defaults");

    public Task SetDefaultsAsync(ConfigXmlDefaults? defaults) => SetSingletonAsync("defaults", defaults);

    public async Task<int> GetVersionAsync()
    {
        var value = await GetSingletonAsync<int?>("version");
        return value ?? 0;
    }

    public Task SetVersionAsync(int version) => SetSingletonAsync("version", version);

    #endregion
}
