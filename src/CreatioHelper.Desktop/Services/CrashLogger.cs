using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace CreatioHelper.Services;

public static class CrashLogger
{
    private static readonly string CrashLogPath = "crash.log";

    private static readonly object LockObject = new();
    private static DateTime _appStartTime = DateTime.UtcNow;

    public static void Initialize()
    {
        _appStartTime = DateTime.UtcNow;
    }

    public static void LogCrash(Exception exception, string source)
    {
        try
        {
            lock (LockObject)
            {
                var crashReport = BuildCrashReport(exception, source);
                File.AppendAllText(CrashLogPath, crashReport);

                // Also try to flush any pending FileLogService logs
                try
                {
                    FileLogService.FlushAsync().GetAwaiter().GetResult();
                }
                catch
                {
                    // Ignore errors during flush
                }
            }
        }
        catch
        {
            // If crash logging fails, try to write to a fallback location
            try
            {
                var fallbackPath = Path.Combine(Path.GetTempPath(), $"CreatioHelper_crash_{DateTime.UtcNow:yyyyMMdd_HHmmss}.log");
                File.WriteAllText(fallbackPath, BuildCrashReport(exception, source));
            }
            catch
            {
                // Nothing more we can do
            }
        }
    }

    private static string BuildCrashReport(Exception exception, string source)
    {
        var sb = new StringBuilder();

        sb.AppendLine("================================================================================");
        sb.AppendLine($"CRASH REPORT - {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine("================================================================================");
        sb.AppendLine();

        // Exception source
        sb.AppendLine($"Source: {source}");
        sb.AppendLine();

        // Application information
        sb.AppendLine("--- APPLICATION INFORMATION ---");
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version;
            var fileVersion = FileVersionInfo.GetVersionInfo(assembly.Location);

            sb.AppendLine($"Application: {assembly.GetName().Name}");
            sb.AppendLine($"Version: {version}");
            sb.AppendLine($"File Version: {fileVersion.FileVersion}");
            sb.AppendLine($"Product Version: {fileVersion.ProductVersion}");
            sb.AppendLine($"Build Date: {GetBuildDate(assembly)}");
            sb.AppendLine($"Uptime: {DateTime.UtcNow - _appStartTime}");
            sb.AppendLine($"Working Directory: {Environment.CurrentDirectory}");
            sb.AppendLine($"Base Directory: {AppDomain.CurrentDomain.BaseDirectory}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error getting app info: {ex.Message}");
        }
        sb.AppendLine();

        // System information
        sb.AppendLine("--- SYSTEM INFORMATION ---");
        try
        {
            sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
            sb.AppendLine($"OS Architecture: {RuntimeInformation.OSArchitecture}");
            sb.AppendLine($"Process Architecture: {RuntimeInformation.ProcessArchitecture}");
            sb.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"Machine Name: {Environment.MachineName}");
            sb.AppendLine($"User Name: {Environment.UserName}");
            sb.AppendLine($"Processor Count: {Environment.ProcessorCount}");
            sb.AppendLine($"System Page Size: {Environment.SystemPageSize} bytes");
            sb.AppendLine($"Is 64-bit OS: {Environment.Is64BitOperatingSystem}");
            sb.AppendLine($"Is 64-bit Process: {Environment.Is64BitProcess}");
            sb.AppendLine($"CLR Version: {Environment.Version}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error getting system info: {ex.Message}");
        }
        sb.AppendLine();

        // Memory information
        sb.AppendLine("--- MEMORY INFORMATION ---");
        try
        {
            var process = Process.GetCurrentProcess();
            sb.AppendLine($"Working Set: {FormatBytes(process.WorkingSet64)}");
            sb.AppendLine($"Private Memory: {FormatBytes(process.PrivateMemorySize64)}");
            sb.AppendLine($"Virtual Memory: {FormatBytes(process.VirtualMemorySize64)}");
            sb.AppendLine($"Peak Working Set: {FormatBytes(process.PeakWorkingSet64)}");
            sb.AppendLine($"GC Total Memory: {FormatBytes(GC.GetTotalMemory(false))}");
            sb.AppendLine($"GC Gen 0 Collections: {GC.CollectionCount(0)}");
            sb.AppendLine($"GC Gen 1 Collections: {GC.CollectionCount(1)}");
            sb.AppendLine($"GC Gen 2 Collections: {GC.CollectionCount(2)}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error getting memory info: {ex.Message}");
        }
        sb.AppendLine();

