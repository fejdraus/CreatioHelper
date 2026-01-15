using Avalonia;
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using CreatioHelper.Services;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace CreatioHelper;

class Program
{
    private static SingleInstanceManager? _singleInstanceManager;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONWARNING = 0x00000030;

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before BuildAvaloniaApp is called:
    // things aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Initialize crash logger
        CrashLogger.Initialize();

        // Setup global exception handlers
        SetupGlobalExceptionHandlers();

        // ✅ Register Windows code pages, including 866 (DOS Cyrillic)
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Check for single instance
        _singleInstanceManager = new SingleInstanceManager("CreatioHelper_B5F8D3A1-9C2E-4F1A-8D7B-3E4C5A6B7C8D");

        try
        {
            bool isFirstInstance = _singleInstanceManager.TryAcquireLock();

            if (!isFirstInstance)
            {
                // Another instance is already running - show message and exit
                ShowErrorMessageAndExit("Application is already running");
                return;
            }

            // This is the first instance - start the application
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLogger.LogCrash(ex, "Main");
            ShowErrorMessageAndExit($"Error starting application: {ex.Message}");
        }
        finally
        {
            _singleInstanceManager?.Dispose();
        }
    }

    private static void SetupGlobalExceptionHandlers()
    {
        // Handle unhandled exceptions in non-UI threads
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var exception = e.ExceptionObject as Exception ?? new Exception("Unknown exception object");
            CrashLogger.LogCrash(exception, "AppDomain.UnhandledException");

            if (e.IsTerminating)
            {
                try
                {
                    ShowCrashMessage(exception);
                }
                catch
                {
                    // Ignore errors showing message during termination
                }
            }
        };

        // Handle unobserved task exceptions
        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            CrashLogger.LogCrash(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved(); // Prevent process termination
        };
    }

    private static void ShowCrashMessage(Exception exception)
    {
        var message = $"Application has crashed unexpectedly.\n\n" +
                      $"Error: {exception.Message}\n\n" +
                      $"A crash report has been saved to crash.log";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                MessageBox(IntPtr.Zero, message, "CreatioHelper - Critical Error", MB_OK | 0x00000010 /* MB_ICONERROR */);
            }
            catch
            {
                // Ignore
            }
        }
    }

    private static void ShowErrorMessageAndExit(string message)
    {
        try
        {
            // Prevent MainWindow from being created
            App.SuppressMainWindow = true;

            // Build Avalonia application
            var appBuilder = BuildAvaloniaApp();

            // Use a custom application lifetime to show message without creating MainWindow
            var lifetime = new Avalonia.Controls.ApplicationLifetimes.ClassicDesktopStyleApplicationLifetime
            {
                ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown
            };

            appBuilder.SetupWithLifetime(lifetime);

            // Flag to track if message was shown
            bool messageShown = false;

            // Show message box on UI thread
            Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    await MessageBoxManager
                        .GetMessageBoxStandard("CreatioHelper", message, ButtonEnum.Ok, Icon.Warning)
                        .ShowAsync();
                    messageShown = true;
                }
                catch
                {
                    // Fallback to native message box
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        MessageBox(IntPtr.Zero, message, "CreatioHelper", MB_OK | MB_ICONWARNING);
                    }
                    messageShown = true;
                }
                finally
                {
                    lifetime.Shutdown();
                }
            });

            // Start the application event loop - this blocks until Shutdown is called
            lifetime.Start(Array.Empty<string>());

            // Ensure we wait for message to be shown before exiting
            if (!messageShown)
            {
                System.Threading.Thread.Sleep(100);
            }
        }
        catch
        {
            // Last resort fallback to native message box
            try
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    MessageBox(IntPtr.Zero, message, "CreatioHelper", MB_OK | MB_ICONWARNING);
                }
            }
            catch
            {
                // If all else fails, silently exit
            }
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    public static SingleInstanceManager? GetSingleInstanceManager() => _singleInstanceManager;
}