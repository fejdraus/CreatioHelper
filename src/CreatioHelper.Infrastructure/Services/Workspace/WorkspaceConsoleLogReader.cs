using System.Text;

namespace CreatioHelper.Infrastructure.Services.Workspace;

public sealed record WorkspaceConsoleLogEntry(string Header, IReadOnlyList<string> Details);

public sealed record WorkspaceConsoleLogReport(
    string FilePath,
    int LineCount,
    IReadOnlyList<WorkspaceConsoleLogEntry> Problems);

public static class WorkspaceConsoleLogReader
{
    private static readonly string[] KnownHarmlessMessages =
    {
        "Could not initialize manager provider configuration from DI"
    };

    private static readonly string[] ProblemMarkers =
    {
        "exception", "error", "fatal", "failed", "failure",
        "could not", "cannot", "unable to",
        "ошибка", "не удалось", "сбой"
    };

    public static string? ReadArgument(string arguments, string name)
    {
        var token = "-" + name + "=\"";
        var start = arguments.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += token.Length;
        var end = arguments.IndexOf('"', start);
        return end < 0 ? null : arguments.Substring(start, end - start);
    }

    public static string? FindLatestLog(string logDirectory, string operation, DateTime notBefore)
    {
        if (string.IsNullOrWhiteSpace(logDirectory) || !Directory.Exists(logDirectory))
        {
            return null;
        }

        var pattern = string.IsNullOrWhiteSpace(operation) ? "log_*.txt" : $"log_{operation}_*.txt";
        return Directory.EnumerateFiles(logDirectory, pattern, SearchOption.TopDirectoryOnly)
            .Select(f => new FileInfo(f))
            .Where(f => f.LastWriteTime >= notBefore)
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => f.FullName)
            .FirstOrDefault();
    }

    public static WorkspaceConsoleLogReport Read(string filePath)
    {
        var problems = new List<WorkspaceConsoleLogEntry>();
        var lineCount = 0;
        string? header = null;
        var details = new List<string>();

        void Flush()
        {
            if (header == null)
            {
                return;
            }

            if (IsProblem(header, details))
            {
                problems.Add(new WorkspaceConsoleLogEntry(header, details.ToList()));
            }

            header = null;
            details.Clear();
        }

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (reader.ReadLine() is { } line)
        {
            lineCount++;
            if (StartsWithTimestamp(line))
            {
                Flush();
                header = line.TrimEnd();
            }
            else if (header != null && line.Trim().Length > 0)
            {
                details.Add(line.TrimEnd());
            }
        }

        Flush();
        return new WorkspaceConsoleLogReport(filePath, lineCount, problems);
    }

    private static bool StartsWithTimestamp(string line)
    {
        return line.Length >= 10
               && line[0] == '['
               && char.IsDigit(line[1]) && char.IsDigit(line[2])
               && line[3] == ':'
               && char.IsDigit(line[4]) && char.IsDigit(line[5])
               && line[6] == ':'
               && char.IsDigit(line[7]) && char.IsDigit(line[8])
               && line[9] == ']';
    }

    private static bool IsProblem(string header, List<string> details)
    {
        foreach (var harmless in KnownHarmlessMessages)
        {
            if (header.Contains(harmless, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (ContainsMarker(header))
        {
            return true;
        }

        foreach (var detail in details)
        {
            if (ContainsMarker(detail))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsMarker(string text)
    {
        if (IsEmptyCountSummary(text))
        {
            return false;
        }

        foreach (var marker in ProblemMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsEmptyCountSummary(string text)
    {
        var body = StripTimestamp(text).Trim();
        var space = body.IndexOf(' ');
        if (space <= 0)
        {
            return false;
        }

        if (!int.TryParse(body.AsSpan(0, space), out var count))
        {
            return false;
        }

        var tail = body[(space + 1)..].Trim();
        var isCount = tail.Equals("Error(s)", StringComparison.OrdinalIgnoreCase)
                      || tail.Equals("Warning(s)", StringComparison.OrdinalIgnoreCase)
                      || tail.Equals("Errors", StringComparison.OrdinalIgnoreCase)
                      || tail.Equals("Warnings", StringComparison.OrdinalIgnoreCase);

        return isCount && count == 0;
    }

    private static string StripTimestamp(string text)
    {
        return StartsWithTimestamp(text) ? text[10..] : text;
    }
}
