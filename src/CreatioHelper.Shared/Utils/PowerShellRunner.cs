using System.Diagnostics;
using System.Text;

namespace CreatioHelper.Shared.Utils;

/// <summary>
/// Builds and runs PowerShell commands on top of <see cref="ShellRunner"/>.
/// </summary>
public static class PowerShellRunner
{
    public const string ImportWebAdministration = "Import-Module WebAdministration; ";
    public const string Utf8OutputPrologue = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; ";

    /// <summary>
    /// Escapes a value for use inside a single-quoted PowerShell literal. Site, pool and service
    /// names come from configuration, so an unescaped apostrophe would break the command or let
    /// arbitrary PowerShell through.
    /// </summary>
    public static string EscapeSingleQuoted(string value) => value.Replace("'", "''");

    /// <summary>
    /// True when the script should run on this machine instead of going through WinRM.
    /// </summary>
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
