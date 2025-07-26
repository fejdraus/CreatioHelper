using System.Diagnostics;
using CreatioHelper.Domain.Entities;

namespace CreatioHelper.Agent.Services.Windows;

public class WindowsServiceManager : IWebServerService
{
    private readonly ILogger<WindowsServiceManager> _logger;

    public WindowsServiceManager(ILogger<WindowsServiceManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsSupported() => OperatingSystem.IsWindows();

    public async Task<WebServerResult> StartSiteAsync(string serviceName)
    {
        if (!IsSupported())
        {
            return new WebServerResult { Success = false, Message = "Windows Service management is only available on Windows." };
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return new WebServerResult { Success = false, Message = "Service name is required." };
        }

        try
        {
            var currentState = await GetServiceStateAsync(serviceName);
            if (currentState == "Running")
            {
                return new WebServerResult { Success = true, Message = $"Service {serviceName} is already running." };
            }

            if (currentState == "Stopped")
            {
                if (!await ExecuteServiceCommandAsync($"Start-Service -Name '{serviceName}'"))
                {
                    return new WebServerResult { Success = false, Message = $"Failed to start service {serviceName}." };
                }
            }

            var success = await WaitForServiceStateAsync(serviceName, "Running");
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
            return new WebServerResult { Success = false, Message = "Windows Service management is only available on Windows." };
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return new WebServerResult { Success = false, Message = "Service name is required." };
        }

        try
        {
            var currentState = await GetServiceStateAsync(serviceName);
            if (currentState == "Stopped")
            {
                return new WebServerResult { Success = true, Message = $"Service {serviceName} is already stopped." };
            }

            if (currentState == "Running")
            {
                if (!await ExecuteServiceCommandAsync($"Stop-Service -Name '{serviceName}' -Force"))
                {
                    return new WebServerResult { Success = false, Message = $"Failed to stop service {serviceName}." };
                }
            }

            var success = await WaitForServiceStateAsync(serviceName, "Stopped");
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

    // Windows Services do not have separate pools
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
            // Retrieve all services that might be Kestrel/Creatio
            var result = await ExecuteServiceCommandWithOutputAsync("Get-Service | Where-Object {$_.Name -like '*creatio*' -or $_.Name -like '*kestrel*' -or $_.Name -like '*dotnet*' -or $_.DisplayName -like '*creatio*'} | Select-Object Name, Status, DisplayName | ConvertTo-Json");
            
            if (!string.IsNullOrWhiteSpace(result))
            {
                try
                {
                    var servicesData = JsonSerializer.Deserialize<WindowsServiceInfo[]>(result, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    
                    if (servicesData != null)
                    {
                        foreach (var serviceInfo in servicesData)
                        {
                            var isRunning = string.Equals(serviceInfo.Status, "Running", StringComparison.OrdinalIgnoreCase);
                            
                            services.Add(new WebServerStatus
                            {
                                Name = serviceInfo.Name,
                                Status = serviceInfo.Status,
                                Type = "WindowsService",
                                Port = await GetServicePortAsync(serviceInfo.Name), // Try to obtain the port
                                IsRunning = isRunning,
                                LastChecked = DateTime.UtcNow
                            });
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogError(ex, "Error parsing services JSON data");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all Windows services");
        }

        return services;
    }

    public Task<List<WebServerStatus>> GetAllAppPoolsAsync() => GetAllSitesAsync();

    #region Private Methods

    private async Task<bool> ExecuteServiceCommandAsync(string command)
    {
        try
        {
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
                _logger.LogError("Failed to start PowerShell process");
                return false;
            }

            var errorText = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(errorText))
            {
                _logger.LogError("PowerShell error: {Error}", errorText);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerShell process failed");
            return false;
        }
    }

    private async Task<string?> ExecuteServiceCommandWithOutputAsync(string command)
    {
        try
        {
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
                _logger.LogError("Failed to start PowerShell process");
                return null;
            }

            var outputText = await process.StandardOutput.ReadToEndAsync();
            var errorText = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (!string.IsNullOrWhiteSpace(errorText))
            {
                _logger.LogError("PowerShell error: {Error}", errorText);
                return null;
            }

            return outputText.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerShell process failed");
            return null;
        }
    }

    private async Task<string?> GetServiceStateAsync(string serviceName)
    {
        var result = await ExecuteServiceCommandWithOutputAsync($"(Get-Service -Name '{serviceName}').Status");
        return result?.Split('\n', '\r').LastOrDefault()?.Trim();
    }

    private async Task<string?> GetServiceDetailsAsync(string serviceName)
    {
        var result = await ExecuteServiceCommandWithOutputAsync($"Get-Service -Name '{serviceName}' | Select-Object * | ConvertTo-Json");
        return result?.Trim();
    }

    private async Task<string> GetServicePortAsync(string serviceName)
    {
        try
        {
            var pidText = await ExecuteServiceCommandWithOutputAsync(
                $"(Get-WmiObject -Class Win32_Service -Filter \"Name='{serviceName}'\").ProcessId");

            if (int.TryParse(pidText?.Split('\n', '\r').LastOrDefault()?.Trim(), out var pid) && pid > 0)
            {
                var portText = await ExecuteServiceCommandWithOutputAsync(
                    $"Get-NetTCPConnection -OwningProcess {pid} -State Listen | Select-Object -First 1 -ExpandProperty LocalPort");
                return portText?.Split('\n', '\r').FirstOrDefault()?.Trim() ?? string.Empty;
            }

            return string.Empty;
        }
        catch
        {
            return "";
        }
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
