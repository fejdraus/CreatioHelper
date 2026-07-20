using System.Diagnostics;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Shared.Utils;
using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.Infrastructure.Services
{
    public class SystemServiceManager : ISystemServiceManager
    {
        private readonly IOutputWriter _output;

        public SystemServiceManager(IOutputWriter output)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        public async Task<bool> StartServiceAsync(string serviceName)
        {
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                _output.WriteLine("[ERROR] Service name is required.");
                return false;
            }

            try
            {
                var currentState = await GetServiceStateAsync(serviceName).ConfigureAwait(false);
                if (IsServiceRunning(currentState))
                {
                    _output.WriteLine($"[INFO] Service {serviceName} is already running.");
                    return true;
                }

                var command = GetStartServiceCommand(serviceName);
                if (!await ExecuteCommandAsync(command).ConfigureAwait(false))
                {
                    return false;
                }

                return await WaitForServiceStateAsync(serviceName, true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[ERROR] Failed to start service '{serviceName}': {ex.Message}");
                return false;
            }
        }

        public async Task<bool> StopServiceAsync(string serviceName)
        {
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                _output.WriteLine("[ERROR] Service name is required.");
                return false;
            }

            try
            {
                var currentState = await GetServiceStateAsync(serviceName).ConfigureAwait(false);
                if (IsServiceStopped(currentState))
                {
                    _output.WriteLine($"[INFO] Service {serviceName} is already stopped.");
                    return true;
                }

                var command = GetStopServiceCommand(serviceName);
                if (!await ExecuteCommandAsync(command).ConfigureAwait(false))
                {
                    return false;
                }

                return await WaitForServiceStateAsync(serviceName, false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[ERROR] Failed to stop service '{serviceName}': {ex.Message}");
                return false;
            }
        }

        public async Task<string?> GetServiceStateAsync(string serviceName)
        {
            if (string.IsNullOrWhiteSpace(serviceName))
                return "Service name is empty";

            try
            {
                var command = GetServiceStatusCommand(serviceName);
                var result = await ExecuteCommandWithOutputAsync(command).ConfigureAwait(false);

                if (OperatingSystem.IsWindows())
                {
                    return result?.Trim();
                }
                else if (OperatingSystem.IsLinux())
                {
                    if (result?.Contains("active (running)") == true)
                        return "active";
                    if (result?.Contains("inactive") == true)
                        return "inactive";
                    if (result?.Contains("failed") == true)
                        return "failed";
                    return "unknown";
                }

                return result?.Trim() ?? "Error";
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[ERROR] Failed to get service state for '{serviceName}': {ex.Message}");
                return "Error";
            }
        }

        private string GetStartServiceCommand(string serviceName)
        {
            if (OperatingSystem.IsWindows())
            {
                return $"Start-Service -Name '{PowerShellRunner.EscapeSingleQuoted(serviceName)}'";
            }
            else if (OperatingSystem.IsLinux())
            {
                return $"systemctl start {serviceName}";
            }

            throw new PlatformNotSupportedException("Service management is not supported on this platform.");
        }

        private string GetStopServiceCommand(string serviceName)
        {
            if (OperatingSystem.IsWindows())
            {
                return $"Stop-Service -Name '{PowerShellRunner.EscapeSingleQuoted(serviceName)}' -Force";
            }
            else if (OperatingSystem.IsLinux())
            {
                return $"systemctl stop {serviceName}";
            }

            throw new PlatformNotSupportedException("Service management is not supported on this platform.");
        }

        private string GetServiceStatusCommand(string serviceName)
        {
            if (OperatingSystem.IsWindows())
            {
                return $"(Get-Service -Name '{PowerShellRunner.EscapeSingleQuoted(serviceName)}').Status";
            }
            else if (OperatingSystem.IsLinux())
            {
                return $"systemctl status {serviceName}";
            }

            throw new PlatformNotSupportedException("Service management is not supported on this platform.");
        }

        private bool IsServiceRunning(string? state)
        {
            if (string.IsNullOrWhiteSpace(state)) return false;

            return state.Equals("Running", StringComparison.OrdinalIgnoreCase) ||
                   state.Equals("active", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsServiceStopped(string? state)
        {
            if (string.IsNullOrWhiteSpace(state)) return false;

            return state.Equals("Stopped", StringComparison.OrdinalIgnoreCase) ||
                   state.Equals("inactive", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<bool> ExecuteCommandAsync(string command)
        {
            try
            {
                var result = await ShellRunner.RunAsync(GetProcessStartInfo(command)).ConfigureAwait(false);
                if (result is null)
                {
                    _output.WriteLine("[ERROR] Failed to start process for service management.");
                    return false;
                }

                if (result.HasError)
                {
                    _output.WriteLine($"[ERROR] Service operation failed: {result.Error.Trim()}");
                    return false;
                }

                return result.ExitCode == 0;
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[ERROR] Process execution failed: {ex.Message}");
                return false;
            }
        }

        private async Task<string?> ExecuteCommandWithOutputAsync(string command)
        {
            try
            {
                var result = await ShellRunner.RunAsync(GetProcessStartInfo(command)).ConfigureAwait(false);
                if (result is null)
                {
                    _output.WriteLine("[ERROR] Failed to start process for service status check.");
                    return null;
                }

                if (result.HasError)
                {
                    return null;
                }

                return result.Output;
            }
            catch (Exception ex)
            {
                _output.WriteLine($"[ERROR] Failed to execute command: {ex.Message}");
                return null;
            }
        }

        private ProcessStartInfo GetProcessStartInfo(string command)
        {
            if (OperatingSystem.IsWindows())
            {
                return new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }
            else if (OperatingSystem.IsLinux())
            {
                return new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = $"-c \"{command}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
            }

            throw new PlatformNotSupportedException("Service management is not supported on this platform.");
        }

        private async Task<bool> WaitForServiceStateAsync(string serviceName, bool waitForRunning)
        {
            var attempts = 0;
            const int maxAttempts = 12;

            while (attempts < maxAttempts)
            {
                var currentState = await GetServiceStateAsync(serviceName).ConfigureAwait(false);

                bool stateMatches = waitForRunning ? IsServiceRunning(currentState) : IsServiceStopped(currentState);
                if (stateMatches)
                {
                    return true;
                }

                _output.WriteLine($"[WAIT] Service {serviceName} current state: {currentState ?? "unknown"}, waiting...");
                await Task.Delay(5000).ConfigureAwait(false);
                attempts++;
            }

            _output.WriteLine($"[ERROR] Service {serviceName} did not reach desired state within timeout.");
            return false;
        }
    }
}
