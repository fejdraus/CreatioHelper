#nullable enable
using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace CreatioHelper.Core;

public class StatusToColorConverter : IValueConverter
{
    public static readonly StatusToColorConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string status)
            return Brushes.Gray;

        return status.ToLowerInvariant() switch
        {
            "started" or "running" => Brushes.Green,
            "stopped" => Brushes.Red,
            "starting" or "stopping" => Brushes.Orange,
            "checking..." => Brushes.Blue,
            "error" => Brushes.Red,
            "unknown" => Brushes.Gray,
            "not specified" => Brushes.LightGray,
            _ => Brushes.Gray
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}