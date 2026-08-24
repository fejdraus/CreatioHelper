using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Infrastructure.Services.Configuration.Store;

public interface IConfigurationStore
{
    Task InitializeAsync();

    Task<bool> IsEmptyAsync();

    Task<ConfigXml> LoadFullAsync();

    Task ImportAsync(ConfigXml config);

    Task<IReadOnlyList<ConfigXmlDevice>> GetDevicesAsync();

    Task<long> GetMaxDeviceVersionAsync(string deviceId);

    Task UpsertDeviceAsync(ConfigXmlDevice device);

    Task<bool> DeleteDeviceAsync(string deviceId);

    Task<IReadOnlyList<ConfigXmlFolder>> GetFoldersAsync();

    Task UpsertFolderAsync(ConfigXmlFolder folder);

    Task<bool> DeleteFolderAsync(string folderId);

    Task<IReadOnlyList<ConfigXmlIgnoredDevice>> GetIgnoredDevicesAsync();

    Task AddIgnoredDeviceAsync(ConfigXmlIgnoredDevice tombstone);

    Task<bool> RemoveIgnoredDeviceAsync(string deviceId);

    Task<int> PruneIgnoredDevicesAsync(DateTime olderThanUtc);

    Task<ConfigXmlOptions?> GetOptionsAsync();

    Task SetOptionsAsync(ConfigXmlOptions options);

    Task<string?> GetClusterKeyAsync();

    Task SetClusterKeyAsync(string key);

    Task<ConfigXmlGui?> GetGuiAsync();

    Task SetGuiAsync(ConfigXmlGui gui);

    Task<ConfigXmlLdap?> GetLdapAsync();

    Task SetLdapAsync(ConfigXmlLdap? ldap);

    Task<ConfigXmlDefaults?> GetDefaultsAsync();

    Task SetDefaultsAsync(ConfigXmlDefaults? defaults);

    Task<int> GetVersionAsync();

    Task SetVersionAsync(int version);
}
