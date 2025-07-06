using System.Diagnostics;
using System.Runtime.Versioning;
using CreatioHelper.Core.Abstractions;
using CreatioHelper.Core.Models;

namespace CreatioHelper.Agent.Services.Linux;

[SupportedOSPlatform("linux")]
public class SystemdServiceManager : IWebServerService
{
    private readonly ILogger<SystemdServiceManager> _logger;

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
            return new WebServerResult { Success = false, Message = ex.Message };
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
            return new WebServerResult { Success = false, Message = ex.Message };
        }
    }

    // Для systemd нет отдельных пулов, поэтому используем те же методы
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
                Data = new { ServiceName = serviceName, Status = state ?? "Unknown", Details = details }
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
            // Получаем все сервисы с определенным паттерном (например, все .NET приложения)
            var result = await ExecuteSystemctlWithOutputAsync("list-units --type=service --state=active,inactive --no-pager --plain | grep -E '\\.(service)$'");
            
            if (!string.IsNullOrWhiteSpace(result))
            {
                var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                
                foreach (var line in lines)
                {
                    var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 4)
                    {
                        var serviceName = parts[0];
                        var loadState = parts[1];
                        var activeState = parts[2];
                        var subState = parts[3];
                        
                        // Фильтруем только интересующие нас сервисы (например, содержащие 'kestrel' или 'dotnet')
                        if (serviceName.Contains("kestrel") || serviceName.Contains("dotnet") || serviceName.Contains("webapp"))
                        {
                            services.Add(new WebServerStatus
                            {
                                Name = serviceName,
                                Status = activeState,
                                Type = "SystemdService",
                                Port = "", // Можно добавить логику получения порта из конфига
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

            return outputText?.Trim();
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

    private async Task<bool> WaitForServiceStateAsync(string serviceName, string desiredState)
    {
        var attempts = 0;
        const int maxAttempts = 12; // 1 минута ожидания
        
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