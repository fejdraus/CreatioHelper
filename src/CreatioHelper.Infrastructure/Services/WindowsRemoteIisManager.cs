using CreatioHelper.Domain.Common;
using System.Diagnostics;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.Infrastructure.Services
{
    public class WindowsRemoteIisManager : IRemoteIisManager
    {
        private readonly IOutputWriter _output;
        private readonly SystemServiceManager _systemServiceManager;
        private readonly IMetricsService _metrics;

        public WindowsRemoteIisManager(IOutputWriter output, IMetricsService metrics)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
            _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
            _systemServiceManager = new SystemServiceManager(output);
        }

        private bool IsLocal(string serverName) => string.Equals(serverName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

        public async Task<Result> StopAppPoolAsync(string poolName, CancellationToken cancellationToken = default)
        {
            if (!OperatingSystem.IsWindows())
            {
                return Result.Failure("Pool management is only available on Windows.");
            }

            if (string.IsNullOrWhiteSpace(poolName))
            {
                return Result.Failure("Pool name is required");
            }

            return await _metrics.MeasureAsync<Result>("iis_command_duration", async () =>
            {
                try
                {
                    var serverName = Environment.MachineName;

                    if (cancellationToken.IsCancellationRequested)
                        return Result.Failure("Operation was cancelled");

                    var currentState = await GetStateAsync(serverName, true, $"Get-WebAppPoolState -Name '{poolName}'", cancellationToken);
                    if (currentState == "Started")
                    {
                        if (!await ExecuteScriptAsync(serverName, $"Stop-WebAppPool -Name '{poolName}'", cancellationToken))
                        {
                            return Result.Failure($"Failed to stop app pool {poolName}");
                        }

                        if (!await WaitForStateAsync(serverName, true, $"Get-WebAppPoolState -Name '{poolName}'", "Stopped", $"Application pool {poolName}", cancellationToken))
                        {
                            return Result.Failure($"Application pool {poolName} did not reach stopped state");
                        }
                        return Result.Success();
                    }
                    if (currentState == "Stopped")
                    {
                        return Result.Success();
                    }
                    return Result.Failure(currentState ?? "Unknown application pool status");
                }
                catch (OperationCanceledException)
                {
                    return Result.Failure("Operation was cancelled");
                }
                catch (Exception ex)
                {
                    var errorMsg = $"Failed to stop app pool {poolName}: {ex.Message}";
                    _output.WriteLine($"[ERROR] {errorMsg}");
                    return Result.Failure(errorMsg, ex);
                }
            }, new() { ["operation"] = "stop_app_pool", ["pool_name"] = poolName });
        }

        public async Task<Result> StopWebsiteAsync(string siteName, CancellationToken cancellationToken = default)
        {
            if (!OperatingSystem.IsWindows())
            {
                return Result.Failure("Website management is only available on Windows.");
            }

            if (string.IsNullOrWhiteSpace(siteName))
            {
                return Result.Failure("Site name is required");
            }

            return await _metrics.MeasureAsync<Result>("iis_command_duration", async () =>
            {
                try
                {
                    var serverName = Environment.MachineName;

                    if (cancellationToken.IsCancellationRequested)
                        return Result.Failure("Operation was cancelled");

                    var currentState = await GetStateAsync(serverName, false, $"Get-Website -Name '{siteName}'", cancellationToken);
                    if (currentState == "Started")
                    {
                        if (!await ExecuteScriptAsync(serverName, $"Stop-Website -Name '{siteName}'", cancellationToken))
                        {
                            return Result.Failure($"Failed to stop website {siteName}");
                        }

                        if (!await WaitForStateAsync(serverName, false, $"Get-Website -Name '{siteName}'", "Stopped", $"Website {siteName}", cancellationToken))
                        {
                            return Result.Failure($"Website {siteName} did not reach stopped state");
                        }
                        return Result.Success();
                    }
                    if (currentState == "Stopped")
                    {
                        return Result.Success();
                    }
                    return Result.Failure(currentState ?? "Unknown website status");
                }
                catch (OperationCanceledException)
                {
                    return Result.Failure("Operation was cancelled");
                }
                catch (Exception ex)
                {
                    var errorMsg = $"Failed to stop website {siteName}: {ex.Message}";
                    _output.WriteLine($"[ERROR] {errorMsg}");
                    return Result.Failure(errorMsg, ex);
                }
            }, new() { ["operation"] = "stop_website", ["site_name"] = siteName });
        }

        public async Task<Result> StartAppPoolAsync(string poolName, CancellationToken cancellationToken = default)
        {
            if (!OperatingSystem.IsWindows())
            {
                return Result.Failure("Pool management is only available on Windows.");
            }

            if (string.IsNullOrWhiteSpace(poolName))
            {
                return Result.Failure("Pool name is required");
            }

            return await _metrics.MeasureAsync<Result>("iis_command_duration", async () =>
            {
                try
                {
                    var serverName = Environment.MachineName;

                    if (cancellationToken.IsCancellationRequested)
                        return Result.Failure("Operation was cancelled");

                    var poolStatus = await GetStateAsync(serverName, true, $"Get-WebAppPoolState -Name '{poolName}'", cancellationToken);
                    if (poolStatus == "Stopped")
                    {
                        if (!await ExecuteScriptAsync(serverName, $"Start-WebAppPool -Name '{poolName}'", cancellationToken))
                        {
                            return Result.Failure($"Failed to start app pool {poolName}");
                        }

                        if (!await WaitForStateAsync(serverName, true, $"Get-WebAppPoolState -Name '{poolName}'", "Started", $"Application pool {poolName}", cancellationToken))
                        {
                            return Result.Failure($"Application pool {poolName} did not reach started state");
                        }
                        return Result.Success();
                    }
                    if (poolStatus == "Started")
                    {
                        return Result.Success();
                    }
                    return Result.Failure(poolStatus ?? "Unknown application pool status");
                }
                catch (OperationCanceledException)
                {
                    return Result.Failure("Operation was cancelled");
                }
                catch (Exception ex)
                {
                    var errorMsg = $"Failed to start app pool {poolName}: {ex.Message}";
                    _output.WriteLine($"[ERROR] {errorMsg}");
                    return Result.Failure(errorMsg, ex);
                }
            }, new() { ["operation"] = "start_app_pool", ["pool_name"] = poolName });
        }

        public async Task<Result> StartWebsiteAsync(string siteName, CancellationToken cancellationToken = default)
        {
            if (!OperatingSystem.IsWindows())
            {
                return Result.Failure("Website management is only available on Windows.");
            }

            if (string.IsNullOrWhiteSpace(siteName))
            {
                return Result.Failure("Site name is required");
            }

            return await _metrics.MeasureAsync<Result>("iis_command_duration", async () =>
            {
                try
                {
                    var serverName = Environment.MachineName;

                    if (cancellationToken.IsCancellationRequested)
                        return Result.Failure("Operation was cancelled");

                    var siteStatus = await GetStateAsync(serverName, false, $"Get-Website -Name '{siteName}'", cancellationToken);
                    if (siteStatus == "Stopped")
                    {
                        if (!await ExecuteScriptAsync(serverName, $"Start-Website -Name '{siteName}'", cancellationToken))
                        {
                            return Result.Failure($"Failed to start website {siteName}");
                        }

                        if (!await WaitForStateAsync(serverName, false, $"Get-Website -Name '{siteName}'", "Started", $"Website {siteName}", cancellationToken))
                        {
                            return Result.Failure($"Website {siteName} did not reach started state");
                        }
                        return Result.Success();
                    }
                    if (siteStatus == "Started")
                    {
                        return Result.Success();
                    }
                    return Result.Failure(siteStatus ?? "Unknown website status");
                }
                catch (OperationCanceledException)
                {
                    return Result.Failure("Operation was cancelled");
                }
                catch (Exception ex)
                {
                    var errorMsg = $"Failed to start website {siteName}: {ex.Message}";
                    _output.WriteLine($"[ERROR] {errorMsg}");
                    return Result.Failure(errorMsg, ex);
                }
            }, new() { ["operation"] = "start_website", ["site_name"] = siteName });
        }

        public async Task<Result> StartServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                return Result.Failure("Service name is required");
            }

            try
            {
                if (cancellationToken.IsCancellationRequested)
                    return Result.Failure("Operation was cancelled");

                var result = await _systemServiceManager.StartServiceAsync(serviceName);
                
                if (result)
                {
                    _output.WriteLine($"[INFO] Service {serviceName} started successfully");
                    return Result.Success();
                }
                else
                {
                    var errorMsg = $"Failed to start service {serviceName}";
                    _output.WriteLine($"[ERROR] {errorMsg}");
                    return Result.Failure(errorMsg);
                }
            }
            catch (OperationCanceledException)
            {
                return Result.Failure("Operation was cancelled");
            }
            catch (Exception ex)
            {
                var errorMsg = $"Failed to start service {serviceName}: {ex.Message}";
                _output.WriteLine($"[ERROR] {errorMsg}");
                return Result.Failure(errorMsg, ex);
            }
        }

        public async Task<Result> StopServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                return Result.Failure("Service name is required");
            }

            try
            {
                if (cancellationToken.IsCancellationRequested)
                    return Result.Failure("Operation was cancelled");

                var result = await _systemServiceManager.StopServiceAsync(serviceName);
                
                if (result)
                {
                    _output.WriteLine($"[INFO] Service {serviceName} stopped successfully");
                    return Result.Success();
                }
                else
                {
                    var errorMsg = $"Failed to stop service {serviceName}";
                    _output.WriteLine($"[ERROR] {errorMsg}");
                    return Result.Failure(errorMsg);
                }
            }
            catch (OperationCanceledException)
            {
                return Result.Failure("Operation was cancelled");
            }
            catch (Exception ex)
            {
                var errorMsg = $"Failed to stop service {serviceName}: {ex.Message}";
                _output.WriteLine($"[ERROR] {errorMsg}");
                return Result.Failure(errorMsg, ex);
            }
        }

        public async Task<Result<string>> GetAppPoolStatusAsync(string poolName, CancellationToken cancellationToken = default)
        {
            if (!OperatingSystem.IsWindows())
            {
                return Result<string>.Failure("Pool status check is only available on Windows.");
            }

            if (string.IsNullOrWhiteSpace(poolName))
            {
                return Result<string>.Failure("Pool name is required");
            }

            try
            {
                var serverName = Environment.MachineName;
                var status = await GetStateAsync(serverName, true, $"Get-WebAppPoolState -Name '{poolName}'", cancellationToken);
                return Result<string>.Success(status ?? "Unknown");
            }
            catch (Exception ex)
            {
                var errorMsg = $"Failed to get app pool status for {poolName}: {ex.Message}";
                _output.WriteLine($"[ERROR] {errorMsg}");
                return Result<string>.Failure(errorMsg, ex);
            }
        }

        public async Task<Result<string>> GetWebsiteStatusAsync(string siteName, CancellationToken cancellationToken = default)
        {
            if (!OperatingSystem.IsWindows())
            {
                return Result<string>.Failure("Website status check is only available on Windows.");
            }

            if (string.IsNullOrWhiteSpace(siteName))
            {
                return Result<string>.Failure("Site name is required");
            }

            try
            {
                var serverName = Environment.MachineName;
                var status = await GetStateAsync(serverName, false, $"Get-Website -Name '{siteName}'", cancellationToken);
                return Result<string>.Success(status ?? "Unknown");
            }
            catch (Exception ex)
            {
                var errorMsg = $"Failed to get website status for {siteName}: {ex.Message}";
                _output.WriteLine($"[ERROR] {errorMsg}");
                return Result<string>.Failure(errorMsg, ex);
            }
        }

        // Private helper methods
        private async Task<bool> ExecuteScriptAsync(string serverName, string script, CancellationToken cancellationToken = default)
        {
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

                await process.WaitForExitAsync(cancellationToken);

                if (process.ExitCode != 0)
                {
                    var errorText = await process.StandardError.ReadToEndAsync(cancellationToken);
                    _output.WriteLine($"[PS-ERROR] Script execution failed: {errorText}");
                    return false;
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[ERROR] PowerShell script execution failed: {ex.Message}");
                return false;
            }
        }

        private async Task<string?> GetStateAsync(string serverName, bool isPool, string expression, CancellationToken cancellationToken = default)
        {
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

                var outputText = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                var errorText = await process.StandardError.ReadToEndAsync(cancellationToken);
                await process.WaitForExitAsync(cancellationToken);

                if (!string.IsNullOrWhiteSpace(errorText))
                {
                    if (errorText.Contains("ObjectNotFound") || errorText.Contains("PathNotFound"))
                    {
                        return "not found";
                    }
                    return $"State check failed: {errorText}";
                }

                var lines = outputText.Trim().Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 3)
                {
                    return "not found";
                }

                return lines[2];
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[ERROR] PowerShell state fetch failed: {ex.Message}");
                return null;
            }
        }

        private async Task<bool> WaitForStateAsync(string serverName, bool isPool, string query, string desiredState, string name, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(query)) throw new ArgumentNullException(nameof(query));
            if (string.IsNullOrEmpty(desiredState)) throw new ArgumentNullException(nameof(desiredState));
            if (string.IsNullOrEmpty(name)) throw new ArgumentNullException(nameof(name));
            string? currentState;
            var maxAttempts = 120;
            var attempts = 0;
            do
            {
                if (cancellationToken.IsCancellationRequested)
                    return false;

                currentState = await GetStateAsync(serverName, isPool, query, cancellationToken);
                if (string.Equals(currentState, desiredState, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                _output.WriteLine($"[WAIT] {name} current state: {currentState ?? "unknown"}, waiting...");
                await Task.Delay(5000, cancellationToken);
                attempts++;
            }
            while (attempts < maxAttempts && !string.Equals(currentState, desiredState, StringComparison.OrdinalIgnoreCase));
            return string.Equals(currentState, desiredState, StringComparison.OrdinalIgnoreCase);
        }
    }
}
