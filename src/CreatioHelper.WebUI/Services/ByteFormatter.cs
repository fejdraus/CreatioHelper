using Microsoft.Extensions.Localization;

namespace CreatioHelper.WebUI.Services;

public static class ByteFormatter
{
    private static readonly string[] UnitKeys =
    {
        "Unit_B", "Unit_KB", "Unit_MB", "Unit_GB", "Unit_TB", "Unit_PB"
    };

    public static string Format(long bytes, IStringLocalizer localizer)
    {
        double len = Math.Abs(bytes);
        var order = 0;

        while (len >= 1024 && order < UnitKeys.Length - 1)
        {
            order++;
            len /= 1024;
        }

        var sign = bytes < 0 ? "-" : "";
        var format = order == 0 ? "F0" : (order <= 2 ? "F1" : "F2");

        return $"{sign}{len.ToString(format)} {localizer[UnitKeys[order]]}";
    }
}
