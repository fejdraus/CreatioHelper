using System.Diagnostics;
using System.Text.Json;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Common;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services;

/// <summary>
/// Unified Windows IIS Manager supporting both local and remote servers
/// </summary>
public class WindowsIisManager : IIisManager
{
    private readonly ILogger<WindowsIisManager> _logger;
    private readonly IMetricsService _metrics;
    
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public WindowsIisManager(ILogger<WindowsIisManager> logger, IMetricsService metrics)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    public bool IsSupported() => OperatingSystem.IsWindows();
    
    public bool IsLocal(string serverName) => 
        string.Equals(serverName, Environment.MachineName, StringComparison.OrdinalIgnoreCase);

    #region App Pool Management

    public async Task<Result> StartAppPoolAsync(string serverName, string poolName, CancellationToken cancellationToken = default)
    {
        if (!IsSupported())
            return Result.Failure("IIS management is only available on Windows.");

        if (string.IsNullOrWhiteSpace(poolName))
            return Result.Failure("Pool name is required.");

        return await _metrics.MeasureAsync("iis_apppool_start", async () =>
        {
            try
            {
                var currentState = await GetAppPoolStateAsync(serverName, poolName, cancellationToken);
                if (currentState == "Started")
                {
                    return Result.Success();
                }

                if (currentState == "Stopped")
                {
                    if (!await ExecuteScriptAsync(serverName, $"Start-WebAppPool -Name '{poolName}'", cancellationToken))
                    {
                        return Result.Failure($"Failed to start app pool {poolName}.");
                    }
                }

                var success = await WaitForStateAsync(serverName, true, $"Get-WebAppPoolState -Name '{poolName}'", "Started", $"Application pool {poolName}", cancellationToken);
                return success 
                    ? Result.Success()
                    : Result.Failure($"Failed to start app pool {poolName}.");
            }
            catch (OperationCanceledException)
            {
                return Result.Failure("Operation was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting app pool {PoolName} on server {ServerName}", poolName, serverName);
                return Result.Failure($"Failed to start app pool {poolName}: {ex.Message}", ex);
            }
        });
    }

    public async Task<Result> StopAppPoolAsync(string serverName, string poolName, CancellationToken cancellationToken = default)
    {
        if (!IsSupported())
            return Result.Failure("IIS management is only available on Windows.");

        if (string.IsNullOrWhiteSpace(poolName))
            return Result.Failure("Pool name is required.");

        return await _metrics.MeasureAsync("iis_apppool_stop", async () =>
        {
            try
            {
                var currentState = await GetAppPoolStateAsync(serverName, poolName, cancellationToken);
                if (currentState == "Stopped")
                {
                    return Result.Success();
                }

                if (currentState == "Started")
                {
                    if (!await ExecuteScriptAsync(serverName, $"Stop-WebAppPool -Name '{poolName}'", cancellationToken))
                    {
                        return Result.Failure($"Failed to stop app pool {poolName}.");
                    }
                }

                var success = await WaitForStateAsync(serverName, true, $"Get-WebAppPoolState -Name '{poolName}'", "Stopped", $"Application pool {poolName}", cancellationToken);
                return success 
                    ? Result.Success()
                    : Result.Failure($"Failed to stop app pool {poolName}.");
            }
            catch (OperationCanceledException)
            {
                return Result.Failure("Operation was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping app pool {PoolName} on server {ServerName}", poolName, serverName);
                return Result.Failure($"Failed to stop app pool {poolName}: {ex.Message}", ex);
            }
        });
    }

