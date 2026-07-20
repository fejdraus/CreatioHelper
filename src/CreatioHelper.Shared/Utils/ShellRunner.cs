using System.Diagnostics;

namespace CreatioHelper.Shared.Utils;

public sealed record ShellResult(int ExitCode, string Output, string Error)
{
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool Succeeded => ExitCode == 0 && !HasError;

        public static string? LastLineOf(string? output) => output?
        .Split('\n', '\r')
        .LastOrDefault(line => !string.IsNullOrWhiteSpace(line))?
        .Trim();
}
public static class ShellRunner
{
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
        public static string EscapeSingleQuoted(string value) => value.Replace("'", "'\\''");
    public static Task<ShellResult?> RunAsync(string command, CancellationToken cancellationToken = default)
        => ShellRunner.RunAsync(
            new ProcessStartInfo { FileName = "/bin/bash", Arguments = $"-c \"{command}\"" },
            cancellationToken);
}