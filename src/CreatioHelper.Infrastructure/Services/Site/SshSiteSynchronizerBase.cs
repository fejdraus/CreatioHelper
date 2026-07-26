using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Shared.Interfaces;
using Renci.SshNet;

namespace CreatioHelper.Infrastructure.Services.Site;

/// <summary>
/// Copies a site to remote servers over SSH. Everything except the service
/// start/stop command is identical across platforms; subclasses supply those.
/// </summary>
public abstract class SshSiteSynchronizerBase : ISiteSynchronizer
{
    private const int MaxConcurrentCopies = 4;
    private static readonly SemaphoreSlim CopySemaphore = new(MaxConcurrentCopies);

    private readonly IFileCopyHelper _fileCopyHelper;

    protected readonly IOutputWriter Output;

    protected SshSiteSynchronizerBase(IOutputWriter output, IFileCopyHelper fileCopyHelper)
    {
        Output = output ?? throw new ArgumentNullException(nameof(output));
        _fileCopyHelper = fileCopyHelper ?? throw new ArgumentNullException(nameof(fileCopyHelper));
    }

    protected abstract string BuildStopCommand(string serviceName);

    protected abstract string BuildStartCommand(string serviceName);

    public async Task<bool> SynchronizeAsync(
        string sitePath,
        List<ServerInfo> targetServers,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sitePath)) throw new ArgumentNullException(nameof(sitePath));
        if (targetServers == null) throw new ArgumentNullException(nameof(targetServers));

        sitePath = sitePath.TrimEnd('/');

        Output.WriteLine("[INFO] Stopping services on target servers...");
        foreach (var server in targetServers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            await StopServiceAsync(server, cancellationToken).ConfigureAwait(false);
        }

        Output.WriteLine($"[INFO] Syncing to {targetServers.Count} server(s)...");
        var copyTasks = targetServers
            .Select(server => CopyToServerAsync(server, sitePath, cancellationToken))
            .ToList();

        bool allOk;
        try
        {
            await Task.WhenAll(copyTasks).ConfigureAwait(false);
            allOk = true;
        }
        catch (OperationCanceledException)
        {
            Output.WriteLine("[INFO] Synchronization was cancelled.");
            allOk = false;
        }
        catch (Exception ex)
        {
            Output.WriteLine($"[ERROR] One or more copy operations failed: {ex.Message}");
            allOk = false;
        }

        Output.WriteLine("[INFO] Starting services on target servers...");
        foreach (var server in targetServers)
        {
            await StartServiceAsync(server, cancellationToken).ConfigureAwait(false);
        }

        if (allOk)
        {
            Output.WriteLine("[OK] Synchronization complete.");
        }

        return allOk;
    }

    private async Task CopyToServerAsync(ServerInfo server, string sitePath, CancellationToken cancellationToken)
    {
        var remoteBase = server.NetworkPath;
        if (string.IsNullOrEmpty(remoteBase))
        {
            Output.WriteLine($"[WARN] NetworkPath is not set for '{server.Name}', skipping.");
            return;
        }

        remoteBase = remoteBase.Replace('\\', '/').TrimEnd('/');

        var folders = SyncFolderResolver.Resolve(server, sitePath);
        if (folders.Count == 0)
        {
            Output.WriteLine($"[WARN] No sync folders found for '{server.Name}', skipping.");
            return;
        }

        await CopySemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int total = 0;
            foreach (var relPath in folders)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                var normalizedRel = relPath.Replace('\\', '/').Trim('/').Trim('.');
                var srcDir = string.IsNullOrEmpty(normalizedRel)
                    ? sitePath
                    : Path.Combine(sitePath, normalizedRel.Replace('/', Path.DirectorySeparatorChar));
                var dstDir = string.IsNullOrEmpty(normalizedRel)
                    ? remoteBase
                    : remoteBase + "/" + normalizedRel;

                if (!Directory.Exists(srcDir))
                {
                    Output.WriteLine($"[WARN] Source folder not found, skipping: {srcDir}");
                    continue;
                }

                Output.WriteLine($"[INFO] Copying to {server.Name} → {dstDir}");
                int count = await _fileCopyHelper.CopyAsync(server, srcDir, dstDir, cancellationToken)
                    .ConfigureAwait(false);
                total += count;
            }
            Output.WriteLine($"[OK] {server.Name}: {total} file(s) updated.");
        }
        catch (OperationCanceledException)
        {
            Output.WriteLine($"[INFO] Copy to {server.Name} was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            Output.WriteLine($"[ERROR] Copy to {server.Name} failed: {ex.Message}");
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
            Output.WriteLine($"[WARN] ServiceName not set for '{server.Name}', skipping stop.");
            return;
        }

        try
        {
            await RunSshCommandAsync(server, BuildStopCommand(server.ServiceName), cancellationToken)
                .ConfigureAwait(false);
            Output.WriteLine($"[OK] Stopped '{server.ServiceName}' on {server.Name}.");
        }
        catch (Exception ex)
        {
            Output.WriteLine($"[WARN] Failed to stop service on {server.Name}: {ex.Message}");
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
            await RunSshCommandAsync(server, BuildStartCommand(server.ServiceName), cancellationToken)
                .ConfigureAwait(false);
            Output.WriteLine($"[OK] Started '{server.ServiceName}' on {server.Name}.");
        }
        catch (Exception ex)
        {
            Output.WriteLine($"[WARN] Failed to start service on {server.Name}: {ex.Message}");
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
