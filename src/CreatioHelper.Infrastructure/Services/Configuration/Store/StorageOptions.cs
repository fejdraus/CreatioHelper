namespace CreatioHelper.Infrastructure.Services.Configuration.Store;

public enum StorageProvider
{
    Sqlite,
    Postgres
}

public enum StorageMode
{
    Standalone,
    Shared
}

public class StorageOptions
{
    public const string SectionName = "Storage";

    public string Provider { get; set; } = nameof(StorageProvider.Sqlite);

    public string Mode { get; set; } = nameof(StorageMode.Standalone);

    public string ConnectionString { get; set; } = "";

    public StorageProvider ResolveProvider()
    {
        return Enum.TryParse<StorageProvider>(Provider, ignoreCase: true, out var parsed)
            ? parsed
            : StorageProvider.Sqlite;
    }

    public StorageMode ResolveMode()
    {
        return Enum.TryParse<StorageMode>(Mode, ignoreCase: true, out var parsed)
            ? parsed
            : StorageMode.Standalone;
    }
}
