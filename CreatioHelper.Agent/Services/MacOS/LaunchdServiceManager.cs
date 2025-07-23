using System.Diagnostics;
using System.Runtime.Versioning;
using CreatioHelper.Core.Abstractions;
using CreatioHelper.Core.Models;

namespace CreatioHelper.Agent.Services.MacOS;

[SupportedOSPlatform("macos")]
public class LaunchdServiceManager : IWebServerService
{
    private readonly ILogger<LaunchdServiceManager> _logger;

    public LaunchdServiceManager(ILogger<LaunchdServiceManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsSupported() => OperatingSystem.IsMacOS();

    public async Task<WebServerResult> StartSiteAsync(string serviceName)
    {
        if (!IsSupported())
        {
            return new WebServerResult { Success = false, Message = "Launchd management is only available on macOS." };
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return new WebServerResult { Success = false, Message = "Service name is required." };
        }

        try
        {
            var currentState = await GetServiceStateAsync(serviceName);
            if (currentState == "running")
            {
                return new WebServerResult { Success = true, Message = $"Service {serviceName} is already running." };
            }

            // Load the service if it is not already loaded
            if (currentState == "not loaded")
            {
                if (!await ExecuteLaunchctlAsync($"load {serviceName}"))
                {
                    return new WebServerResult { Success = false, Message = $"Failed to load service {serviceName}." };
                }
            }

            // Start the service
            if (!await ExecuteLaunchctlAsync($"start {serviceName}"))
            {
                return new WebServerResult { Success = false, Message = $"Failed to start service {serviceName}." };
            }

            var success = await WaitForServiceStateAsync(serviceName, "running");
            return new WebServerResult 
            { 
                Success = success, 
                Message = success ? $"Service {serviceName} started successfully." : $"Failed to start service {serviceName}."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting service {ServiceName}", serviceName);
            return new WebServerResult { Success = false, Message = ex.Message };
        }
    }

    public async Task<WebServerResult> StopSiteAsync(string serviceName)
    {
        if (!IsSupported())
        {
            return new WebServerResult { Success = false, Message = "Launchd management is only available on macOS." };
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return new WebServerResult { Success = false, Message = "Service name is required." };
        }

        try
        {
            var currentState = await GetServiceStateAsync(serviceName);
            if (currentState == "not running" || currentState == "not loaded")
            {
                return new WebServerResult { Success = true, Message = $"Service {serviceName} is already stopped." };
            }

            if (currentState == "running")
            {
                if (!await ExecuteLaunchctlAsync($"stop {serviceName}"))
                {
                    return new WebServerResult { Success = false, Message = $"Failed to stop service {serviceName}." };
                }
            }

            var success = await WaitForServiceStateAsync(serviceName, "not running");
            return new WebServerResult 
            { 
                Success = success, 
                Message = success ? $"Service {serviceName} stopped successfully." : $"Failed to stop service {serviceName}."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping service {ServiceName}", serviceName);
            return new WebServerResult { Success = false, Message = ex.Message };
        }
    }

    // Launchd has no separate pools, so use the same methods
    public Task<WebServerResult> StartAppPoolAsync(string serviceName) => StartSiteAsync(serviceName);
    public Task<WebServerResult> StopAppPoolAsync(string serviceName) => StopSiteAsync(serviceName);

    public async Task<WebServerResult> GetSiteStatusAsync(string serviceName)
    {
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
            return new WebServerResult { Success = false, Message = ex.Message };
        }
    }

    public Task<WebServerResult> GetAppPoolStatusAsync(string serviceName) => GetSiteStatusAsync(serviceName);

    public async Task<List<WebServerStatus>> GetAllSitesAsync()
    {
        var services = new List<WebServerStatus>();
        
        try
        {
            // Retrieve all loaded services
            var result = await ExecuteLaunchctlWithOutputAsync("list");
            
            if (!string.IsNullOrWhiteSpace(result))
            {
                var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                
                foreach (var line in lines.Skip(1)) // Skip the header line
                {
                    var parts = line.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        var pid = parts[0];
                        var status = parts[1];
                        var serviceName = parts[2];
                        
                        // Filter only the services we are interested in
                        if (serviceName.Contains("kestrel") || serviceName.Contains("dotnet") || serviceName.Contains("webapp"))
                        {
                            var isRunning = pid != "-" && !string.IsNullOrWhiteSpace(pid);
                            
                            services.Add(new WebServerStatus
                            {
                                Name = serviceName,
                                Status = isRunning ? "running" : "loaded",
                                Type = "LaunchdService",
                                Port = "", // TODO: add logic for retrieving the port
                                IsRunning = isRunning,
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

    private async Task<bool> ExecuteLaunchctlAsync(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "launchctl",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.LogError("Failed to start launchctl process");
                return false;
            }

            var errorText = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(errorText) && !errorText.Contains("already loaded"))
            {
                _logger.LogError("Launchctl error: {Error}", errorText);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Launchctl process failed");
            return false;
        }
    }

    private async Task<string?> ExecuteLaunchctlWithOutputAsync(string arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "launchctl",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                _logger.LogError("Failed to start launchctl process");
                return null;
            }

            var outputText = await process.StandardOutput.ReadToEndAsync();
            var errorText = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(errorText))
            {
                _logger.LogError("Launchctl error: {Error}", errorText);
                return null;
            }

            return outputText?.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Launchctl process failed");
            return null;
        }
    }

    private async Task<string?> GetServiceStateAsync(string serviceName)
    {
        try
        {
            var result = await ExecuteLaunchctlWithOutputAsync($"print {serviceName}");
            if (string.IsNullOrWhiteSpace(result))
            {
                return "not loaded";
            }
            
            // Parse launchctl print output to determine the state
            if (result.Contains("state = running"))
                return "running";
            if (result.Contains("state = waiting"))
                return "not running";
            
            return "loaded";
        }
        catch
        {
            return "not loaded";
        }
    }

    private async Task<string?> GetServiceDetailsAsync(string serviceName)
    {
        var result = await ExecuteLaunchctlWithOutputAsync($"print {serviceName}");
        return result?.Trim();
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