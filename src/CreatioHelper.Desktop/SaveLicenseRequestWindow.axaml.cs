using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace CreatioHelper;

public class SaveLicenseRequestResult
{
    public string CustomerId { get; set; } = "";
    public string FilePath { get; set; } = "";
}

public partial class SaveLicenseRequestWindow : Window
{
    public SaveLicenseRequestWindow()
    {
        InitializeComponent();
    }

    private async void Save_Click(object? sender, RoutedEventArgs e)
    {
        var customerId = CustomerIdTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(customerId))
        {
            await ShowValidationError("Customer ID is required.");
            CustomerIdTextBox.Focus();
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title = "Save license request",
            SuggestedFileName = "LicenseRequest.tlr",
            FileTypeChoices = new[]
            {
                new Avalonia.Platform.Storage.FilePickerFileType("License request") { Patterns = new[] { "*.tlr" } }
            }
        });

        if (file == null) return;

        Close(new SaveLicenseRequestResult
        {
            CustomerId = customerId,
            FilePath = file.Path.LocalPath
        });
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
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
