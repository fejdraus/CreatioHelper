using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CreatioHelper;

public partial class CloseWarningWindow : Window
{
    public CloseWarningWindow()
    {
        InitializeComponent();
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}