using Avalonia.Controls;
using Avalonia.Interactivity;
using CreatioHelper.Domain.Enums;
using CreatioHelper.ViewModels;

namespace CreatioHelper;

public partial class SettingsWindow : Window
{
    private readonly SettingsWindowViewModel _viewModel;

    public SettingsWindow()
    {
        InitializeComponent();
        _viewModel = new SettingsWindowViewModel();
        DataContext = _viewModel;
    }

    public SettingsWindow(bool updateCheckEnabled, UpdateChannel updateChannel, string currentVersion)
    {
        InitializeComponent();
        _viewModel = new SettingsWindowViewModel
        {
            UpdateCheckEnabled = updateCheckEnabled,
            UpdateChannel = updateChannel,
            CurrentVersion = currentVersion,
        };
        DataContext = _viewModel;
    }

    private void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(new SettingsResult
        {
            UpdateCheckEnabled = _viewModel.UpdateCheckEnabled,
            UpdateChannel = _viewModel.UpdateChannel,
        });
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }
}

public class SettingsResult
{
    public bool UpdateCheckEnabled { get; set; }
    public UpdateChannel UpdateChannel { get; set; }
}