        // Thread information
        sb.AppendLine("--- THREAD INFORMATION ---");
        try
        {
            var process = Process.GetCurrentProcess();
            sb.AppendLine($"Thread Count: {process.Threads.Count}");
            sb.AppendLine($"Current Thread ID: {Environment.CurrentManagedThreadId}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error getting thread info: {ex.Message}");
        }
        sb.AppendLine();

        // Loaded assemblies
        sb.AppendLine("--- LOADED ASSEMBLIES ---");
        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .OrderBy(a => a.GetName().Name)
                .ToList();

            sb.AppendLine($"Total Count: {assemblies.Count}");
            sb.AppendLine();

            foreach (var asm in assemblies)
            {
                try
                {
                    var name = asm.GetName();
                    sb.AppendLine($"  {name.Name} v{name.Version}");
                }
                catch
                {
                    sb.AppendLine($"  [Unable to get assembly info]");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error getting assemblies: {ex.Message}");
        }
        sb.AppendLine();

        // Exception details
        sb.AppendLine("--- EXCEPTION DETAILS ---");
        AppendExceptionDetails(sb, exception, 0);
        sb.AppendLine();

        // Stack trace
        sb.AppendLine("--- STACK TRACE ---");
        sb.AppendLine(exception.StackTrace ?? "[No stack trace available]");
        sb.AppendLine();

        // Environment variables (sanitized)
        sb.AppendLine("--- ENVIRONMENT VARIABLES (SANITIZED) ---");
        try
        {
            var envVars = Environment.GetEnvironmentVariables();
            var sanitizedKeys = new[] { "PATH", "TEMP", "TMP", "PROCESSOR_IDENTIFIER", "NUMBER_OF_PROCESSORS", "OS" };

            foreach (var key in sanitizedKeys)
            {
                if (envVars.Contains(key))
                {
                    sb.AppendLine($"{key}: {envVars[key]}");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Error getting environment variables: {ex.Message}");
        }
        sb.AppendLine();

        sb.AppendLine("================================================================================");
        sb.AppendLine();

        return sb.ToString();
    }

    private static void AppendExceptionDetails(StringBuilder sb, Exception? exception, int level)
    {
        if (exception == null)
            return;

        var indent = new string(' ', level * 2);

        sb.AppendLine($"{indent}Type: {exception.GetType().FullName}");
        sb.AppendLine($"{indent}Message: {exception.Message}");
        sb.AppendLine($"{indent}Source: {exception.Source ?? "[Unknown]"}");
        sb.AppendLine($"{indent}HResult: 0x{exception.HResult:X8}");

        if (exception.TargetSite != null)
        {
            sb.AppendLine($"{indent}Target Site: {exception.TargetSite}");
        }

        if (exception.Data.Count > 0)
        {
            sb.AppendLine($"{indent}Data:");
            foreach (var key in exception.Data.Keys)
            {
                sb.AppendLine($"{indent}  {key}: {exception.Data[key]}");
            }
        }

        if (exception is AggregateException aggEx)
        {
            sb.AppendLine($"{indent}Inner Exceptions ({aggEx.InnerExceptions.Count}):");
            for (int i = 0; i < aggEx.InnerExceptions.Count; i++)
            {
                sb.AppendLine($"{indent}--- Inner Exception {i + 1} ---");
                AppendExceptionDetails(sb, aggEx.InnerExceptions[i], level + 1);
            }
        }
        else if (exception.InnerException != null)
        {
            sb.AppendLine($"{indent}--- Inner Exception ---");
            AppendExceptionDetails(sb, exception.InnerException, level + 1);
        }
    }

    private static string GetBuildDate(Assembly assembly)
    {
        try
        {
            var attribute = assembly.GetCustomAttribute<BuildDateAttribute>();
            if (attribute != null)
            {
                return attribute.Date.ToString("yyyy-MM-dd HH:mm:ss UTC");
            }

            // Fallback: try to get from file info
            var fileInfo = new FileInfo(assembly.Location);
            return fileInfo.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss UTC");
        }
        catch
        {
            return "[Unknown]";
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}

[AttributeUsage(AttributeTargets.Assembly)]
public class BuildDateAttribute : Attribute
{
    public DateTime Date { get; }

    public BuildDateAttribute(string date)
    {
        Date = DateTime.Parse(date);
    }
}
