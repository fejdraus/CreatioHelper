namespace CreatioHelper.Infrastructure.Services.DeviceManagement;

public class ClusterKeyProvider
{
    private volatile string? _key;

    public string? Key => _key;

    public bool HasKey => !string.IsNullOrWhiteSpace(_key);

    public void Set(string key)
    {
        _key = key;
    }
}
