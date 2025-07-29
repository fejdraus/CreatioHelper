using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Common;
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
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return Result.Failure("Service name is required");
        }

        try
        {
            _output.WriteLine($"[INFO] Starting service '{serviceName}' on macOS using launchctl...");
            
            var startInfo = new ProcessStartInfo
            {
                FileName = "launchctl",
                Arguments = $"start {serviceName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return Result.Failure("Failed to start launchctl process");
            }

            await process.WaitForExitAsync(cancellationToken);
            
            if (process.ExitCode == 0)
            {
                _output.WriteLine($"[INFO] Service '{serviceName}' started successfully");
                return Result.Success();
            }
            else
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken);
                var message = $"Failed to start service '{serviceName}': {error}";
                _output.WriteLine($"[ERROR] {message}");
                return Result.Failure(message);
            }
        }
        catch (Exception ex)
        {
            var message = $"Failed to start service '{serviceName}': {ex.Message}";
            _output.WriteLine($"[ERROR] {message}");
            return Result.Failure(message);
        }
    }

    public async Task<Result> StopServiceAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return Result.Failure("Service name is required");
        }

        try
        {
            _output.WriteLine($"[INFO] Stopping service '{serviceName}' on macOS using launchctl...");
            
            var startInfo = new ProcessStartInfo
            {
                FileName = "launchctl",
                Arguments = $"stop {serviceName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return Result.Failure("Failed to start launchctl process");
            }

            await process.WaitForExitAsync(cancellationToken);
            
            if (process.ExitCode == 0)
            {
                _output.WriteLine($"[INFO] Service '{serviceName}' stopped successfully");
                return Result.Success();
            }
            else
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken);
                var message = $"Failed to stop service '{serviceName}': {error}";
                _output.WriteLine($"[ERROR] {message}");
                return Result.Failure(message);
            }
        }
        catch (Exception ex)
        {
            var message = $"Failed to stop service '{serviceName}': {ex.Message}";
            _output.WriteLine($"[ERROR] {message}");
            return Result.Failure(message);
        }
    }

    public Task<Result<string>> GetAppPoolStatusAsync(string poolName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(poolName))
        {
            return Task.FromResult(Result<string>.Failure("Pool name is required"));
        }
        return GetServiceStatusAsync(poolName, cancellationToken);
    }

    public Task<Result<string>> GetWebsiteStatusAsync(string siteName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(siteName))
        {
            return Task.FromResult(Result<string>.Failure("Site name is required"));
        }
        return GetServiceStatusAsync(siteName, cancellationToken);
    }

    private async Task<Result<string>> GetServiceStatusAsync(string serviceName, CancellationToken cancellationToken = default)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "launchctl",
                Arguments = $"list {serviceName}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return Result<string>.Failure("Failed to start launchctl process");
            }

            await process.WaitForExitAsync(cancellationToken);
            
            if (process.ExitCode == 0)
            {
                var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
                // Simple check - if the service is found, consider it running
                var status = string.IsNullOrWhiteSpace(output) ? "Stopped" : "Running";
                return Result<string>.Success(status);
            }
            else
            {
                // Service not found or stopped
                return Result<string>.Success("Stopped");
            }
        }
        catch (Exception ex)
        {
            var message = $"Failed to get status for service '{serviceName}': {ex.Message}";
            _output.WriteLine($"[ERROR] {message}");
            return Result<string>.Failure(message);
        }
    }
}
