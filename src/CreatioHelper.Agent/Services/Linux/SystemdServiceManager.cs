using System.Diagnostics;
using System.Runtime.Versioning;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Agent.Services.Linux;

[SupportedOSPlatform("linux")]
public class SystemdServiceManager : IWebServerService
{
    private readonly ILogger<SystemdServiceManager> _logger;

    // Regex pattern for valid systemd service names (alphanumeric, dots, hyphens, underscores, @)
    private static readonly System.Text.RegularExpressions.Regex ValidServiceNameRegex =
        new(@"^[a-zA-Z0-9._@-]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Validates that a service name contains only safe characters.
    /// </summary>
    private static bool IsValidServiceName(string serviceName)
        => !string.IsNullOrWhiteSpace(serviceName) && ValidServiceNameRegex.IsMatch(serviceName);

    public SystemdServiceManager(ILogger<SystemdServiceManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsSupported() => OperatingSystem.IsLinux();

    public async Task<WebServerResult> StartSiteAsync(string serviceName)
    {
        if (!IsSupported())
        {
            return new WebServerResult { Success = false, Message = "Systemd management is only available on Linux." };
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return new WebServerResult { Success = false, Message = "Service name is required." };
        }

        if (!IsValidServiceName(serviceName))
        {
            return new WebServerResult { Success = false, Message = "Invalid service name format." };
        }

        try
        {
            var currentState = await GetServiceStateAsync(serviceName);
            if (currentState == "active")
            {
                return new WebServerResult { Success = true, Message = $"Service {serviceName} is already running." };
            }

            if (currentState == "inactive" || currentState == "failed")
            {
                if (!await ExecuteSystemctlAsync($"start {serviceName}"))
                {
                    return new WebServerResult { Success = false, Message = $"Failed to start service {serviceName}." };
                }
            }

            var success = await WaitForServiceStateAsync(serviceName, "active");
            return new WebServerResult
            {
                Success = success,
                Message = success ? $"Service {serviceName} started successfully." : $"Failed to start service {serviceName}."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting service {ServiceName}", serviceName);
            return new WebServerResult { Success = false, Message = "Failed to start service." };
        }
    }

    public async Task<WebServerResult> StopSiteAsync(string serviceName)
    {
        if (!IsSupported())
        {
            return new WebServerResult { Success = false, Message = "Systemd management is only available on Linux." };
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return new WebServerResult { Success = false, Message = "Service name is required." };
        }

        if (!IsValidServiceName(serviceName))
        {
            return new WebServerResult { Success = false, Message = "Invalid service name format." };
        }

        try
        {
            var currentState = await GetServiceStateAsync(serviceName);
            if (currentState == "inactive")
            {
                return new WebServerResult { Success = true, Message = $"Service {serviceName} is already stopped." };
            }

            if (currentState == "active")
            {
                if (!await ExecuteSystemctlAsync($"stop {serviceName}"))
                {
                    return new WebServerResult { Success = false, Message = $"Failed to stop service {serviceName}." };
                }
            }

            var success = await WaitForServiceStateAsync(serviceName, "inactive");
            return new WebServerResult
            {
                Success = success,
                Message = success ? $"Service {serviceName} stopped successfully." : $"Failed to stop service {serviceName}."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping service {ServiceName}", serviceName);
            return new WebServerResult { Success = false, Message = "Failed to stop service." };
        }
    }

    // Systemd has no separate pools, so use the same methods
    public Task<WebServerResult> StartAppPoolAsync(string serviceName) => StartSiteAsync(serviceName);
    public Task<WebServerResult> StopAppPoolAsync(string serviceName) => StopSiteAsync(serviceName);

    public async Task<WebServerResult> GetSiteStatusAsync(string serviceName)
    {
        if (!IsValidServiceName(serviceName))
        {
            return new WebServerResult { Success = false, Message = "Invalid service name format." };
        }

        try
        {
            var state = await GetServiceStateAsync(serviceName);
            var details = await GetServiceDetailsAsync(serviceName);

            return new WebServerResult
            {
                Success = true,
                Message = $"Service {serviceName} status retrieved successfully.",
                Data = new Data { ServiceName = serviceName, Status = state ?? "Unknown", Details = details }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting service status {ServiceName}", serviceName);
            return new WebServerResult { Success = false, Message = "Failed to get service status." };
        }
    }

    public Task<WebServerResult> GetAppPoolStatusAsync(string serviceName) => GetSiteStatusAsync(serviceName);

    public async Task<List<WebServerStatus>> GetAllSitesAsync()
    {
        var services = new List<WebServerStatus>();
        
        try
        {
            // Retrieve all services matching a pattern (e.g., all .NET apps)
            var result = await ExecuteSystemctlWithOutputAsync("list-units --type=service --state=active,inactive --no-pager --plain");
            
            if (!string.IsNullOrWhiteSpace(result))
            {
                var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                
                foreach (var line in lines)
                {
                    var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4)
                    {
                        var serviceName = parts[0];
                        var loadState = parts[1];
                        var activeState = parts[2];
                        var subState = parts[3];
                        
                        // Filter only .service files and relevant services (e.g., containing 'kestrel' or 'dotnet')
                        if (serviceName.EndsWith(".service") && 
                            (serviceName.Contains("kestrel") || serviceName.Contains("dotnet") || serviceName.Contains("webapp")))
                        {
                            services.Add(new WebServerStatus
                            {
                                Name = serviceName,
                                Status = activeState,
                                Type = "SystemdService",
                                Port = await GetServicePortAsync(serviceName),
                                IsRunning = string.Equals(activeState, "active", StringComparison.OrdinalIgnoreCase),
                                LastChecked = DateTime.UtcNow
                            });
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all services");
        }

        return services;
    }

    public Task<List<WebServerStatus>> GetAllAppPoolsAsync() => GetAllSitesAsync();

    #region Private Methods

    private async Task<bool> ExecuteSystemctlAsync(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.LogError("Failed to start systemctl process");
                return false;
            }

            var errorText = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(errorText))
            {
                _logger.LogError("Systemctl error: {Error}", errorText);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Systemctl process failed");
            return false;
        }
    }

    private async Task<string?> ExecuteSystemctlWithOutputAsync(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "systemctl",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.LogError("Failed to start systemctl process");
                return null;
            }

            var outputText = await process.StandardOutput.ReadToEndAsync();
            var errorText = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(errorText))
            {
                _logger.LogError("Systemctl error: {Error}", errorText);
                return null;
            }

            return outputText.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Systemctl process failed");
            return null;
        }
    }

    private async Task<string?> GetServiceStateAsync(string serviceName)
    {
        var result = await ExecuteSystemctlWithOutputAsync($"is-active {serviceName}");
        return result?.Trim();
    }

    private async Task<string?> GetServiceDetailsAsync(string serviceName)
    {
        var result = await ExecuteSystemctlWithOutputAsync($"status {serviceName} --no-pager --lines=0");
        return result?.Trim();
    }

    private async Task<string> GetServicePortAsync(string serviceName)
    {
        // serviceName is already validated by callers using IsValidServiceName
        try
        {
            var pidText = await ExecuteSystemctlWithOutputAsync($"show -p MainPID --value {serviceName}");
            if (int.TryParse(pidText?.Trim(), out var pid) && pid > 0)
            {
                // pid is an integer, so it's safe to use in the command (no injection possible)
                var startInfo = new ProcessStartInfo
                {
                    FileName = "sh",
                    Arguments = $"-c \"ss -ltnp | grep 'pid={pid},'\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                    return string.Empty;

                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                var match = System.Text.RegularExpressions.Regex.Match(output, @":(\d+)\s");
                if (match.Success)
                    return match.Groups[1].Value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get service port for {ServiceName}", serviceName);
        }

        return string.Empty;
    }

    private async Task<bool> WaitForServiceStateAsync(string serviceName, string desiredState)
    {
        var attempts = 0;
        const int maxAttempts = 12; // wait up to 1 minute
        
        while (attempts < maxAttempts)
        {
            var currentState = await GetServiceStateAsync(serviceName);
            if (string.Equals(currentState, desiredState, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            _logger.LogInformation("Waiting for service {ServiceName} to reach state {DesiredState}, current: {CurrentState}", 
                serviceName, desiredState, currentState ?? "unknown");
            
            await Task.Delay(5000);
            attempts++;
        }

        return false;
    }

    #endregion
}