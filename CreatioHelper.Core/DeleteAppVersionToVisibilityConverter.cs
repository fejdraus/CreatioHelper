using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace CreatioHelper.Core;

public class DeleteAppVersionToVisibilityConverter : IMultiValueConverter
{
    public object Convert(IList<object> values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is [bool isIisMode, bool isFolderMode, Version selectedVersion, Version sitePathWithVersion])
        {
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