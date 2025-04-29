using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CreatioHelper.Core;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace CreatioHelper;

public partial class AddServerWindow : Window
{
    private ServerInfo ViewModel => (ServerInfo)DataContext!;

    public AddServerWindow(ServerInfo? existing = null)
    {
        InitializeComponent();
        DataContext = existing != null
            ? new ServerInfo
            {
                Name = existing.Name,
                NetworkPath = existing.NetworkPath,
                SiteName = existing.SiteName,
                PoolName = existing.PoolName
            }
            : new ServerInfo();
    }

    private async void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ViewModel.Name))
        {
            await ShowValidationError("Please enter the server name.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ViewModel.NetworkPath))
        {
            await ShowValidationError("Please enter the network path.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ViewModel.SiteName))
        {
            await ShowValidationError("Please enter the site name.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ViewModel.PoolName))
        {
            await ShowValidationError("Please enter the pool name.");
            return;
        }

        Close(ViewModel);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private async Task ShowValidationError(string message)
    {
        var box = MessageBoxManager
            .GetMessageBoxStandard("Validation error", message, ButtonEnum.Ok, MsBox.Avalonia.Enums.Icon.Warning);
        await box.ShowWindowDialogAsync(this);
    }
}