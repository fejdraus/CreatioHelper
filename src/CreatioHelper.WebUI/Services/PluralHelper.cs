using System.Globalization;
using Microsoft.Extensions.Localization;

namespace CreatioHelper.WebUI.Services;

public static class PluralHelper
{
    public static string Get(IStringLocalizer localizer, string keyPrefix, long count)
    {
        return localizer[$"{keyPrefix}_{GetForm(count)}"];
    }

    private static string GetForm(long count)
    {
        var n = Math.Abs(count);

        if (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru")
        {
            var mod10 = n % 10;
            var mod100 = n % 100;

            if (mod10 == 1 && mod100 != 11)
            {
                return "one";
            }

            if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14))
            {
                return "few";
            }

            return "many";
        }

        return n == 1 ? "one" : "many";
    }
}
