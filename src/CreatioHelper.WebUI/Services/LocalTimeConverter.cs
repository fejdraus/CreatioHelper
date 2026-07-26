namespace CreatioHelper.WebUI.Services;

public static class LocalTimeConverter
{
    public static DateTime ToLocal(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Local => value,
            DateTimeKind.Utc => value.ToLocalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime()
        };
    }

    public static DateTime? ToLocal(DateTime? value)
    {
        return value.HasValue ? ToLocal(value.Value) : null;
    }
}
