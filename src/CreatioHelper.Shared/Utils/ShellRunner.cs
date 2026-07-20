using System.Diagnostics;

namespace CreatioHelper.Shared.Utils;

public sealed record ShellResult(int ExitCode, string Output, string Error)
{
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool Succeeded => ExitCode == 0 && !HasError;
}

/// <summary>
/// Starts a child process and returns its exit code together with both streams.
/// Callers decide how to interpret failures; this type only owns process handling.
/// </summary>
public static class ShellRunner
{
    /// <summary>
    /// Returns null when the process could not be started at all.
    /// Both streams are always drained concurrently before waiting for exit, otherwise a command
    /// writing more than the pipe buffer holds would deadlock.
    /// </summary>
    public static async Task<ShellResult?> RunAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken = default)
    {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return null;
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        return new ShellResult(process.ExitCode, output, error);
    }
}

public static class BashRunner
{
    /// <summary>
    /// Escapes a value for use inside a single-quoted bash literal.
    /// </summary>
    public static string EscapeSingleQuoted(string value) => value.Replace("'", "'\\''");

    public static Task<ShellResult?> RunAsync(string command, CancellationToken cancellationToken = default)
        => ShellRunner.RunAsync(
            new ProcessStartInfo { FileName = "/bin/bash", Arguments = $"-c \"{command}\"" },
            cancellationToken);
}
