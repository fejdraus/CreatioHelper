using Avalonia.Controls;
using System;
using System.Runtime.InteropServices;

namespace CreatioHelper.Services;

public static class WindowActivator
{
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    /// <summary>
    /// Activates and brings the window to foreground
    /// </summary>
    public static void ActivateWindow(Window window)
    {
        if (window == null)
            return;

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Get native window handle
                var handle = window.TryGetPlatformHandle();
                if (handle != null)
                {
                    var hwnd = handle.Handle;

                    // Restore if minimized
                    if (IsIconic(hwnd))
                    {
                        ShowWindow(hwnd, SW_RESTORE);
                    }
                    else
                    {
                        ShowWindow(hwnd, SW_SHOW);
                    }

                    // Bring to foreground
                    SetForegroundWindow(hwnd);
                }
            }

            // Use Avalonia's cross-platform methods
            window.WindowState = WindowState.Normal;
            window.Activate();
            window.BringIntoView();
            window.Focus();
        }
        catch
        {
            // Fallback to Avalonia only
            try
            {
                window.WindowState = WindowState.Normal;
                window.Activate();
            }
            catch
            {
                // Ignore errors
            }
        }
    }
}