    public async Task<Result<string>> GetAppPoolStatusAsync(string serverName, string poolName, CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await GetAppPoolStateAsync(serverName, poolName, cancellationToken);
            return Result<string>.Success(status ?? "Unknown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting app pool status {PoolName} on server {ServerName}", poolName, serverName);
            return Result<string>.Failure($"Failed to get app pool status: {ex.Message}", ex);
        }
    }

    #endregion

    #region Website Management

    public async Task<Result> StartWebsiteAsync(string serverName, string siteName, CancellationToken cancellationToken = default)
    {
        if (!IsSupported())
            return Result.Failure("IIS management is only available on Windows.");

        if (string.IsNullOrWhiteSpace(siteName))
            return Result.Failure("Site name is required.");

        return await _metrics.MeasureAsync("iis_website_start", async () =>
        {
            try
            {
                var currentState = await GetWebsiteStateAsync(serverName, siteName, cancellationToken);
                if (currentState == "Started")
                {
                    return Result.Success();
                }

                if (currentState == "Stopped")
                {
                    if (!await ExecuteScriptAsync(serverName, $"Start-Website -Name '{siteName}'", cancellationToken))
                    {
                        return Result.Failure($"Failed to start website {siteName}.");
                    }
                }

                var success = await WaitForStateAsync(serverName, false, $"Get-Website -Name '{siteName}'", "Started", $"Website {siteName}", cancellationToken);
                return success 
                    ? Result.Success()
                    : Result.Failure($"Failed to start website {siteName}.");
            }
            catch (OperationCanceledException)
            {
                return Result.Failure("Operation was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting website {SiteName} on server {ServerName}", siteName, serverName);
                return Result.Failure($"Failed to start website {siteName}: {ex.Message}", ex);
            }
        });
    }

    public async Task<Result> StopWebsiteAsync(string serverName, string siteName, CancellationToken cancellationToken = default)
    {
        if (!IsSupported())
            return Result.Failure("IIS management is only available on Windows.");

        if (string.IsNullOrWhiteSpace(siteName))
            return Result.Failure("Site name is required.");

        return await _metrics.MeasureAsync("iis_website_stop", async () =>
        {
            try
            {
                var currentState = await GetWebsiteStateAsync(serverName, siteName, cancellationToken);
                if (currentState == "Stopped")
                {
                    return Result.Success();
                }

                if (currentState == "Started")
                {
                    if (!await ExecuteScriptAsync(serverName, $"Stop-Website -Name '{siteName}'", cancellationToken))
                    {
                        return Result.Failure($"Failed to stop website {siteName}.");
                    }
                }

                var success = await WaitForStateAsync(serverName, false, $"Get-Website -Name '{siteName}'", "Stopped", $"Website {siteName}", cancellationToken);
                return success 
                    ? Result.Success()
                    : Result.Failure($"Failed to stop website {siteName}.");
            }
            catch (OperationCanceledException)
            {
                return Result.Failure("Operation was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping website {SiteName} on server {ServerName}", siteName, serverName);
                return Result.Failure($"Failed to stop website {siteName}: {ex.Message}", ex);
            }
        });
    }

    public async Task<Result<string>> GetWebsiteStatusAsync(string serverName, string siteName, CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await GetWebsiteStateAsync(serverName, siteName, cancellationToken);
            return Result<string>.Success(status ?? "Unknown");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting website status {SiteName} on server {ServerName}", siteName, serverName);
            return Result<string>.Failure($"Failed to get website status: {ex.Message}", ex);
        }
    }

    #endregion

    #region Service Management

    public async Task<Result> StartServiceAsync(string serverName, string serviceName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
            return Result.Failure("Service name is required");

        return await _metrics.MeasureAsync("service_start", async () =>
        {
            try
            {
                var script = IsLocal(serverName) 
                    ? $"Start-Service -Name '{serviceName}'"
                    : $"Invoke-Command -ComputerName '{serverName}' -ScriptBlock {{ Start-Service -Name '{serviceName}' }}";

                if (!await ExecuteScriptAsync(serverName, script, cancellationToken))
                {
                    return Result.Failure($"Failed to start service {serviceName}");
                }

                return Result.Success();
            }
            catch (OperationCanceledException)
            {
                return Result.Failure("Operation was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting service {ServiceName} on server {ServerName}", serviceName, serverName);
                return Result.Failure($"Failed to start service {serviceName}: {ex.Message}", ex);
            }
        });
    }

    public async Task<Result> StopServiceAsync(string serverName, string serviceName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
            return Result.Failure("Service name is required");

        return await _metrics.MeasureAsync("service_stop", async () =>
        {
            try
            {
                var script = IsLocal(serverName) 
                    ? $"Stop-Service -Name '{serviceName}'"
                    : $"Invoke-Command -ComputerName '{serverName}' -ScriptBlock {{ Stop-Service -Name '{serviceName}' }}";

                if (!await ExecuteScriptAsync(serverName, script, cancellationToken))
                {
                    return Result.Failure($"Failed to stop service {serviceName}");
                }

                return Result.Success();
            }
            catch (OperationCanceledException)
            {
                return Result.Failure("Operation was cancelled");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping service {ServiceName} on server {ServerName}", serviceName, serverName);
                return Result.Failure($"Failed to stop service {serviceName}: {ex.Message}", ex);
            }
        });
    }

    #endregion

    #region Bulk Operations (Local Only)

    public async Task<List<WebServerStatus>> GetAllSitesAsync()
    {
        var sites = new List<WebServerStatus>();
        
        try
        {
            var script = "Get-Website | Select-Object Name, State, @{Name='Port';Expression={($_.bindings.Collection.bindingInformation.Split(':')[1])}} | ConvertTo-Json";
            var result = await ExecuteScriptWithOutputAsync(Environment.MachineName, script);
            
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
            var result = await ExecuteScriptWithOutputAsync(Environment.MachineName, script);
            
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
                            Port = "", // App Pools do not have ports
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

    #endregion

    #region Private Helper Methods

    private async Task<bool> ExecuteScriptAsync(string serverName, string script, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(script)) 
            throw new ArgumentNullException(nameof(script));

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
                _logger.LogError("Failed to start PowerShell process");
                return false;
            }

            var errorText = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(errorText))
            {
                _logger.LogError("PowerShell error on server {ServerName}: {Error}", serverName, errorText);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerShell process failed on server {ServerName}", serverName);
            return false;
        }
    }

    private async Task<string?> ExecuteScriptWithOutputAsync(string serverName, string script, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(script)) 
            throw new ArgumentNullException(nameof(script));

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
                _logger.LogError("Failed to start PowerShell process");
                return null;
            }

            var outputText = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorText = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            if (!string.IsNullOrWhiteSpace(errorText))
            {
                _logger.LogError("PowerShell error on server {ServerName}: {Error}", serverName, errorText);
                return null;
            }

            return outputText.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerShell process failed on server {ServerName}", serverName);
            return null;
        }
    }

    private async Task<string?> GetAppPoolStateAsync(string serverName, string poolName, CancellationToken cancellationToken = default)
    {
        var script = $"(Get-WebAppPoolState -Name '{poolName}').Value";
        var result = await ExecuteScriptWithOutputAsync(serverName, script, cancellationToken);
        return result?.Split('\n', '\r').LastOrDefault()?.Trim();
    }

    private async Task<string?> GetWebsiteStateAsync(string serverName, string siteName, CancellationToken cancellationToken = default)
    {
        var script = $"(Get-Website -Name '{siteName}').State";
        var result = await ExecuteScriptWithOutputAsync(serverName, script, cancellationToken);
        return result?.Split('\n', '\r').LastOrDefault()?.Trim();
    }

    private async Task<bool> WaitForStateAsync(string serverName, bool isPool, string expression, string desiredState, string displayName, CancellationToken cancellationToken = default)
    {
        var attempts = 0;
        const int maxAttempts = 12; // wait up to 1 minute
        
        while (attempts < maxAttempts)
        {
            if (cancellationToken.IsCancellationRequested)
                return false;

            var currentState = isPool 
                ? await GetAppPoolStateAsync(serverName, expression.Split('\'')[1], cancellationToken)
                : await GetWebsiteStateAsync(serverName, expression.Split('\'')[1], cancellationToken);

            if (string.Equals(currentState, desiredState, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            _logger.LogInformation("Waiting for {DisplayName} on server {ServerName} to reach state {DesiredState}, current: {CurrentState}", 
                displayName, serverName, desiredState, currentState ?? "unknown");
            
            await Task.Delay(5000, cancellationToken);
            attempts++;
        }

        return false;
    }

    #endregion
}