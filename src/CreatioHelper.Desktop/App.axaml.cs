using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using System.Net.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using CreatioHelper.Application.Extensions;
using CreatioHelper.Infrastructure.Extensions;
using CreatioHelper.Services;
using MsBox.Avalonia;

namespace CreatioHelper;

public partial class App : Avalonia.Application
{
    public static IServiceProvider? Services { get; private set; }
    internal static bool SuppressMainWindow { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Setup Avalonia UI thread exception handler
        SetupAvaloniaExceptionHandler();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Skip MainWindow creation if we're just showing an error message
            if (SuppressMainWindow)
            {
                base.OnFrameworkInitializationCompleted();
                return;
            }

            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            desktop.MainWindow = new MainWindow();

            // Subscribe to single instance activation requests
            var singleInstanceManager = Program.GetSingleInstanceManager();
            if (singleInstanceManager != null)
            {
                singleInstanceManager.ActivationRequested += (sender, e) =>
                {
                    // Activate main window on UI thread
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        if (desktop.MainWindow != null)
                        {
                            WindowActivator.ActivateWindow(desktop.MainWindow);
                        }
                    });
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupAvaloniaExceptionHandler()
    {
        // Handle unhandled exceptions in Avalonia UI thread
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (sender, e) =>
        {
            CrashLogger.LogCrash(e.Exception, "Avalonia.UIThread.UnhandledException");

            // Show error message to user
            try
            {
                var message = $"An unexpected error occurred:\n\n{e.Exception.Message}\n\n" +
                              $"A crash report has been saved to crash.log\n\n" +
                              $"The application will attempt to continue, but may be unstable.";

                _ = MessageBoxManager
                    .GetMessageBoxStandard("Error", message,
                        MsBox.Avalonia.Enums.ButtonEnum.Ok,
                        MsBox.Avalonia.Enums.Icon.Error)
                    .ShowAsync();
            }
            catch
            {
                // Ignore errors showing message box
            }

            e.Handled = true; // Prevent application termination
        };
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Enable logging for MetricsService and other services
        services.AddLogging(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        // Add HttpClient for Syncthing API
        services.AddHttpClient("Syncthing", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            var handler = new SocketsHttpHandler
            {
                // Accept any certificate; Syncthing uses self-signed certs by default
                SslOptions =
                {
                    RemoteCertificateValidationCallback = static (_, _, _, _) => true
                },
                AllowAutoRedirect = true,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10)
            };
            return handler;
        });

        services.AddApplication();
        services.AddInfrastructureServices();

        // Register UI dispatcher for Avalonia
        services.AddSingleton<CreatioHelper.Application.Interfaces.IUIDispatcher, CreatioHelper.Services.AvaloniaUIDispatcher>();
    }
}
