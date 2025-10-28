using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace CreatioHelper.Converters;

/// <summary>
/// Converts Syncthing Device ID and Folder ID to a color brush.
/// Green if both are configured, Red if not configured.
/// </summary>
public class SyncthingConfigToBrushConverter : IMultiValueConverter
{
    public static readonly SyncthingConfigToBrushConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values == null || values.Count < 2)
            return Brushes.Red;

        var deviceId = values[0] as string;
        var folderId = values[1] as string;

        // Both must be non-empty for "Yes" (green)
        bool bothConfigured = !string.IsNullOrEmpty(deviceId) && !string.IsNullOrEmpty(folderId);

        return bothConfigured ? Brushes.Green : Brushes.Red;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
