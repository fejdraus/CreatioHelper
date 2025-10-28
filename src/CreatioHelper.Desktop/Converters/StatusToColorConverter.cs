using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace CreatioHelper.Converters;

public class StatusToColorConverter : IValueConverter
{
    public static readonly StatusToColorConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string status)
            return Brushes.Gray;

        var statusLower = status.ToLowerInvariant();

        // IIS statuses
        if (statusLower is "started" or "running")
            return Brushes.Green;
        if (statusLower is "stopped")
            return Brushes.Red;
        if (statusLower is "starting" or "stopping")
            return Brushes.Orange;

        // Syncthing statuses with emoji
        // Green: Up to Date
        if (statusLower.Contains("up to date") || statusLower.Contains("✅"))
            return Brushes.Green;

        // Red: Offline, API Error, Error
        if (statusLower.Contains("offline") ||
            statusLower.Contains("api error") || statusLower.Contains("⚠️") ||
            statusLower.Contains("error") || statusLower.Contains("❌"))
            return Brushes.Red;

        // Blue: Syncing
        if (statusLower.Contains("syncing") || statusLower.Contains("🔄"))
            return Brushes.Blue;

        // Orange: Paused, Not Sharing
        if (statusLower.Contains("paused") || statusLower.Contains("⏸️") ||
            statusLower.Contains("not sharing") || statusLower.Contains("🚫"))
            return Brushes.Orange;

        // Gray: Not Configured, Monitor Disabled, Unknown
        if (statusLower.Contains("not configured") || statusLower.Contains("monitor disabled") || statusLower.Contains("⚙️") ||
            statusLower.Contains("unknown") || statusLower.Contains("❓"))
            return Brushes.Gray;

        // Generic statuses
        if (statusLower is "online")
            return Brushes.Green;
        if (statusLower is "checking..." or "loading...")
            return Brushes.Blue;
        if (statusLower is "not specified")
            return Brushes.LightGray;

        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}