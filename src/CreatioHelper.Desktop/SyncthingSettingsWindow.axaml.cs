using Avalonia.Controls;
using Avalonia.Interactivity;
using CreatioHelper.ViewModels;

namespace CreatioHelper;

public partial class SyncthingSettingsWindow : Window
{
    private readonly SyncthingSettingsViewModel _viewModel;

    public SyncthingSettingsWindow()
    {
        InitializeComponent();
        _viewModel = new SyncthingSettingsViewModel();
        DataContext = _viewModel;
    }

    public SyncthingSettingsWindow(bool enableFileCopySync, bool useSyncthing, string? apiUrl, string? apiKey)
    {
        InitializeComponent();
        _viewModel = new SyncthingSettingsViewModel
        {
            EnableFileCopySynchronization = enableFileCopySync,
            UseSyncthingForSync = useSyncthing,
            NoSynchronization = !enableFileCopySync && !useSyncthing,
            SyncthingApiUrl = apiUrl,
            SyncthingApiKey = apiKey
        };
        DataContext = _viewModel;
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(new SyncthingSettingsResult
        {
            EnableFileCopySynchronization = _viewModel.EnableFileCopySynchronization,
            UseSyncthingForSync = _viewModel.UseSyncthingForSync,
            SyncthingApiUrl = _viewModel.SyncthingApiUrl,
            SyncthingApiKey = _viewModel.SyncthingApiKey
        });
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}

public class SyncthingSettingsResult
{
    public bool EnableFileCopySynchronization { get; set; }
    public bool UseSyncthingForSync { get; set; }
    public string? SyncthingApiUrl { get; set; }
    public string? SyncthingApiKey { get; set; }
}
