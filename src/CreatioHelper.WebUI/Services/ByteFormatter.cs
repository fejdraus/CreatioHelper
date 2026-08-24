using Microsoft.Extensions.Localization;

namespace CreatioHelper.WebUI.Services;

public static class ByteFormatter
{
    private static readonly string[] BinaryUnits = { "B", "KiB", "MiB", "GiB", "TiB", "PiB" };
    private static readonly string[] DecimalUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

    // Set from user preferences (gui_sizeFormat). true = binary (1024), false = decimal (1000).
    public static bool UseBinary { get; set; } = true;

    public static string Format(long bytes) => Format(bytes, UseBinary);

    public static string Format(long bytes, bool useBinary)
    {
        var units = useBinary ? BinaryUnits : DecimalUnits;
        double divisor = useBinary ? 1024d : 1000d;

        double len = Math.Abs(bytes);
        var order = 0;
        while (len >= divisor && order < units.Length - 1)
        {
            order++;
            len /= divisor;
        }

        var sign = bytes < 0 ? "-" : "";
        var format = order == 0 ? "F0" : (order <= 2 ? "F1" : "F2");
        return $"{sign}{len.ToString(format)} {units[order]}";
    }

    // Kept for existing call sites that pass a localizer; unit symbols are universal, so it is ignored.
    public static string Format(long bytes, IStringLocalizer localizer) => Format(bytes, UseBinary);
}
