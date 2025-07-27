using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Shared.Interfaces;
using System.Diagnostics;

namespace CreatioHelper.Infrastructure.Services.Linux;

public class LinuxRemoteIisManager : IRemoteIisManager
{
    private readonly IOutputWriter _output;

    public LinuxRemoteIisManager(IOutputWriter output)
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
            return await ExecuteSystemdCommand("start", serviceName, server);
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
            return await ExecuteSystemdCommand("stop", serviceName, server);
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
            await ExecuteSystemdCommand("status", serviceName, server);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to get status for service {serviceName} on {server.Name}: {ex.Message}");
        }
    }

    private async Task<bool> ExecuteSystemdCommand(string action, string serviceName, ServerInfo server)
    {
        var command = $"systemctl {action} {serviceName}";
        
        if (!string.IsNullOrEmpty(server.NetworkPath) && server.NetworkPath != "localhost" && server.NetworkPath != "127.0.0.1")
        {
            command = $"ssh {server.NetworkPath} 'sudo {command}'";
        }
        else
        {
            var result = await ExecuteCommand(command, server.Name);
            if (!result)
            {
                _output.WriteLine($"[INFO] {server.Name}: Command failed without sudo, trying with sudo...");
                _output.WriteLine($"[INFO] {server.Name}: Note: For GUI application to work with sudo, consider configuring passwordless sudo for systemctl commands");
                _output.WriteLine($"[INFO] {server.Name}: Add to /etc/sudoers: {Environment.UserName} ALL=(ALL) NOPASSWD: /bin/systemctl");
                
                command = $"sudo {command}";
                return await ExecuteCommand(command, server.Name);
            }
            return result;
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
                FileName = "/bin/bash",
                Arguments = $"-c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process();
            process.StartInfo = startInfo;
            process.Start();

            string output = "";
            string error = "";

            if (startInfo.RedirectStandardOutput)
            {
                output = await process.StandardOutput.ReadToEndAsync();
            }
            if (startInfo.RedirectStandardError)
            {
                error = await process.StandardError.ReadToEndAsync();
            }

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
        catch (System.Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to execute command on {serverName}: {ex.Message}");
            return false;
        }
    }

    private string GetServiceName(ServerInfo server)
    {
        if (!string.IsNullOrEmpty(server.ServiceName))
            return server.ServiceName;

        return string.Empty;
    }
}
