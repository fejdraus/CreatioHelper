using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CreatioHelper.Domain.Entities;
using CreatioHelper.ViewModels;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace CreatioHelper;

public partial class AddServerWindow : Window
{
    private AddServerViewModel ViewModel => (AddServerViewModel)DataContext!;

    public AddServerWindow() : this(null, false, false) { }

    public AddServerWindow(ServerInfo? existing, bool useSyncthingForSync, bool enableFileCopySync)
    {
        InitializeComponent();
        DataContext = new AddServerViewModel(existing, useSyncthingForSync, enableFileCopySync);
    }

    private async void OkButton_Click(object? sender, RoutedEventArgs e)
    {
        if (!ViewModel.Validate(out var validationError))
        {
            await ShowValidationError(validationError);
            return;
        }

        Close(ViewModel.Server);
    }

    private void CancelButton_Click(object? sender, RoutedEventArgs e)
    {
        Close(null);
    }

    private async Task ShowValidationError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            message = "An unknown validation error occurred.";
        }

        var box = MessageBoxManager
            .GetMessageBoxStandard("Validation error", message, ButtonEnum.Ok, MsBox.Avalonia.Enums.Icon.Warning);
        await box.ShowWindowDialogAsync(this);
    }
}