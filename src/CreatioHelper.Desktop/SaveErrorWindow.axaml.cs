using Avalonia.Controls;
using Avalonia.Interactivity;

namespace CreatioHelper;

public partial class SaveErrorWindow : Window
{
    public SaveErrorWindow() : this(string.Empty) { }

    public SaveErrorWindow(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
