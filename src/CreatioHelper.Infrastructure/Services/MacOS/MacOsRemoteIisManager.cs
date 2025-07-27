using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Common;
using CreatioHelper.Domain.ValueObjects;
using CreatioHelper.Shared.Interfaces;
using System.Diagnostics;

namespace CreatioHelper.Infrastructure.Services.MacOs;

public class MacOsRemoteIisManager : IRemoteIisManager
{
    private readonly IOutputWriter _output;

    public MacOsRemoteIisManager(IOutputWriter output)
    {
        _output = output;
    }

    public Task<Result> StopAppPoolAsync(ServerId serverId, CancellationToken cancellationToken = default)
    {
        return StopServiceAsync(serverId, cancellationToken);
    }

    public Task<Result> StopWebsiteAsync(ServerId serverId, CancellationToken cancellationToken = default)
    {
        return StopServiceAsync(serverId, cancellationToken);
    }

    public Task<Result> StartAppPoolAsync(ServerId serverId, CancellationToken cancellationToken = default)
    {
        return StartServiceAsync(serverId, cancellationToken);
    }

    public Task<Result> StartWebsiteAsync(ServerId serverId, CancellationToken cancellationToken = default)
    {
        return StartServiceAsync(serverId, cancellationToken);
    }

    public async Task<Result> StartServiceAsync(ServerId serverId, CancellationToken cancellationToken = default)
    {
        try
        {
            // На macOS сервисы управляются через launchctl
            var serviceName = $"creatio.service.{serverId.Value}";
            
            if (cancellationToken.IsCancellationRequested)
                return Result.Failure("Operation was cancelled");

            var result = await ExecuteLaunchctlCommand("start", serviceName, cancellationToken);
            
            if (result)
            {
                _output.WriteLine($"[INFO] Service {serviceName} started successfully");
                return Result.Success();
            }
            else
            {
                var errorMsg = $"Failed to start service {serviceName}";
                _output.WriteLine($"[ERROR] {errorMsg}");
                return Result.Failure(errorMsg);
            }
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Operation was cancelled");
        }
        catch (Exception ex)
        {
            var errorMsg = $"Failed to start service for server {serverId}: {ex.Message}";
            _output.WriteLine($"[ERROR] {errorMsg}");
            return Result.Failure(errorMsg, ex);
        }
    }

    public async Task<Result> StopServiceAsync(ServerId serverId, CancellationToken cancellationToken = default)
    {
        try
        {
            // На macOS сервисы управляются через launchctl
            var serviceName = $"creatio.service.{serverId.Value}";
            
            if (cancellationToken.IsCancellationRequested)
                return Result.Failure("Operation was cancelled");

            var result = await ExecuteLaunchctlCommand("stop", serviceName, cancellationToken);
            
            if (result)
            {
                _output.WriteLine($"[INFO] Service {serviceName} stopped successfully");
                return Result.Success();
            }
            else
            {
                var errorMsg = $"Failed to stop service {serviceName}";
                _output.WriteLine($"[ERROR] {errorMsg}");
                return Result.Failure(errorMsg);
            }
        }
        catch (OperationCanceledException)
        {
            return Result.Failure("Operation was cancelled");
        }
        catch (Exception ex)
        {
            var errorMsg = $"Failed to stop service for server {serverId}: {ex.Message}";
            _output.WriteLine($"[ERROR] {errorMsg}");
            return Result.Failure(errorMsg, ex);
        }
    }

    public async Task<Result<string>> GetAppPoolStatusAsync(ServerId serverId, CancellationToken cancellationToken = default)
    {
        return await GetServiceStatusAsync(serverId, cancellationToken);
    }

    public async Task<Result<string>> GetWebsiteStatusAsync(ServerId serverId, CancellationToken cancellationToken = default)
    {
        return await GetServiceStatusAsync(serverId, cancellationToken);
    }

    private async Task<Result<string>> GetServiceStatusAsync(ServerId serverId, CancellationToken cancellationToken = default)
    {
        try
        {
            var serviceName = $"creatio.service.{serverId.Value}";
            
            if (cancellationToken.IsCancellationRequested)
                return Result<string>.Failure("Operation was cancelled");

            var isRunning = await CheckServiceStatus(serviceName, cancellationToken);
            var status = isRunning ? "Running" : "Stopped";
            
            return Result<string>.Success(status);
        }
        catch (OperationCanceledException)
        {
            return Result<string>.Failure("Operation was cancelled");
        }
        catch (Exception ex)
        {
            var errorMsg = $"Failed to get status for server {serverId}: {ex.Message}";
            _output.WriteLine($"[ERROR] {errorMsg}");
            return Result<string>.Failure(errorMsg, ex);
        }
    }

    private async Task<bool> ExecuteLaunchctlCommand(string action, string serviceName, CancellationToken cancellationToken)
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "launchctl",
                Arguments = $"{action} {serviceName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processStartInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync(cancellationToken);

            var output = await outputTask;
            var error = await errorTask;

            if (process.ExitCode == 0)
            {
                if (!string.IsNullOrEmpty(output))
                    _output.WriteLine($"[INFO] launchctl output: {output}");
                return true;
            }
            else
            {
                if (!string.IsNullOrEmpty(error))
                    _output.WriteLine($"[ERROR] launchctl error: {error}");
                return false;
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to execute launchctl command: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> CheckServiceStatus(string serviceName, CancellationToken cancellationToken)
    {
        try
        {
            var processStartInfo = new ProcessStartInfo
            {
                FileName = "launchctl",
                Arguments = $"list {serviceName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processStartInfo };
            process.Start();

            await process.WaitForExitAsync(cancellationToken);

            // Если сервис найден и запущен, launchctl list возвращает 0
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to check service status: {ex.Message}");
            return false;
        }
    }
}
