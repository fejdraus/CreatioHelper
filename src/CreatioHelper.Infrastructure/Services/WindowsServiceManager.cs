using System.Threading.Tasks;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Shared.Interfaces;
using System.Diagnostics;
using System.ServiceProcess;

namespace CreatioHelper.Infrastructure.Services;

public class WindowsServiceManager : IRemoteIisManager
{
    private readonly IOutputWriter _output;

    public WindowsServiceManager(IOutputWriter output)
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
            if (IsLocalServer(server))
            {
                return await StartLocalServiceAsync(serviceName);
            }
            else
            {
                return await ExecuteRemoteServiceCommand("start", serviceName, server);
            }
        }
        catch (System.Exception ex)
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
            if (IsLocalServer(server))
            {
                return await StopLocalServiceAsync(serviceName);
            }
            else
            {
                return await ExecuteRemoteServiceCommand("stop", serviceName, server);
            }
        }
        catch (System.Exception ex)
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
            if (IsLocalServer(server))
            {
                await GetLocalServiceStatusAsync(serviceName);
            }
            else
            {
                await ExecuteRemoteServiceCommand("query", serviceName, server);
            }
        }
        catch (System.Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to get status for service {serviceName} on {server.Name}: {ex.Message}");
        }
    }

    private async Task<bool> StartLocalServiceAsync(string serviceName)
    {
        try
        {
            _output.WriteLine($"[INFO] Starting local Windows service: {serviceName}");
            
            using var service = new ServiceController(serviceName);
            
            if (service.Status == ServiceControllerStatus.Running)
            {
                _output.WriteLine($"[INFO] Service {serviceName} is already running");
                return true;
            }

            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            
            _output.WriteLine($"[SUCCESS] Service {serviceName} started successfully");
            return true;
        }
        catch (System.Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to start local service {serviceName}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> StopLocalServiceAsync(string serviceName)
    {
        try
        {
            _output.WriteLine($"[INFO] Stopping local Windows service: {serviceName}");
            
            using var service = new ServiceController(serviceName);
            
            if (service.Status == ServiceControllerStatus.Stopped)
            {
                _output.WriteLine($"[INFO] Service {serviceName} is already stopped");
                return true;
            }

            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            
            _output.WriteLine($"[SUCCESS] Service {serviceName} stopped successfully");
            return true;
        }
        catch (System.Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to stop local service {serviceName}: {ex.Message}");
            return false;
        }
    }

    private async Task GetLocalServiceStatusAsync(string serviceName)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            _output.WriteLine($"[INFO] Service {serviceName} status: {service.Status}");
        }
        catch (System.Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to get status for local service {serviceName}: {ex.Message}");
        }
    }

    private async Task<bool> ExecuteRemoteServiceCommand(string action, string serviceName, ServerInfo server)
    {
        string command;
        
        switch (action.ToLower())
        {
            case "start":
                command = $"sc \\\\{server.NetworkPath} start {serviceName}";
                break;
            case "stop":
                command = $"sc \\\\{server.NetworkPath} stop {serviceName}";
                break;
            case "query":
                command = $"sc \\\\{server.NetworkPath} query {serviceName}";
                break;
            default:
                command = $"sc \\\\{server.NetworkPath} {action} {serviceName}";
                break;
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
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
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
        catch (System.Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to execute command on {serverName}: {ex.Message}");
            return false;
        }
    }

    private bool IsLocalServer(ServerInfo server)
    {
        return string.IsNullOrEmpty(server.NetworkPath) || 
               server.NetworkPath == "localhost" || 
               server.NetworkPath == "127.0.0.1" ||
               server.NetworkPath == Environment.MachineName;
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
