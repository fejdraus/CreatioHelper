using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Common;
using CreatioHelper.Shared.Interfaces;
using System.Diagnostics;

namespace CreatioHelper.Infrastructure.Services.Linux;

public class LinuxRemoteIisManager : IRemoteIisManager
{
    private const string SystemctlCommand = "/usr/bin/systemctl";
    private const string RunningStatus = "Running";
    private const string StoppedStatus = "Stopped";
    private const string ActiveStatus = "active";
    private const string OperationCancelledMessage = "Operation was cancelled";
    private static readonly bool IsFlatpak = File.Exists("/.flatpak-info");

    private readonly IOutputWriter _output;

    public LinuxRemoteIisManager(IOutputWriter output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public Task<Result> StopAppPoolAsync(string poolName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(poolName))
        {
            return Task.FromResult(Result.Failure("Pool name is required"));
        }
        return StopServiceAsync(poolName, cancellationToken);
    }

    public Task<Result> StopWebsiteAsync(string siteName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(siteName))
        {
            return Task.FromResult(Result.Failure("Site name is required"));
        }
        return StopServiceAsync(siteName, cancellationToken);
    }

    public Task<Result> StartAppPoolAsync(string poolName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(poolName))
        {
            return Task.FromResult(Result.Failure("Pool name is required"));
        }
        return StartServiceAsync(poolName, cancellationToken);
    }

    public Task<Result> StartWebsiteAsync(string siteName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(siteName))
        {
            return Task.FromResult(Result.Failure("Site name is required"));
        }
        return StartServiceAsync(siteName, cancellationToken);
    }

    public async Task<Result> StartServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        => await ExecuteServiceOperationAsync(serviceName, "start", "started", cancellationToken);

    public async Task<Result> StopServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        => await ExecuteServiceOperationAsync(serviceName, "stop", "stopped", cancellationToken);

    public async Task<Result<string>> GetAppPoolStatusAsync(string poolName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(poolName))
        {
            return Result<string>.Failure("Pool name is required");
        }
        return await GetServiceStatusAsync(poolName, cancellationToken);
    }

    public async Task<Result<string>> GetWebsiteStatusAsync(string siteName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(siteName))
        {
            return Result<string>.Failure("Site name is required");
        }
        return await GetServiceStatusAsync(siteName, cancellationToken);
    }

    private async Task<Result> ExecuteServiceOperationAsync(
        string serviceName, 
        string action, 
        string actionPastTense, 
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            
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
            var errorMsg = $"Failed to {action} service {serviceName}: {ex.Message}";
            _output.WriteLine($"[ERROR] {errorMsg}");
            return Result.Failure(errorMsg, ex);
        }
    }

    private async Task<Result<string>> GetServiceStatusAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            
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
            var errorMsg = $"Failed to get status for service {serviceName}: {ex.Message}";
            _output.WriteLine($"[ERROR] {errorMsg}");
            return Result<string>.Failure(errorMsg, ex);
        }
    }

    private static ProcessStartInfo CreateHostProcessStartInfo(string fileName, string arguments)
    {
        if (IsFlatpak)
        {
            return new ProcessStartInfo
            {
                FileName = "flatpak-spawn",
                Arguments = $"--host {fileName} {arguments}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }

        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private async Task<Result> ExecuteSystemctlCommandAsync(string action, string serviceName, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(CreateHostProcessStartInfo(SystemctlCommand, $"{action} {serviceName}"));

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
            using var process = Process.Start(CreateHostProcessStartInfo(SystemctlCommand, $"is-active {serviceName}"));

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
}
