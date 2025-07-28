using System;
using System.IO;

namespace CreatioHelper.Services;

public static class FileLogService
{
    private static readonly object _lock = new();

    public static bool Enabled { get; set; }

    public static string LogFilePath { get; set; } = "log.txt";

    public static void AppendLine(string line)
    {
        if (!Enabled)
            return;

        lock (_lock)
        {
            File.AppendAllText(LogFilePath, line + Environment.NewLine);
        }
    }

    public static void Clear()
    {
        if (!Enabled)
            return;

        lock (_lock)
        {
            File.WriteAllText(LogFilePath, string.Empty);
        }
    }
}
