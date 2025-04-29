using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace CreatioHelper.Core.Services
{
    [SupportedOSPlatform("windows")]
    public class RemoteIisManager : IRemoteIisManager
    {
        private readonly string _serverName;
        private readonly IOutputWriter _output;

        private bool IsLocal => string.Equals(_serverName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

        public RemoteIisManager(string serverName, IOutputWriter output)
        {
            _serverName = serverName ?? throw new ArgumentNullException(nameof(serverName));
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        public async Task<bool> StopAppPoolAsync(string poolName)
        {
            if (string.IsNullOrEmpty(poolName)) throw new ArgumentNullException(nameof(poolName));

            string currentState = await GetStateAsync(true, $"Get-WebAppPoolState -Name '{poolName}'");
            if (currentState == "Started")
            {
                _output.WriteLine($"[INFO] Stopping app pool {poolName} on {_serverName}");
                if (!await ExecuteScriptAsync($"Stop-WebAppPool -Name '{poolName}'"))
                    return false;
            }

            return await WaitForStateAsync(true, $"Get-WebAppPoolState -Name '{poolName}'", "Stopped", $"App pool {poolName}");
        }

        public async Task<bool> StopWebsiteAsync(string siteName)
        {
            if (string.IsNullOrEmpty(siteName)) throw new ArgumentNullException(nameof(siteName));

            string siteStatus = await GetStateAsync(false, $"Get-Website -Name '{siteName}'");
            if (siteStatus == "Started")
            {
                _output.WriteLine($"[INFO] Stopping site {siteName} on {_serverName}");
                if (!await ExecuteScriptAsync($"Stop-Website -Name '{siteName}'"))
                    return false;
            }

            return await WaitForStateAsync(false, $"Get-Website -Name '{siteName}'", "Stopped", $"Website {siteName}");
        }

        public async Task<bool> StartAppPoolAsync(string poolName)
        {
            if (string.IsNullOrEmpty(poolName)) throw new ArgumentNullException(nameof(poolName));

            string poolStatus = await GetStateAsync(true, $"Get-WebAppPoolState -Name '{poolName}'");
            if (poolStatus == "Stopped")
            {
                _output.WriteLine($"[INFO] Starting app pool {poolName} on {_serverName}");
                if (!await ExecuteScriptAsync($"Start-WebAppPool -Name '{poolName}'"))
                    return false;
            }

            return await WaitForStateAsync(true, $"Get-WebAppPoolState -Name '{poolName}'", "Started", $"App pool {poolName}");
        }

        public async Task<bool> StartWebsiteAsync(string siteName)
        {
            if (string.IsNullOrEmpty(siteName)) throw new ArgumentNullException(nameof(siteName));

            string siteStatus = await GetStateAsync(false, $"Get-Website -Name '{siteName}'");
            if (siteStatus == "Stopped")
            {
                _output.WriteLine($"[INFO] Starting site {siteName} on {_serverName}");
                if (!await ExecuteScriptAsync($"Start-Website -Name '{siteName}'"))
                    return false;
            }

            return await WaitForStateAsync(false, $"Get-Website -Name '{siteName}'", "Started", $"Website {siteName}");
        }

        private async Task<bool> ExecuteScriptAsync(string script)
        {
            if (string.IsNullOrEmpty(script)) throw new ArgumentNullException(nameof(script));

            try
            {
                string command = IsLocal
                    ? $"Import-Module WebAdministration; {script}"
                    : $"Invoke-Command -ComputerName '{_serverName}' -ScriptBlock {{\r\n    Import-Module WebAdministration\r\n    {script}\r\n}} -ErrorAction Stop";

                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    _output.WriteLine("[ERROR] Failed to start PowerShell process.");
                    return false;
                }

                string outputText = await process.StandardOutput.ReadToEndAsync();
                string errorText = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (!string.IsNullOrWhiteSpace(outputText))
                    _output.WriteLine($"[PS] {outputText.Trim()}");

                if (!string.IsNullOrWhiteSpace(errorText))
                {
                    _output.WriteLine($"[PS-ERROR] {errorText.Trim()}");
                    return false;
                }

                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[ERROR] PowerShell process failed: {ex.Message}");
                return false;
            }
        }

        private async Task<string?> GetStateAsync(bool isPool, string expression)
        {
            if (string.IsNullOrEmpty(expression)) throw new ArgumentNullException(nameof(expression));

            try
            {
                string script = isPool
                    ? $"Write-Output \"IsAdmin: $([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)\"; ({expression}).Value"
                    : $"Write-Output \"IsAdmin: $([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)\"; ({expression}).State";

                string command = IsLocal
                    ? $"Import-Module WebAdministration; {script}"
                    : $"Invoke-Command -ComputerName '{_serverName}' -ScriptBlock {{\r\n    Import-Module WebAdministration\r\n    {script}\r\n}} -ErrorAction Stop";

                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    _output.WriteLine("[ERROR] Failed to start PowerShell process.");
                    return null;
                }

                string outputText = await process.StandardOutput.ReadToEndAsync();
                string errorText = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (!string.IsNullOrWhiteSpace(outputText))
                    _output.WriteLine($"[PS] {outputText.Trim()}");

                if (!string.IsNullOrWhiteSpace(errorText))
                {
                    _output.WriteLine($"[PS-ERROR] {errorText.Trim()}");
                    return null;
                }

                if (string.IsNullOrWhiteSpace(outputText))
                    return null;

                string[] lines = outputText.Trim().Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                return lines[^1]; // Последняя строка содержит состояние
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[ERROR] PowerShell state fetch failed: {ex.Message}");
                return null;
            }
        }

        private async Task<bool> WaitForStateAsync(bool isPool, string query, string desiredState, string name)
        {
            if (string.IsNullOrEmpty(query)) throw new ArgumentNullException(nameof(query));
            if (string.IsNullOrEmpty(desiredState)) throw new ArgumentNullException(nameof(desiredState));
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            for (int attempt = 1; attempt <= 12; attempt++)
            {
                string? currentState = await GetStateAsync(isPool, query);
                if (string.Equals(currentState, desiredState, StringComparison.OrdinalIgnoreCase))
                {
                    _output.WriteLine($"[INFO] {name} reached desired state: {desiredState}");
                    return true;
                }

                _output.WriteLine($"[WAIT] {name} current state: {currentState ?? "unknown"}, waiting...");
                await Task.Delay(5000);
            }

            _output.WriteLine($"[ERROR] {name} did not reach state {desiredState} within expected time.");
            return false;
        }
    }
}