namespace CreatioHelper.Agent.Logging;

public static class SystemLogStore
{
    public static string DatabasePath { get; } =
        Path.Combine(Directory.GetCurrentDirectory(), "logs", "system-log.db");
}
