using System.Diagnostics;
using System.Text.Json;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Common;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Shared.Utils;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services;

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
    public bool IsLocal(string serverName) => PowerShellRunner.IsLocalServer(serverName);
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
                if (!await AppPoolExistsAsync(serverName, poolName, cancellationToken))
                {
                    return Result.Failure($"Application pool '{poolName}' does not exist on server {serverName}.");
                }
                var currentState = await GetAppPoolStateAsync(serverName, poolName, cancellationToken);
                if (currentState == "Started")
                {
                    return Result.Success();
                }
                if (currentState == "Stopped")
                {
                    if (!await ExecuteScriptAsync(serverName, $"Start-WebAppPool -Name '{PowerShellRunner.EscapeSingleQuoted(poolName)}'", cancellationToken))
                    {
                        return Result.Failure($"Failed to start app pool {poolName}.");
                    }
                }
                var success = await WaitForStateAsync(serverName, true, poolName, "Started", $"Application pool {poolName}", cancellationToken);
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
                if (!await AppPoolExistsAsync(serverName, poolName, cancellationToken))
                {
                    return Result.Failure($"Application pool '{poolName}' does not exist on server {serverName}.");
                }
                var currentState = await GetAppPoolStateAsync(serverName, poolName, cancellationToken);
                if (currentState == "Stopped")
                {
                    return Result.Success();
                }
                if (currentState == "Started")
                {
                    if (!await ExecuteScriptAsync(serverName, $"Stop-WebAppPool -Name '{PowerShellRunner.EscapeSingleQuoted(poolName)}'", cancellationToken))
                    {
                        return Result.Failure($"Failed to stop app pool {poolName}.");
                    }
                }
                var success = await WaitForStateAsync(serverName, true, poolName, "Stopped", $"Application pool {poolName}", cancellationToken);
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
                if (!await WebsiteExistsAsync(serverName, siteName, cancellationToken))
                {
                    return Result.Failure($"Website '{siteName}' does not exist on server {serverName}.");
                }
                var currentState = await GetWebsiteStateAsync(serverName, siteName, cancellationToken);
                if (currentState == "Started")
                {
                    return Result.Success();
                }
                if (currentState == "Stopped")
                {
                    if (!await ExecuteScriptAsync(serverName, $"Start-Website -Name '{PowerShellRunner.EscapeSingleQuoted(siteName)}'", cancellationToken))
                    {
                        return Result.Failure($"Failed to start website {siteName}.");
                    }
                }
                var success = await WaitForStateAsync(serverName, false, siteName, "Started", $"Website {siteName}", cancellationToken);
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
                if (!await WebsiteExistsAsync(serverName, siteName, cancellationToken))
                {
                    return Result.Failure($"Website '{siteName}' does not exist on server {serverName}.");
                }
                var currentState = await GetWebsiteStateAsync(serverName, siteName, cancellationToken);
                if (currentState == "Stopped")
                {
                    return Result.Success();
                }
                if (currentState == "Started")
                {
                    if (!await ExecuteScriptAsync(serverName, $"Stop-Website -Name '{PowerShellRunner.EscapeSingleQuoted(siteName)}'", cancellationToken))
                    {
                        return Result.Failure($"Failed to stop website {siteName}.");
                    }
                }
                var success = await WaitForStateAsync(serverName, false, siteName, "Stopped", $"Website {siteName}", cancellationToken);
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
                    ? $"Start-Service -Name '{PowerShellRunner.EscapeSingleQuoted(serviceName)}'"
                    : $"Invoke-Command -ComputerName '{PowerShellRunner.EscapeSingleQuoted(serverName)}' -ScriptBlock {{ Start-Service -Name '{PowerShellRunner.EscapeSingleQuoted(serviceName)}' }}";
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
                    ? $"Stop-Service -Name '{PowerShellRunner.EscapeSingleQuoted(serviceName)}'"
                    : $"Invoke-Command -ComputerName '{PowerShellRunner.EscapeSingleQuoted(serverName)}' -ScriptBlock {{ Stop-Service -Name '{PowerShellRunner.EscapeSingleQuoted(serviceName)}' }}";
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
                            Port = "",
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
            var result = await RunWebAdministrationAsync(serverName, script, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                _logger.LogError("Failed to start PowerShell process");
                return false;
            }
            if (result.HasError)
            {
                _logger.LogError("PowerShell error on server {ServerName}: {Error}", serverName, result.Error);
                return false;
            }
            return result.ExitCode == 0;
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
            var result = await RunWebAdministrationAsync(serverName, script, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                _logger.LogError("Failed to start PowerShell process");
                return null;
            }
            if (result.HasError)
            {
                _logger.LogError("PowerShell error on server {ServerName}: {Error}", serverName, result.Error);
                return null;
            }
            return result.Output.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerShell process failed on server {ServerName}", serverName);
            return null;
        }
    }
    private Task<ShellResult?> RunWebAdministrationAsync(string serverName, string script, CancellationToken cancellationToken)
    {
        var command = IsLocal(serverName)
            ? $"Import-Module WebAdministration; {script}"
            : $"Invoke-Command -ComputerName '{PowerShellRunner.EscapeSingleQuoted(serverName)}' -ScriptBlock {{\r\n    Import-Module WebAdministration\r\n    {script}\r\n}} -ErrorAction Stop";
        return PowerShellRunner.RunAsync(command, cancellationToken: cancellationToken);
    }
    private async Task<string?> GetAppPoolStateAsync(string serverName, string poolName, CancellationToken cancellationToken = default)
    {
        var script = $"(Get-WebAppPoolState -Name '{PowerShellRunner.EscapeSingleQuoted(poolName)}').Value";
        var result = await ExecuteScriptWithOutputAsync(serverName, script, cancellationToken);
        return ShellResult.LastLineOf(result);
    }
    private async Task<string?> GetWebsiteStateAsync(string serverName, string siteName, CancellationToken cancellationToken = default)
    {
        var script = $"(Get-Website -Name '{PowerShellRunner.EscapeSingleQuoted(siteName)}').State";
        var result = await ExecuteScriptWithOutputAsync(serverName, script, cancellationToken);
        return ShellResult.LastLineOf(result);
    }
    private async Task<bool> AppPoolExistsAsync(string serverName, string poolName, CancellationToken cancellationToken = default)
    {
        var script = $"Test-Path 'IIS:\\AppPools\\{poolName}'";
        var result = await ExecuteScriptWithOutputAsync(serverName, script, cancellationToken);
        return result?.Trim().Equals("True", StringComparison.OrdinalIgnoreCase) == true;
    }
    private async Task<bool> WebsiteExistsAsync(string serverName, string siteName, CancellationToken cancellationToken = default)
    {
        var script = $"Test-Path 'IIS:\\Sites\\{siteName}'";
        var result = await ExecuteScriptWithOutputAsync(serverName, script, cancellationToken);
        return result?.Trim().Equals("True", StringComparison.OrdinalIgnoreCase) == true;
    }
    private Task<bool> WaitForStateAsync(string serverName, bool isPool, string objectName, string desiredState, string displayName, CancellationToken cancellationToken = default)
        => StatePolling.WaitForStateAsync(
            ct => isPool
                ? GetAppPoolStateAsync(serverName, objectName, ct)
                : GetWebsiteStateAsync(serverName, objectName, ct),
            desiredState,
            currentState => _logger.LogInformation(
                "Waiting for {DisplayName} on server {ServerName} to reach state {DesiredState}, current: {CurrentState}",
                displayName, serverName, desiredState, currentState ?? "unknown"),
            cancellationToken: cancellationToken);
    #endregion
}