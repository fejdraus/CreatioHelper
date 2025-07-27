using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Common;
using CreatioHelper.Domain.ValueObjects;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Shared.Interfaces;
using System.Diagnostics;

namespace CreatioHelper.Infrastructure.Services.Linux;

public class LinuxRemoteIisManager : IRemoteIisManager
{
    private const string SystemctlCommand = "systemctl";
    private const string RunningStatus = "Running";
    private const string StoppedStatus = "Stopped";
    private const string ActiveStatus = "active";
    private const string OperationCancelledMessage = "Operation was cancelled";
    
    private readonly IOutputWriter _output;
    private readonly Func<ServerId, ServerInfo?> _getServerInfo;

    public LinuxRemoteIisManager(IOutputWriter output, Func<ServerId, ServerInfo?> getServerInfo)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _getServerInfo = getServerInfo ?? throw new ArgumentNullException(nameof(getServerInfo));
    }

    public Task<Result> StopAppPoolAsync(ServerId serverId, CancellationToken cancellationToken = default)
        => StopServiceAsync(serverId, cancellationToken);

    public Task<Result> StopWebsiteAsync(ServerId serverId, CancellationToken cancellationToken = default)
        => StopServiceAsync(serverId, cancellationToken);

    public Task<Result> StartAppPoolAsync(ServerId serverId, CancellationToken cancellationToken = default)
        => StartServiceAsync(serverId, cancellationToken);

    public Task<Result> StartWebsiteAsync(ServerId serverId, CancellationToken cancellationToken = default)
        => StartServiceAsync(serverId, cancellationToken);

    public async Task<Result> StartServiceAsync(ServerId serverId, CancellationToken cancellationToken = default)
        => await ExecuteServiceOperationAsync(serverId, "start", "started", cancellationToken);

    public async Task<Result> StopServiceAsync(ServerId serverId, CancellationToken cancellationToken = default)
        => await ExecuteServiceOperationAsync(serverId, "stop", "stopped", cancellationToken);

    public async Task<Result<string>> GetAppPoolStatusAsync(ServerId serverId, CancellationToken cancellationToken = default)
        => await GetServiceStatusAsync(serverId, cancellationToken);

    public async Task<Result<string>> GetWebsiteStatusAsync(ServerId serverId, CancellationToken cancellationToken = default)
        => await GetServiceStatusAsync(serverId, cancellationToken);

    private async Task<Result> ExecuteServiceOperationAsync(
        ServerId serverId, 
        string action, 
        string actionPastTense, 
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var serviceName = GetServiceName(serverId);
            var result = await ExecuteSystemctlCommandAsync(action, serviceName, cancellationToken);
            
            if (result.IsSuccess)
            {
                _output.WriteLine($"[INFO] Service {serviceName} {actionPastTense} successfully");
                return Result.Success();
            }
            
            var errorMsg = $"Failed to {action} service {serviceName}: {result.ErrorMessage}";
            _output.WriteLine($"[ERROR] {errorMsg}");
            return Result.Failure(errorMsg);
        }
        catch (OperationCanceledException)
        {
            return Result.Failure(OperationCancelledMessage);
        }
        catch (Exception ex)
        {
            var errorMsg = $"Failed to {action} service for server {serverId}: {ex.Message}";
            _output.WriteLine($"[ERROR] {errorMsg}");
            return Result.Failure(errorMsg, ex);
        }
    }

    private async Task<Result<string>> GetServiceStatusAsync(ServerId serverId, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var serviceName = GetServiceName(serverId);
            var statusResult = await CheckServiceStatusAsync(serviceName, cancellationToken);
            
            if (!statusResult.IsSuccess)
            {
                return Result<string>.Failure(statusResult.ErrorMessage);
            }
            
            var status = statusResult.Value ? RunningStatus : StoppedStatus;
            return Result<string>.Success(status);
        }
        catch (OperationCanceledException)
        {
            return Result<string>.Failure(OperationCancelledMessage);
        }
        catch (Exception ex)
        {
            var errorMsg = $"Failed to get status for server {serverId}: {ex.Message}";
            _output.WriteLine($"[ERROR] {errorMsg}");
            return Result<string>.Failure(errorMsg, ex);
        }
    }

    private async Task<Result> ExecuteSystemctlCommandAsync(string action, string serviceName, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = SystemctlCommand,
                Arguments = $"{action} {serviceName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process == null)
            {
                return Result.Failure($"Failed to start {SystemctlCommand} process");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cancellationToken);

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode == 0)
            {
                if (!string.IsNullOrWhiteSpace(output))
                    _output.WriteLine($"[INFO] {SystemctlCommand} output: {output.Trim()}");
                return Result.Success();
            }
            
            var errorMessage = !string.IsNullOrWhiteSpace(error) ? error.Trim() : $"Command failed with exit code {process.ExitCode}";
            _output.WriteLine($"[ERROR] {SystemctlCommand} error: {errorMessage}");
            return Result.Failure(errorMessage);
        }
        catch (Exception ex)
        {
            var errorMsg = $"Failed to execute {SystemctlCommand} command: {ex.Message}";
            _output.WriteLine($"[ERROR] {errorMsg}");
            return Result.Failure(errorMsg, ex);
        }
    }

    private async Task<Result<bool>> CheckServiceStatusAsync(string serviceName, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = SystemctlCommand,
                Arguments = $"is-active {serviceName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process == null)
            {
                return Result<bool>.Failure($"Failed to start {SystemctlCommand} process");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(cancellationToken);
            
            var output = await outputTask;
            var isActive = process.ExitCode == 0 && 
                          !string.IsNullOrWhiteSpace(output) && 
                          output.Trim().Equals(ActiveStatus, StringComparison.OrdinalIgnoreCase);
            
            return Result<bool>.Success(isActive);
        }
        catch (Exception ex)
        {
            var errorMsg = $"Failed to check service status: {ex.Message}";
            _output.WriteLine($"[ERROR] {errorMsg}");
            return Result<bool>.Failure(errorMsg, ex);
        }
    }

    /// <summary>
    /// Получает имя сервиса для управления в systemctl.
    /// Использует ServiceName из ServerInfo, если доступно, иначе ServerName.
    /// </summary>
    private string GetServiceName(ServerId serverId)
    {
        var serverInfo = _getServerInfo(serverId);
        if (serverInfo == null)
        {
            _output.WriteLine($"[WARNING] ServerInfo not found for {serverId}, using ID as service name");
            return serverId.ToString();
        }

        // Приоритет: ServiceName -> ServerName -> ServerId
        if (!string.IsNullOrWhiteSpace(serverInfo.ServiceName))
        {
            return serverInfo.ServiceName;
        }
        
        if (!string.IsNullOrWhiteSpace(serverInfo.Name?.Value))
        {
            return serverInfo.Name.Value;
        }

        _output.WriteLine($"[WARNING] No service name found for server {serverId}, using ID");
        return serverId.ToString();
    }
}
