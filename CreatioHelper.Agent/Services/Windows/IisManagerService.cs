using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using CreatioHelper.Core.Abstractions;
using CreatioHelper.Core.Models;

namespace CreatioHelper.Agent.Services.Windows;

[SupportedOSPlatform("windows")]
public class IisManagerService : IWebServerService
{
    private readonly ILogger<IisManagerService> _logger;
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public IisManagerService(ILogger<IisManagerService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public bool IsSupported() => OperatingSystem.IsWindows();

    public async Task<WebServerResult> StartSiteAsync(string siteName)
    {
        if (!IsSupported())
        {
            return new WebServerResult { Success = false, Message = "IIS management is only available on Windows." };
        }

        if (string.IsNullOrWhiteSpace(siteName))
        {
            return new WebServerResult { Success = false, Message = "Site name is required." };
        }

        try
        {
            var currentState = await GetSiteStateAsync(siteName);
            if (currentState == "Started")
            {
                return new WebServerResult { Success = true, Message = $"Site {siteName} is already running." };
            }

            if (currentState == "Stopped")
            {
                if (!await ExecuteScriptAsync($"Start-Website -Name '{siteName}'"))
                {
                    return new WebServerResult { Success = false, Message = $"Failed to start site {siteName}." };
                }
            }

            var success = await WaitForSiteStateAsync(siteName, "Started");
            return new WebServerResult 
            { 
                Success = success, 
                Message = success ? $"Site {siteName} started successfully." : $"Failed to start site {siteName}."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting site {SiteName}", siteName);
            return new WebServerResult { Success = false, Message = ex.Message };
        }
    }

    public async Task<WebServerResult> StopSiteAsync(string siteName)
    {
        if (!IsSupported())
        {
            return new WebServerResult { Success = false, Message = "IIS management is only available on Windows." };
        }

        if (string.IsNullOrWhiteSpace(siteName))
        {
            return new WebServerResult { Success = false, Message = "Site name is required." };
        }

        try
        {
            var currentState = await GetSiteStateAsync(siteName);
            if (currentState == "Stopped")
            {
                return new WebServerResult { Success = true, Message = $"Site {siteName} is already stopped." };
            }

            if (currentState == "Started")
            {
                if (!await ExecuteScriptAsync($"Stop-Website -Name '{siteName}'"))
                {
                    return new WebServerResult { Success = false, Message = $"Failed to stop site {siteName}." };
                }
            }

            var success = await WaitForSiteStateAsync(siteName, "Stopped");
            return new WebServerResult 
            { 
                Success = success, 
                Message = success ? $"Site {siteName} stopped successfully." : $"Failed to stop site {siteName}."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping site {SiteName}", siteName);
            return new WebServerResult { Success = false, Message = ex.Message };
        }
    }

    public async Task<WebServerResult> StartAppPoolAsync(string poolName)
    {
        if (!IsSupported())
        {
            return new WebServerResult { Success = false, Message = "IIS management is only available on Windows." };
        }

        if (string.IsNullOrWhiteSpace(poolName))
        {
            return new WebServerResult { Success = false, Message = "Pool name is required." };
        }

        try
        {
            var currentState = await GetAppPoolStateAsync(poolName);
            if (currentState == "Started")
            {
                return new WebServerResult { Success = true, Message = $"App pool {poolName} is already running." };
            }

            if (currentState == "Stopped")
            {
                if (!await ExecuteScriptAsync($"Start-WebAppPool -Name '{poolName}'"))
                {
                    return new WebServerResult { Success = false, Message = $"Failed to start app pool {poolName}." };
                }
            }

            var success = await WaitForAppPoolStateAsync(poolName, "Started");
            return new WebServerResult 
            { 
                Success = success, 
                Message = success ? $"App pool {poolName} started successfully." : $"Failed to start app pool {poolName}."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting app pool {PoolName}", poolName);
            return new WebServerResult { Success = false, Message = ex.Message };
        }
    }

    public async Task<WebServerResult> StopAppPoolAsync(string poolName)
    {
        if (!IsSupported())
        {
            return new WebServerResult { Success = false, Message = "IIS management is only available on Windows." };
        }

        if (string.IsNullOrWhiteSpace(poolName))
        {
            return new WebServerResult { Success = false, Message = "Pool name is required." };
        }

        try
        {
            var currentState = await GetAppPoolStateAsync(poolName);
            if (currentState == "Stopped")
            {
                return new WebServerResult { Success = true, Message = $"App pool {poolName} is already stopped." };
            }

            if (currentState == "Started")
            {
                if (!await ExecuteScriptAsync($"Stop-WebAppPool -Name '{poolName}'"))
                {
                    return new WebServerResult { Success = false, Message = $"Failed to stop app pool {poolName}." };
                }
            }

            var success = await WaitForAppPoolStateAsync(poolName, "Stopped");
            return new WebServerResult 
            { 
                Success = success, 
                Message = success ? $"App pool {poolName} stopped successfully." : $"Failed to stop app pool {poolName}."
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping app pool {PoolName}", poolName);
            return new WebServerResult { Success = false, Message = ex.Message };
        }
    }

    public async Task<WebServerResult> GetSiteStatusAsync(string siteName)
    {
        try
        {
            var state = await GetSiteStateAsync(siteName);
            return new WebServerResult 
            { 
                Success = true, 
                Message = $"Site {siteName} status retrieved successfully.",
                Data = new Data { ServiceName = siteName, Status = state ?? "Unknown" }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting site status {SiteName}", siteName);
            return new WebServerResult { Success = false, Message = ex.Message };
        }
    }

    public async Task<WebServerResult> GetAppPoolStatusAsync(string poolName)
    {
        try
        {
            var state = await GetAppPoolStateAsync(poolName);
            return new WebServerResult 
            { 
                Success = true, 
                Message = $"App pool {poolName} status retrieved successfully.",
                Data = new Data { PoolName = poolName, Status = state ?? "Unknown" }
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting app pool status {PoolName}", poolName);
            return new WebServerResult { Success = false, Message = ex.Message };
        }
    }

    public async Task<List<WebServerStatus>> GetAllSitesAsync()
    {
        var sites = new List<WebServerStatus>();
        
        try
        {
            var script = "Get-Website | Select-Object Name, State, @{Name='Port';Expression={($_.bindings.Collection.bindingInformation.Split(':')[1])}} | ConvertTo-Json";
            var result = await ExecuteScriptWithOutputAsync(script);
            
            if (!string.IsNullOrWhiteSpace(result))
            {
                _logger.LogDebug("Sites JSON data: {Result}", result);
                
                var sitesData = JsonSerializer.Deserialize<IisSiteInfo[]>(result, JsonOptions);
                
                if (sitesData != null)
                {
                    foreach (var siteInfo in sitesData)
                    {
                        var isRunning = string.Equals(siteInfo.State, "Started", StringComparison.OrdinalIgnoreCase);
                        
                        sites.Add(new WebServerStatus
                        {
                            Name = siteInfo.Name,
                            Status = siteInfo.State,
                            Type = "Site",
                            Port = siteInfo.Port,
                            IsRunning = isRunning,
                            LastChecked = DateTime.UtcNow
                        });
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Error parsing sites JSON data");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all sites");
        }

        return sites;
    }

    public async Task<List<WebServerStatus>> GetAllAppPoolsAsync()
    {
        var appPools = new List<WebServerStatus>();
        
        try
        {
            var script = "Get-WebAppPoolState | Select-Object Name, Value | ConvertTo-Json";
            var result = await ExecuteScriptWithOutputAsync(script);
            
            if (!string.IsNullOrWhiteSpace(result))
            {
                _logger.LogDebug("App pools JSON data: {Result}", result);
                
                var appPoolsData = JsonSerializer.Deserialize<IisAppPoolInfo[]>(result, JsonOptions);
                
                if (appPoolsData != null)
                {
                    foreach (var poolInfo in appPoolsData)
                    {
                        var isRunning = string.Equals(poolInfo.Value, "Started", StringComparison.OrdinalIgnoreCase);
                        
                        appPools.Add(new WebServerStatus
                        {
                            Name = poolInfo.Name,
                            Status = poolInfo.Value,
                            Type = "AppPool",
                            Port = "", // У App Pools нет портов
                            IsRunning = isRunning,
                            LastChecked = DateTime.UtcNow
                        });
                    }
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Error parsing app pools JSON data");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all app pools");
        }

        return appPools;
    }

    #region Private Methods (адаптированные из RemoteIisManager)

    private async Task<bool> ExecuteScriptAsync(string script)
    {
        if (string.IsNullOrEmpty(script)) throw new ArgumentNullException(nameof(script));

        try
        {
            var command = $"Import-Module WebAdministration; {script}";

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

    private async Task<string?> ExecuteScriptWithOutputAsync(string script)
    {
        if (string.IsNullOrEmpty(script)) throw new ArgumentNullException(nameof(script));

        try
        {
            var command = $"Import-Module WebAdministration; {script}";

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

            return outputText?.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerShell process failed");
            return null;
        }
    }

    private async Task<string?> GetSiteStateAsync(string siteName)
    {
        var script = $"(Get-Website -Name '{siteName}').State";
        var result = await ExecuteScriptWithOutputAsync(script);
        return result?.Split('\n', '\r').LastOrDefault()?.Trim();
    }

    private async Task<string?> GetAppPoolStateAsync(string poolName)
    {
        var script = $"(Get-WebAppPoolState -Name '{poolName}').Value";
        var result = await ExecuteScriptWithOutputAsync(script);
        return result?.Split('\n', '\r').LastOrDefault()?.Trim();
    }

    private async Task<bool> WaitForSiteStateAsync(string siteName, string desiredState)
    {
        var attempts = 0;
        const int maxAttempts = 12; // 1 минута ожидания
        
        while (attempts < maxAttempts)
        {
            var currentState = await GetSiteStateAsync(siteName);
            if (string.Equals(currentState, desiredState, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            _logger.LogInformation("Waiting for site {SiteName} to reach state {DesiredState}, current: {CurrentState}", 
                siteName, desiredState, currentState ?? "unknown");
            
            await Task.Delay(5000);
            attempts++;
        }

        return false;
    }

    private async Task<bool> WaitForAppPoolStateAsync(string poolName, string desiredState)
    {
        var attempts = 0;
        const int maxAttempts = 12; // 1 минута ожидания
        
        while (attempts < maxAttempts)
        {
            var currentState = await GetAppPoolStateAsync(poolName);
            if (string.Equals(currentState, desiredState, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            _logger.LogInformation("Waiting for app pool {PoolName} to reach state {DesiredState}, current: {CurrentState}", 
                poolName, desiredState, currentState ?? "unknown");
            
            await Task.Delay(5000);
            attempts++;
        }

        return false;
    }

    #endregion
}
