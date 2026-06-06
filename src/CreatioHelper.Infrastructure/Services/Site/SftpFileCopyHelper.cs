using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Shared.Interfaces;
using Renci.SshNet;

namespace CreatioHelper.Infrastructure.Services.Site;

public class SftpFileCopyHelper : IFileCopyHelper
{
    private readonly IOutputWriter _output;

    public SftpFileCopyHelper(IOutputWriter output)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
    }

    public async Task<int> CopyAsync(
        ServerInfo server,
        string sourceDir,
        string destDir,
        CancellationToken cancellationToken = default)
    {
        var connectionInfo = BuildConnectionInfo(server);
        const int maxAttempts = 10;
        int totalCopied = 0;
        Exception? lastException = null;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await Task.Run(() =>
                {
                    using var sftp = new SftpClient(connectionInfo);
                    sftp.Connect();
                    try
                    {
                        EnsureRemoteDirectory(sftp, destDir);
                        totalCopied += SyncDirectory(sftp, sourceDir, destDir, cancellationToken);
                    }
                    finally
                    {
                        sftp.Disconnect();
                    }
                }, cancellationToken).ConfigureAwait(false);

                return totalCopied;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (attempt < maxAttempts)
                {
                    _output.WriteLine($"[WARN] SFTP connection lost (attempt {attempt}/{maxAttempts}): {ex.Message}");
                    var delaySec = Math.Min(attempt * 3, 30);
                    await Task.Delay(TimeSpan.FromSeconds(delaySec), cancellationToken).ConfigureAwait(false);
                }
            }
        }

        throw new InvalidOperationException($"SFTP sync failed after {maxAttempts} attempts", lastException);
    }

    private static ConnectionInfo BuildConnectionInfo(ServerInfo server)
    {
        var host = server.SshHost ?? server.Name
            ?? throw new InvalidOperationException($"SSH host is not configured for server '{server.Name}'");
        var port = server.SshPort > 0 ? server.SshPort : 22;
        var user = server.SshUser
            ?? throw new InvalidOperationException($"SSH user is not configured for server '{server.Name}'");

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
            throw new InvalidOperationException(
                $"No SSH credentials configured for server '{server.Name}' — set SshPassword or SshKeyPath");
        }

        return new ConnectionInfo(host, port, user, methods.ToArray());
    }

    private static void EnsureRemoteDirectory(SftpClient sftp, string remotePath)
    {
        var normalized = remotePath.Replace('\\', '/').TrimEnd('/');
        var parts = normalized.TrimStart('/').Split('/');
        var current = "";
        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
            {
                continue;
            }

            current += "/" + part;
            if (!sftp.Exists(current))
            {
                sftp.CreateDirectory(current);
            }
        }
    }

    private int SyncDirectory(SftpClient sftp, string localDir, string remoteDir, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedRemote = remoteDir.Replace('\\', '/').TrimEnd('/');
        EnsureRemoteDirectory(sftp, normalizedRemote);

        var remoteFiles = sftp.ListDirectory(normalizedRemote)
            .Where(f => f.Name != "." && f.Name != "..")
            .ToDictionary(f => f.Name, StringComparer.Ordinal);

        int count = 0;

        foreach (var localFilePath in Directory.EnumerateFiles(localDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(localFilePath);
            var remotePath = normalizedRemote + "/" + name;
            var localInfo = new FileInfo(localFilePath);

            bool needsCopy = true;
            if (remoteFiles.TryGetValue(name, out var remoteFile) && remoteFile.IsRegularFile)
            {
                var mtimeDiff = Math.Abs((remoteFile.LastWriteTime.ToUniversalTime() - localInfo.LastWriteTimeUtc).TotalSeconds);
                needsCopy = remoteFile.Length != localInfo.Length || mtimeDiff > 2.0;
            }

            if (!needsCopy)
            {
                continue;
            }

            UploadFileAtomic(sftp, localFilePath, remotePath, localInfo.LastWriteTimeUtc);
            count++;
            _output.WriteLine($"[SFTP] {name}");
        }

        foreach (var localSubDir in Directory.EnumerateDirectories(localDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(localSubDir);
            var remoteSubDir = normalizedRemote + "/" + name;
            count += SyncDirectory(sftp, localSubDir, remoteSubDir, cancellationToken);
        }

        return count;
    }

    private static void UploadFileAtomic(SftpClient sftp, string localPath, string remotePath, DateTime mtimeUtc)
    {
        var tempPath = remotePath + ".tmp~";
        var localLength = new FileInfo(localPath).Length;
        long resumeOffset = 0;

        if (sftp.Exists(tempPath))
        {
            var remoteSize = sftp.GetAttributes(tempPath).Size;
            if (remoteSize == localLength)
            {
                FinalizeUpload(sftp, tempPath, remotePath, mtimeUtc);
                return;
            }
            if (remoteSize < localLength)
            {
                resumeOffset = remoteSize;
            }
            else
            {
                sftp.DeleteFile(tempPath);
            }
        }

        using var fs = File.OpenRead(localPath);
        if (resumeOffset > 0)
        {
            fs.Seek(resumeOffset, SeekOrigin.Begin);
            using var remote = sftp.Open(tempPath, FileMode.Append);
            fs.CopyTo(remote);
        }
        else
        {
            sftp.UploadFile(fs, tempPath, true);
        }

        FinalizeUpload(sftp, tempPath, remotePath, mtimeUtc);
    }

    private static void FinalizeUpload(SftpClient sftp, string tempPath, string remotePath, DateTime mtimeUtc)
    {
        var attrs = sftp.GetAttributes(tempPath);
        attrs.LastWriteTime = mtimeUtc.ToLocalTime();
        sftp.SetAttributes(tempPath, attrs);

        if (sftp.Exists(remotePath))
        {
            sftp.DeleteFile(remotePath);
        }

        sftp.RenameFile(tempPath, remotePath);
    }
}
