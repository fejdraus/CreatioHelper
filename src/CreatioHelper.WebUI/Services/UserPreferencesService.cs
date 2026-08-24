namespace CreatioHelper.WebUI.Services;

public interface IUserPreferences
{
    bool ShowRecentEvents { get; }
    int MaxRecentEvents { get; }
    string DateFormat { get; }
    string SizeFormat { get; }
    bool NotifyOnSyncComplete { get; }
    bool NotifyOnDeviceConnect { get; }
    bool NotifyOnError { get; }
    bool NotifyOnPendingDevice { get; }

    event Action? Changed;

    Task EnsureLoadedAsync();
    Task ReloadAsync();
}

public class UserPreferencesService : IUserPreferences
{
    private readonly IBrowserStorageService _storage;
    private bool _loaded;

    public UserPreferencesService(IBrowserStorageService storage)
    {
        _storage = storage;
    }

    public bool ShowRecentEvents { get; private set; } = true;
    public int MaxRecentEvents { get; private set; } = 10;
    public string DateFormat { get; private set; } = "medium";
    public string SizeFormat { get; private set; } = "binary";
    public bool NotifyOnSyncComplete { get; private set; } = true;
    public bool NotifyOnDeviceConnect { get; private set; } = true;
    public bool NotifyOnError { get; private set; } = true;
    public bool NotifyOnPendingDevice { get; private set; } = true;

    public event Action? Changed;

    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }
        await ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        ShowRecentEvents = await _storage.GetItemAsync<bool?>("gui_showRecentEvents") ?? true;
        MaxRecentEvents = await _storage.GetItemAsync<int?>("gui_maxRecentEvents") ?? 10;
        DateFormat = await _storage.GetItemAsync<string>("gui_dateFormat") ?? "medium";
        SizeFormat = await _storage.GetItemAsync<string>("gui_sizeFormat") ?? "binary";
        NotifyOnSyncComplete = await _storage.GetItemAsync<bool?>("gui_notifyOnSyncComplete") ?? true;
        NotifyOnDeviceConnect = await _storage.GetItemAsync<bool?>("gui_notifyOnDeviceConnect") ?? true;
        NotifyOnError = await _storage.GetItemAsync<bool?>("gui_notifyOnError") ?? true;
        NotifyOnPendingDevice = await _storage.GetItemAsync<bool?>("gui_notifyOnPendingDevice") ?? true;

        ByteFormatter.UseBinary = SizeFormat != "decimal";
        DateFormatter.CurrentFormat = DateFormat;

        _loaded = true;
        Changed?.Invoke();
    }
}
