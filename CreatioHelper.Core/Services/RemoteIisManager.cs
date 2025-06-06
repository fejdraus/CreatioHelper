using System;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Threading.Tasks;

namespace CreatioHelper.Core.Services
{
    [SupportedOSPlatform("windows")]
    public class RemoteIisManager(IOutputWriter output) : IRemoteIisManager
    {
        private readonly IOutputWriter _output = output ?? throw new ArgumentNullException(nameof(output));

        private bool IsLocal(string serverName) => string.Equals(serverName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

        public async Task<bool> StopAppPoolAsync(ServerInfo server)
        {
            if (!OperatingSystem.IsWindows())
            {
                _output.WriteLine("[ERROR] Pool management is only available on Windows.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(server.PoolName))
            {
                _output.WriteLine($"[ERROR] Pool name is not specified for server '{server.Name}'.");
                return false;
            }

            var currentState = await GetStateAsync(server.Name, true, $"Get-WebAppPoolState -Name '{server.PoolName}'");
            if (currentState == "Started")
            {
                if (!await ExecuteScriptAsync(server.Name, $"Stop-WebAppPool -Name '{server.PoolName}'"))
                    return false;
            }
            return await WaitForStateAsync(server.Name, true, $"Get-WebAppPoolState -Name '{server.PoolName}'", "Stopped", $"App pool {server.PoolName}");
        }

        public async Task<bool> StopWebsiteAsync(ServerInfo server)
        {
            if (!OperatingSystem.IsWindows())
            {
                _output.WriteLine("[ERROR] Pool management is only available on Windows.");
                return false;
            }
            
            if (string.IsNullOrWhiteSpace(server.SiteName))
            {
                _output.WriteLine($"[ERROR] Site name is not specified for server '{server.Name}'.");
                return false;
            }

            var siteStatus = await GetStateAsync(server.Name, false, $"Get-Website -Name '{server.SiteName}'");
            if (siteStatus == "Started")
            {
                if (!await ExecuteScriptAsync(server.Name, $"Stop-Website -Name '{server.SiteName}'"))
                    return false;
            }

            return await WaitForStateAsync(server.Name, false, $"Get-Website -Name '{server.SiteName}'", "Stopped", $"Website {server.SiteName}");
        }

        public async Task<bool> StartAppPoolAsync(ServerInfo server)
        {
            if (string.IsNullOrEmpty(server.PoolName)) throw new ArgumentNullException(nameof(server.PoolName));

            var poolStatus = await GetStateAsync(server.Name, true, $"Get-WebAppPoolState -Name '{server.PoolName}'");
            if (poolStatus == "Stopped")
            {
                //_output.WriteLine($"[INFO] Starting app pool {server.PoolName} on {_serverName}");
                if (!await ExecuteScriptAsync(server.Name, $"Start-WebAppPool -Name '{server.PoolName}'"))
                    return false;
            }

            return await WaitForStateAsync(server.Name, true, $"Get-WebAppPoolState -Name '{server.PoolName}'", "Started", $"App pool {server.PoolName}");
        }

        public async Task<bool> StartWebsiteAsync(ServerInfo server)
        {
            if (string.IsNullOrEmpty(server.SiteName)) throw new ArgumentNullException(nameof(server.SiteName));

            var siteStatus = await GetStateAsync(server.Name, false, $"Get-Website -Name '{server.SiteName}'");
            if (siteStatus == "Stopped")
            {
                //_output.WriteLine($"[INFO] Starting site {server.SiteName} on {_serverName}");
                if (!await ExecuteScriptAsync(server.Name, $"Start-Website -Name '{server.SiteName}'"))
                    return false;
            }

            return await WaitForStateAsync(server.Name, false, $"Get-Website -Name '{server.SiteName}'", "Started", $"Website {server.SiteName}");
        }

        private async Task<bool> ExecuteScriptAsync(string serverName, string script)
        {
            if (string.IsNullOrEmpty(script)) throw new ArgumentNullException(nameof(script));

            try
            {
                var command = IsLocal(serverName)
                    ? $"Import-Module WebAdministration; {script}"
                    : $"Invoke-Command -ComputerName '{serverName}' -ScriptBlock {{\r\n    Import-Module WebAdministration\r\n    {script}\r\n}} -ErrorAction Stop";

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

                //var outputText = await process.StandardOutput.ReadToEndAsync();
                var errorText = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                /*if (!string.IsNullOrWhiteSpace(outputText))
                {
                    _output.WriteLine($"[PS] {outputText.Trim()}");
                }*/
                
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

        private async Task<string> GetStateAsync(string serverName, bool isPool, string expression)
        {
            if (string.IsNullOrEmpty(expression)) throw new ArgumentNullException(nameof(expression));

            try
            {
                var script = isPool
                    ? $"Write-Output \"IsAdmin: $([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)\"; ({expression}).Value"
                    : $"Write-Output \"IsAdmin: $([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)\"; ({expression}).State";

                var command = IsLocal(serverName)
                    ? $"Import-Module WebAdministration; {script}"
                    : $"Invoke-Command -ComputerName '{serverName}' -ScriptBlock {{\r\n    Import-Module WebAdministration\r\n    {script}\r\n}} -ErrorAction Stop";

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

                var outputText = await process.StandardOutput.ReadToEndAsync();
                var errorText = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();
    
                if (!string.IsNullOrWhiteSpace(errorText))
                {
                    _output.WriteLine($"[PS-ERROR] Check the name of the pool");
                    return null;
                }
                
                var lines = outputText.Trim().Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 3)
                {
                    _output.WriteLine($"[PS-ERROR] Check the name of the site");
                    return null;
                }
                
                if (string.IsNullOrWhiteSpace(outputText))
                    return null;
                
                return  lines[2];
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[ERROR] PowerShell state fetch failed: {ex.Message}");
                return null;
            }
        }

        private async Task<bool> WaitForStateAsync(string serverName, bool isPool, string query, string desiredState, string name)
        {
            if (string.IsNullOrEmpty(query)) throw new ArgumentNullException(nameof(query));
            if (string.IsNullOrEmpty(desiredState)) throw new ArgumentNullException(nameof(desiredState));
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));

            string currentState;
            do
            {
                currentState = await GetStateAsync(serverName, isPool, query);
                if (string.Equals(currentState, desiredState, StringComparison.OrdinalIgnoreCase))
                {
                    //_output.WriteLine($"[INFO] {name} reached desired state: {desiredState}");
                    return true;
                }

                _output.WriteLine($"[WAIT] {name} current state: {currentState ?? "unknown"}, waiting...");
                await Task.Delay(5000);
            }
            while (!string.Equals(currentState, desiredState, StringComparison.OrdinalIgnoreCase));

            return true;
        }
        
        public async Task GetAppPoolStatusAsync(ServerInfo server)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(server.PoolName))
                {
                    var poolStatus = await GetStateAsync(server.Name,true, $"Get-WebAppPoolState -Name '{server.PoolName}'");
                    server.PoolStatus = poolStatus ?? "Error";
                }
                else
                {
                    server.PoolStatus = "Pool name is empty";
                }
            }
            catch (Exception ex)
            {
                server.PoolStatus = "Error";
                _output.WriteLine($"[ERROR] Failed to get status for server '{server.Name}': {ex.Message}");
            }
        }

        public async Task GetWebsiteStatusAsync(ServerInfo server)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(server.SiteName))
                {
                    var siteStatus = await GetStateAsync(server.Name,false, $"Get-Website -Name '{server.SiteName}'");
                    server.SiteStatus = siteStatus ?? "Error";
                }
                else
                {
                    server.SiteStatus = "Site name is empty";
                }
            }
            catch (Exception ex)
            {
                server.SiteStatus = "Error";
                _output.WriteLine($"[ERROR] Failed to get status for server '{server.Name}': {ex.Message}");
            }
        }
    }
}