namespace CreatioHelper.WebUI.Services;

public static class DateFormatter
{
    // Set from user preferences (gui_dateFormat): short | medium | long | iso.
    public static string CurrentFormat { get; set; } = "medium";

    public static string FormatDate(DateTime date) => FormatDate(date, CurrentFormat);

    public static string FormatDate(DateTime date, string format) => format switch
    {
        "short" => date.ToString("MM/dd/yy"),
        "long" => date.ToString("MMMM d, yyyy"),
        "iso" => date.ToString("yyyy-MM-dd"),
        _ => date.ToString("MMM d, yyyy")
    };
}
