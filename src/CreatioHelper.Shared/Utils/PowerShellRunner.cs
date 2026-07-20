using System.Diagnostics;
using System.Text;

namespace CreatioHelper.Shared.Utils;

public static class PowerShellRunner
{
    public const string ImportWebAdministration = "Import-Module WebAdministration; ";
    public const string Utf8OutputPrologue = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; ";
        public static string EscapeSingleQuoted(string value) => value.Replace("'", "''");
        public static bool IsLocalServer(string? serverName) =>
        string.IsNullOrWhiteSpace(serverName)
        || string.Equals(serverName, "localhost", StringComparison.OrdinalIgnoreCase)
        || string.Equals(serverName, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(serverName, "::1", StringComparison.OrdinalIgnoreCase)
        || string.Equals(serverName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    public static Task<ShellResult?> RunAsync(
        string command,
        string executionPolicy = "Bypass",
        bool useUtf8 = false,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy {executionPolicy} -Command \"{command}\""
        };
        if (useUtf8)
        {
            startInfo.StandardOutputEncoding = Encoding.UTF8;
            startInfo.StandardErrorEncoding = Encoding.UTF8;
        }
        return ShellRunner.RunAsync(startInfo, cancellationToken);
    }
}