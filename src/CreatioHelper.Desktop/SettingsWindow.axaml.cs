using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Application.Services.Updates;
using CreatioHelper.Domain.Enums;
using CreatioHelper.ViewModels;

namespace CreatioHelper;

public partial class SettingsWindow : Window
{
    private readonly SettingsWindowViewModel _viewModel;
    private readonly IUpdateService? _updateService;

    public SettingsWindow()
    {
        InitializeComponent();
        _viewModel = new SettingsWindowViewModel();
        DataContext = _viewModel;
    }

    public SettingsWindow(bool updateCheckEnabled, UpdateChannel updateChannel, IUpdateService updateService)
    {
        InitializeComponent();
        _updateService = updateService;
        _viewModel = new SettingsWindowViewModel
        {
            UpdateCheckEnabled = updateCheckEnabled,
            UpdateChannel = updateChannel,
            CurrentVersion = updateService.CurrentVersion,
            LatestVersion = updateService.LastSeenVersion,
        };
        DataContext = _viewModel;

        ApplyUpdateState(updateService.State);
        updateService.StateChanged += OnUpdateServiceStateChanged;
        Closed += (_, _) => updateService.StateChanged -= OnUpdateServiceStateChanged;
    }

    private void OnUpdateServiceStateChanged(object? sender, UpdateState state)
    {
        Dispatcher.UIThread.Post(() => ApplyUpdateState(state));
    }

    private void ApplyUpdateState(UpdateState state)
    {
        switch (state)
        {
            case UpdateState.Checking:
                _viewModel.IsCheckInFlight = true;
                _viewModel.CheckStatus = "Checking…";
                _viewModel.ActionButtonText = "Checking…";
                _viewModel.IsActionButtonEnabled = false;
                _viewModel.IsDownloadProgressVisible = false;
                break;

            case UpdateState.Available available:
                _viewModel.IsCheckInFlight = false;
                _viewModel.LatestVersion = available.Version;
                _viewModel.CheckStatus = "Update available";
                _viewModel.ActionButtonText = "Install update now";
                _viewModel.IsActionButtonEnabled = true;
                _viewModel.IsDownloadProgressVisible = false;
                _viewModel.DownloadProgressPercent = 0;
                break;

            case UpdateState.Downloading downloading:
                _viewModel.IsCheckInFlight = false;
                _viewModel.LatestVersion = downloading.Version;
                _viewModel.CheckStatus = $"Downloading {downloading.Percent:F0}%";
                _viewModel.ActionButtonText = $"Downloading {downloading.Percent:F0}%";
                _viewModel.IsActionButtonEnabled = false;
                _viewModel.IsDownloadProgressVisible = true;
                _viewModel.DownloadProgressPercent = downloading.Percent;
                break;

            case UpdateState.Ready ready:
                _viewModel.IsCheckInFlight = false;
                _viewModel.LatestVersion = ready.Version;
                _viewModel.CheckStatus = "Update downloaded — pending restart";
                _viewModel.ActionButtonText = "Restart and apply update";
                _viewModel.IsActionButtonEnabled = true;
                _viewModel.IsDownloadProgressVisible = false;
                _viewModel.DownloadProgressPercent = 100;
                break;

            case UpdateState.Idle idle:
                _viewModel.IsCheckInFlight = false;
                _viewModel.IsDownloadProgressVisible = false;
                _viewModel.DownloadProgressPercent = 0;
                _viewModel.ActionButtonText = "Check for updates now";
                _viewModel.IsActionButtonEnabled = true;
                if (!string.IsNullOrEmpty(idle.Error))
                {
                    _viewModel.CheckStatus = $"Check failed: {idle.Error}";
                }
                else if (idle.NotAvailable)
                {
                    if (_updateService is not null && !string.IsNullOrEmpty(_updateService.LastSeenVersion))
                    {
                        _viewModel.LatestVersion = _updateService.LastSeenVersion;
                    }
                    _viewModel.CheckStatus = "Up to date";
                }
                else
                {
                    if (_updateService is not null && !string.IsNullOrEmpty(_updateService.LastSeenVersion))
                    {
                        _viewModel.LatestVersion = _updateService.LastSeenVersion;
                    }
                    _viewModel.CheckStatus = null;
                }
                break;

            case UpdateState.Disabled:
                _viewModel.IsCheckInFlight = false;
                _viewModel.CheckStatus = "Update checks are disabled";
                _viewModel.ActionButtonText = "Check for updates now";
                _viewModel.IsActionButtonEnabled = false;
                _viewModel.IsDownloadProgressVisible = false;
                break;
        }
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

    private async void ActionButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_updateService is null)
        {
            return;
        }

        switch (_updateService.State)
        {
            case UpdateState.Available:
                try
                {
                    _ = _updateService.DownloadAndInstallAsync();
                }
                catch
                {
                    // Surfaced via state events.
                }
                break;

            case UpdateState.Ready:
                _updateService.QuitAndApply();
                break;

            default:
                try
                {
                    await _updateService.CheckNowAsync(explicitly: true);
                }
                catch
                {
                    // Surfaced via state events.
                }
                break;
        }
    }
}

public class SettingsResult
{
    public bool UpdateCheckEnabled { get; set; }
    public UpdateChannel UpdateChannel { get; set; }
}
