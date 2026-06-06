using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Shared.Interfaces;
using Renci.SshNet;

namespace CreatioHelper.Infrastructure.Services.MacOS;

public class MacOsSiteSynchronizer : ISiteSynchronizer
{
    private readonly IOutputWriter _output;
    private readonly IFileCopyHelper _fileCopyHelper;
    private const int MaxConcurrentCopies = 4;
    private static readonly SemaphoreSlim CopySemaphore = new(MaxConcurrentCopies);

    public MacOsSiteSynchronizer(IOutputWriter output, IFileCopyHelper fileCopyHelper)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _fileCopyHelper = fileCopyHelper ?? throw new ArgumentNullException(nameof(fileCopyHelper));
    }

    public async Task<bool> SynchronizeAsync(
        string sitePath,
        List<ServerInfo> targetServers,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sitePath)) throw new ArgumentNullException(nameof(sitePath));
        if (targetServers == null) throw new ArgumentNullException(nameof(targetServers));

        sitePath = sitePath.TrimEnd('/');

        var sourceConfigPath = ResolveSourceConfigPath(sitePath);
        if (sourceConfigPath == null)
        {
            _output.WriteLine($"[ERROR] Terrasoft.Configuration not found under '{sitePath}'");
            return false;
        }

        _output.WriteLine("[INFO] Stopping services on target servers...");
        foreach (var server in targetServers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            await StopServiceAsync(server, cancellationToken).ConfigureAwait(false);
        }

        _output.WriteLine($"[INFO] Syncing '{sourceConfigPath}' to {targetServers.Count} server(s)...");
        var copyTasks = targetServers
            .Select(server => CopyToServerAsync(server, sourceConfigPath, cancellationToken))
            .ToList();

        bool allOk;
        try
        {
            await Task.WhenAll(copyTasks).ConfigureAwait(false);
            allOk = true;
        }
        catch (OperationCanceledException)
        {
            _output.WriteLine("[INFO] Synchronization was cancelled.");
            allOk = false;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] One or more copy operations failed: {ex.Message}");
            allOk = false;
        }

        _output.WriteLine("[INFO] Starting services on target servers...");
        foreach (var server in targetServers)
        {
            await StartServiceAsync(server, cancellationToken).ConfigureAwait(false);
        }

        if (allOk)
        {
            _output.WriteLine("[OK] Synchronization complete.");
        }

        return allOk;
    }

    private static string? ResolveSourceConfigPath(string sitePath)
    {
        var netCorePath = Path.Combine(sitePath, "Terrasoft.Configuration");
        if (Directory.Exists(netCorePath))
        {
            return netCorePath;
        }

        var netFxPath = Path.Combine(sitePath, "Terrasoft.WebApp", "Terrasoft.Configuration");
        if (Directory.Exists(netFxPath))
        {
            return netFxPath;
        }

        return null;
    }

    private async Task CopyToServerAsync(ServerInfo server, string sourceConfigPath, CancellationToken cancellationToken)
    {
        var remoteBase = server.NetworkPath;
        if (string.IsNullOrEmpty(remoteBase))
        {
            _output.WriteLine($"[WARN] NetworkPath is not set for '{server.Name}', skipping.");
            return;
        }

        remoteBase = remoteBase.Replace('\\', '/').TrimEnd('/');

        var parentName = Path.GetFileName(Path.GetDirectoryName(sourceConfigPath) ?? "");
        string remoteConfigPath;
        if (string.Equals(parentName, "Terrasoft.WebApp", StringComparison.OrdinalIgnoreCase))
        {
            remoteConfigPath = remoteBase + "/Terrasoft.WebApp/Terrasoft.Configuration";
        }
        else
        {
            remoteConfigPath = remoteBase + "/Terrasoft.Configuration";
        }

        await CopySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _output.WriteLine($"[INFO] Copying to {server.Name} → {remoteConfigPath}");
            int count = await _fileCopyHelper.CopyAsync(server, sourceConfigPath, remoteConfigPath, cancellationToken)
                .ConfigureAwait(false);
            _output.WriteLine($"[OK] {server.Name}: {count} file(s) updated.");
        }
        catch (OperationCanceledException)
        {
            _output.WriteLine($"[INFO] Copy to {server.Name} was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Copy to {server.Name} failed: {ex.Message}");
            throw;
        }
        finally
        {
            CopySemaphore.Release();
        }
    }

    private async Task StopServiceAsync(ServerInfo server, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(server.ServiceName))
        {
            _output.WriteLine($"[WARN] ServiceName not set for '{server.Name}', skipping stop.");
            return;
        }

        try
        {
            // macOS uses launchctl; fall back to systemctl for Linux-style services
            var command = $"sudo launchctl stop {server.ServiceName} 2>/dev/null || sudo systemctl stop {server.ServiceName}";
            await RunSshCommandAsync(server, command, cancellationToken).ConfigureAwait(false);
            _output.WriteLine($"[OK] Stopped '{server.ServiceName}' on {server.Name}.");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[WARN] Failed to stop service on {server.Name}: {ex.Message}");
        }
    }

    private async Task StartServiceAsync(ServerInfo server, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(server.ServiceName))
        {
            return;
        }

        try
        {
            var command = $"sudo launchctl start {server.ServiceName} 2>/dev/null || sudo systemctl start {server.ServiceName}";
            await RunSshCommandAsync(server, command, cancellationToken).ConfigureAwait(false);
            _output.WriteLine($"[OK] Started '{server.ServiceName}' on {server.Name}.");
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[WARN] Failed to start service on {server.Name}: {ex.Message}");
        }
    }

    private static async Task RunSshCommandAsync(ServerInfo server, string command, CancellationToken cancellationToken)
    {
        var connectionInfo = BuildSshConnectionInfo(server);

        await Task.Run(() =>
        {
            using var ssh = new SshClient(connectionInfo);
            ssh.Connect();
            try
            {
                var cmd = ssh.RunCommand(command);
                if (cmd.ExitStatus != 0)
                {
                    throw new InvalidOperationException(
                        $"Command '{command}' exited with code {cmd.ExitStatus}: {cmd.Error}");
                }
            }
            finally
            {
                ssh.Disconnect();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static ConnectionInfo BuildSshConnectionInfo(ServerInfo server)
    {
        var host = server.SshHost ?? server.Name
            ?? throw new InvalidOperationException($"SSH host is not configured for '{server.Name}'");
        var port = server.SshPort > 0 ? server.SshPort : 22;
        var user = server.SshUser
            ?? throw new InvalidOperationException($"SSH user is not configured for '{server.Name}'");

        var methods = new List<AuthenticationMethod>();

        if (!string.IsNullOrEmpty(server.SshKeyPath))
        {
            methods.Add(new PrivateKeyAuthenticationMethod(user, new PrivateKeyFile(server.SshKeyPath)));
        }

        if (!string.IsNullOrEmpty(server.SshPassword))
        {
            methods.Add(new PasswordAuthenticationMethod(user, server.SshPassword));
        }

        if (methods.Count == 0)
        {
            throw new InvalidOperationException($"No SSH credentials configured for '{server.Name}'");
        }

        return new ConnectionInfo(host, port, user, methods.ToArray());
    }
}
