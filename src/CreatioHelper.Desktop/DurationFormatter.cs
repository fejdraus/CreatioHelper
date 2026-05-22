namespace CreatioHelper;

internal static class DurationFormatter
{
    internal static string Format(double ms)
    {
        if (ms < 1000) { return $"{ms:F0} ms"; }
        var totalSec = ms / 1000.0;
        if (totalSec < 60) { return $"{totalSec:F2} s"; }
        var minutes = (int)(totalSec / 60);
        var seconds = (int)(totalSec % 60);
        if (minutes < 60) { return $"{minutes} min {seconds} s"; }
        var hours = (int)(minutes / 60);
        var mins = minutes % 60;
        return $"{hours} h {mins} min";
    }
}
