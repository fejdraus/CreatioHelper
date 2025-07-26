using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Shared.Interfaces;
using System.Diagnostics;

namespace CreatioHelper.Infrastructure.Services.Linux;

public class MacOsRemoteIisManager : IRemoteIisManager
{
    private readonly IOutputWriter _output;

    public MacOsRemoteIisManager(IOutputWriter output)
    {
        _output = output;
    }

    public Task<bool> StopAppPoolAsync(ServerInfo server)
    {
        return StopServiceAsync(server);
    }

    public Task<bool> StopWebsiteAsync(ServerInfo server)
    {
        return StopServiceAsync(server);
    }

    public Task<bool> StartAppPoolAsync(ServerInfo server)
    {
        return StartServiceAsync(server);
    }

    public Task<bool> StartWebsiteAsync(ServerInfo server)
    {
        return StartServiceAsync(server);
    }

    public async Task<bool> StartServiceAsync(ServerInfo server)
    {
        var serviceName = GetServiceName(server);
        if (string.IsNullOrEmpty(serviceName))
        {
            _output.WriteLine($"[ERROR] Service name not specified for server {server.Name}");
            return false;
        }

        try
        {
            return await ExecuteLaunchctlCommand("start", serviceName, server);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to start service {serviceName} on {server.Name}: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> StopServiceAsync(ServerInfo server)
    {
        var serviceName = GetServiceName(server);
        if (string.IsNullOrEmpty(serviceName))
        {
            _output.WriteLine($"[ERROR] Service name not specified for server {server.Name}");
            return false;
        }

        try
        {
            return await ExecuteLaunchctlCommand("stop", serviceName, server);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to stop service {serviceName} on {server.Name}: {ex.Message}");
            return false;
        }
    }

    public async Task GetAppPoolStatusAsync(ServerInfo server)
    {
        await GetServiceStatusAsync(server);
    }

    public async Task GetWebsiteStatusAsync(ServerInfo server)
    {
        await GetServiceStatusAsync(server);
    }

    private async Task GetServiceStatusAsync(ServerInfo server)
    {
        var serviceName = GetServiceName(server);
        if (string.IsNullOrEmpty(serviceName))
        {
            return;
        }

        try
        {
            await ExecuteLaunchctlCommand("status", serviceName, server);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to get status for service {serviceName} on {server.Name}: {ex.Message}");
        }
    }

    private async Task<bool> ExecuteLaunchctlCommand(string action, string serviceName, ServerInfo server)
    {
        string command;
        
        switch (action.ToLower())
        {
            case "start":
                command = $"launchctl bootstrap gui/$(id -u) {serviceName}";
                break;
            case "stop":
                command = $"launchctl bootout gui/$(id -u) {serviceName}";
                break;
            case "status":
                command = $"launchctl print gui/$(id -u)/{serviceName}";
                break;
            case "list":
                command = $"launchctl list | grep {serviceName}";
                break;
            case "enable":
                command = $"launchctl enable gui/$(id -u)/{serviceName}";
                break;
            case "disable":
                command = $"launchctl disable gui/$(id -u)/{serviceName}";
                break;
            default:
                command = $"launchctl {action} {serviceName}";
                break;
        }

        if (!string.IsNullOrEmpty(server.NetworkPath) && server.NetworkPath != "localhost" && server.NetworkPath != "127.0.0.1")
        {
            command = $"ssh {server.NetworkPath} '{command}'";
        }

        return await ExecuteCommand(command, server.Name);
    }

    private async Task<bool> ExecuteCommand(string command, string serverName)
    {
        try
        {
            _output.WriteLine($"[INFO] Executing on {serverName}: {command}");

            var startInfo = new ProcessStartInfo
            {
                FileName = "/bin/zsh",
                Arguments = $"-c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process();
            process.StartInfo = startInfo;
            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (!string.IsNullOrEmpty(output))
            {
                _output.WriteLine($"[INFO] {serverName}: {output}");
            }

            if (!string.IsNullOrEmpty(error))
            {
                _output.WriteLine($"[ERROR] {serverName}: {error}");
            }

            var success = process.ExitCode == 0;
            _output.WriteLine($"[{(success ? "SUCCESS" : "ERROR")}] Command on {serverName} completed with exit code {process.ExitCode}");

            return success;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to execute command on {serverName}: {ex.Message}");
            return false;
        }
    }

    private string GetServiceName(ServerInfo server)
    {
        if (!string.IsNullOrEmpty(server.PoolName))
            return server.PoolName;
        
        if (!string.IsNullOrEmpty(server.SiteName))
            return server.SiteName;

        return string.Empty;
    }
}
