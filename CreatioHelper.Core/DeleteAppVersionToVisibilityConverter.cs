using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace CreatioHelper.Core;

public class DeleteAppVersionToVisibilityConverter : IMultiValueConverter
{
    public object Convert(IList<object> values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Count == 4)
        {
            var isIisMode = values[0] is bool iis && iis;
            var isFolderMode = values[1] is bool folder && folder;
            
            var selectedVersion = values[2] switch
            {
                Version v => v,
                _ => new Version()
            };
            
            var sitePathWithVersion = values[3] switch
            {
                Version v => v,
                _ => new Version()
            };
            
            if (isIisMode)
            {
                return selectedVersion >= Constants.MinimumVersionForDeletePackages;
            }
            if (isFolderMode)
            {
                return sitePathWithVersion >= Constants.MinimumVersionForDeletePackages;
            }
        }
        return false;
    }
}