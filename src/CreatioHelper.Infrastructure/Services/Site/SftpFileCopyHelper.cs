using System.IO.Enumeration;
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
                    sftp.KeepAliveInterval = TimeSpan.FromSeconds(15);
                    sftp.Connect();
                    try
                    {
                        EnsureRemoteDirectory(sftp, destDir);
                        var excludePatterns = server.FileCopyExcludePatterns;
                        totalCopied += SyncDirectory(sftp, sourceDir, destDir, excludePatterns, "", cancellationToken);
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

    private int SyncDirectory(SftpClient sftp, string localDir, string remoteDir,
        IReadOnlyList<string> excludePatterns, string relPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedRemote = remoteDir.Replace('\\', '/').TrimEnd('/');
        EnsureRemoteDirectory(sftp, normalizedRemote);

        var remoteEntries = sftp.ListDirectory(normalizedRemote)
            .Where(f => f.Name != "." && f.Name != "..")
            .ToDictionary(f => f.Name, StringComparer.Ordinal);

        int count = 0;
        var localFileNames = new HashSet<string>(StringComparer.Ordinal);
        var localDirNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var localFilePath in Directory.EnumerateFiles(localDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(localFilePath);
            var fileRelPath = relPath.Length == 0 ? name : relPath + "/" + name;

            if (IsExcluded(name, fileRelPath, excludePatterns))
            {
                continue;
            }

            localFileNames.Add(name);
            var remotePath = normalizedRemote + "/" + name;
            var localInfo = new FileInfo(localFilePath);

            bool needsCopy = true;
            if (remoteEntries.TryGetValue(name, out var remoteFile) && remoteFile.IsRegularFile)
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
            var dirRelPath = relPath.Length == 0 ? name : relPath + "/" + name;

            if (IsExcluded(name, dirRelPath, excludePatterns))
            {
                continue;
            }

            localDirNames.Add(name);
            var remoteSubDir = normalizedRemote + "/" + name;
            count += SyncDirectory(sftp, localSubDir, remoteSubDir, excludePatterns, dirRelPath, cancellationToken);
        }

        foreach (var entry in remoteEntries.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entryRelPath = relPath.Length == 0 ? entry.Name : relPath + "/" + entry.Name;
            if (IsExcluded(entry.Name, entryRelPath, excludePatterns))
            {
                continue;
            }

            if (entry.IsRegularFile && !entry.Name.EndsWith(".tmp~") && !localFileNames.Contains(entry.Name))
            {
                sftp.DeleteFile(normalizedRemote + "/" + entry.Name);
                _output.WriteLine($"[SFTP] deleted {entry.Name}");
            }
            else if (entry.IsDirectory && !localDirNames.Contains(entry.Name))
            {
                DeleteRemoteDirectory(sftp, normalizedRemote + "/" + entry.Name, cancellationToken);
            }
        }

        return count;
    }

    private static bool IsExcluded(string name, string relPath, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return false;
        }

        foreach (var pattern in patterns)
        {
            if (pattern.Contains('/') || pattern.Contains('\\'))
            {
                if (FileSystemName.MatchesSimpleExpression(pattern, relPath, ignoreCase: true))
                {
                    return true;
                }
            }
            else
            {
                if (FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void DeleteRemoteDirectory(SftpClient sftp, string remotePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var entry in sftp.ListDirectory(remotePath).Where(f => f.Name != "." && f.Name != ".."))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.IsRegularFile)
            {
                sftp.DeleteFile(entry.FullName);
            }
            else if (entry.IsDirectory)
            {
                DeleteRemoteDirectory(sftp, entry.FullName, cancellationToken);
            }
        }

        sftp.DeleteDirectory(remotePath);
        _output.WriteLine($"[SFTP] deleted dir {remotePath}");
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
            using var remote = sftp.Open(tempPath, FileMode.Append, FileAccess.Write);
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
