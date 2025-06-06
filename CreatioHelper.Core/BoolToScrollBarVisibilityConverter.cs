using System;
using System.Globalization;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;

namespace CreatioHelper.Core
{
    public class BoolToScrollBarVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isChecked)
            {
                return isChecked ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            }
            return ScrollBarVisibility.Auto;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}