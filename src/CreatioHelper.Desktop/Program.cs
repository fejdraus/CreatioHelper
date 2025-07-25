using Avalonia;
using System;
using System.Text;

namespace CreatioHelper;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before BuildAvaloniaApp is called:
    // things aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // ✅ Register Windows code pages, including 866 (DOS Cyrillic)
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}