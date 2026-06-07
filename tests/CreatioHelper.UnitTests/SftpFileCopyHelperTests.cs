using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Logging;
using CreatioHelper.Infrastructure.Services.Site;
using Renci.SshNet;

namespace CreatioHelper.Tests;

public class SftpFileCopyHelperTests : IDisposable
{
    private readonly string _localSrcDir;
    private readonly string _host;
    private readonly string _user;
    private readonly string _pass;
    private const int Port = 22;
    private readonly List<string> _remoteTestDirs = new();

    public SftpFileCopyHelperTests()
    {
        _host = Environment.GetEnvironmentVariable("SSH_HOST") ?? "172.18.65.116";
        _user = Environment.GetEnvironmentVariable("SSH_USER") ?? "root";
        _pass = Environment.GetEnvironmentVariable("SSH_PASS") ?? "test1234";

        _localSrcDir = Path.Combine(Path.GetTempPath(), "sftp_test_src_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_localSrcDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_localSrcDir))
        {
            Directory.Delete(_localSrcDir, true);
        }

        if (_remoteTestDirs.Count > 0 && SshReachable(_host, Port))
        {
            try
            {
                using var sftp = OpenSftp();
                sftp.Connect();
                foreach (var dir in _remoteTestDirs)
                {
                    DeleteRemoteDir(sftp, dir);
                }
                sftp.Disconnect();
            }
            catch { }
        }
    }

    private static void DeleteRemoteDir(SftpClient sftp, string path)
    {
        if (!sftp.Exists(path)) { return; }
        foreach (var entry in sftp.ListDirectory(path))
        {
            if (entry.Name == "." || entry.Name == "..") { continue; }
            if (entry.IsDirectory)
            {
                DeleteRemoteDir(sftp, entry.FullName);
            }
            else
            {
                sftp.DeleteFile(entry.FullName);
            }
        }
        sftp.DeleteDirectory(path);
    }

    private static bool SshReachable(string host, int port)
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            return tcp.ConnectAsync(host, port).Wait(2000);
        }
        catch
        {
            return false;
        }
    }

    private ServerInfo MakeServer(string remoteBase) => new()
    {
        Name = "wsl-test",
        SshHost = _host,
        SshPort = Port,
        SshUser = _user,
        SshPassword = _pass,
        NetworkPath = remoteBase
    };

    private static SftpFileCopyHelper MakeHelper()
    {
        var writer = new BufferingOutputWriter(line => Console.WriteLine(line), () => { });
        return new SftpFileCopyHelper(writer);
    }

    private SftpClient OpenSftp() => new(_host, Port, _user, _pass);

    private string RemoteDir()
    {
        var dir = "/tmp/sftp_test_" + Guid.NewGuid().ToString("N")[..8];
        _remoteTestDirs.Add(dir);
        return dir;
    }

    // ── Copy: basic ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CopyAsync_CopiesAllFiles_WhenDestinationIsEmpty()
    {
        if (!SshReachable(_host, Port)) return;

        File.WriteAllText(Path.Combine(_localSrcDir, "File1.cs"), "content one");
        File.WriteAllText(Path.Combine(_localSrcDir, "File2.cs"), "content two");

        var remoteDir = RemoteDir();
        int count = await MakeHelper().CopyAsync(MakeServer(remoteDir), _localSrcDir, remoteDir);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CopyAsync_ReturnsZero_WhenAllFilesUnchanged()
    {
        if (!SshReachable(_host, Port)) return;

        File.WriteAllText(Path.Combine(_localSrcDir, "A.cs"), "aaa");
        File.WriteAllText(Path.Combine(_localSrcDir, "B.cs"), "bbb");

        var remoteDir = RemoteDir();
        var server = MakeServer(remoteDir);
        var helper = MakeHelper();

        await helper.CopyAsync(server, _localSrcDir, remoteDir);
        int secondCount = await helper.CopyAsync(server, _localSrcDir, remoteDir);

        Assert.Equal(0, secondCount);
    }

    // ── Copy: change detection ────────────────────────────────────────────────

    [Fact]
    public async Task CopyAsync_CopiesFile_WhenSizeChanges()
    {
        if (!SshReachable(_host, Port)) return;

        var path = Path.Combine(_localSrcDir, "Sized.cs");
        File.WriteAllText(path, "short");

        var remoteDir = RemoteDir();
        var server = MakeServer(remoteDir);
        var helper = MakeHelper();

        await helper.CopyAsync(server, _localSrcDir, remoteDir);

        File.WriteAllText(path, "much longer content here");

        int count = await helper.CopyAsync(server, _localSrcDir, remoteDir);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CopyAsync_CopiesFile_WhenMtimeChanges()
    {
        if (!SshReachable(_host, Port)) return;

        var path = Path.Combine(_localSrcDir, "Mtime.cs");
        var content = "same length!!";
        File.WriteAllText(path, content);

        var remoteDir = RemoteDir();
        var server = MakeServer(remoteDir);
        var helper = MakeHelper();

        await helper.CopyAsync(server, _localSrcDir, remoteDir);

        await Task.Delay(3000);
        File.WriteAllText(path, content);

        int count = await helper.CopyAsync(server, _localSrcDir, remoteDir);

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task CopyAsync_SkipsUnchangedFiles_OnSecondRun()
    {
        if (!SshReachable(_host, Port)) return;

        File.WriteAllText(Path.Combine(_localSrcDir, "Unchanged.cs"), "same content");
        File.WriteAllText(Path.Combine(_localSrcDir, "Changed.cs"), "original");

        var remoteDir = RemoteDir();
        var server = MakeServer(remoteDir);
        var helper = MakeHelper();

        int firstCount = await helper.CopyAsync(server, _localSrcDir, remoteDir);
        Assert.Equal(2, firstCount);

        await Task.Delay(3000);
        File.WriteAllText(Path.Combine(_localSrcDir, "Changed.cs"), "modified content is now longer");

        int secondCount = await helper.CopyAsync(server, _localSrcDir, remoteDir);
        Assert.Equal(1, secondCount);
    }

    // ── Copy: directory structure ─────────────────────────────────────────────

    [Fact]
    public async Task CopyAsync_HandlesSubdirectories()
    {
        if (!SshReachable(_host, Port)) return;

        var subDir = Directory.CreateDirectory(Path.Combine(_localSrcDir, "Subdir"));
        File.WriteAllText(Path.Combine(_localSrcDir, "Root.cs"), "root file");
        File.WriteAllText(Path.Combine(subDir.FullName, "Nested.cs"), "nested file");

        var remoteDir = RemoteDir();
        int count = await MakeHelper().CopyAsync(MakeServer(remoteDir), _localSrcDir, remoteDir);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CopyAsync_HandlesDeepNesting()
    {
        if (!SshReachable(_host, Port)) return;

        var lvl1 = Directory.CreateDirectory(Path.Combine(_localSrcDir, "L1"));
        var lvl2 = Directory.CreateDirectory(Path.Combine(lvl1.FullName, "L2"));
        var lvl3 = Directory.CreateDirectory(Path.Combine(lvl2.FullName, "L3"));
        File.WriteAllText(Path.Combine(_localSrcDir, "Root.cs"), "r");
        File.WriteAllText(Path.Combine(lvl1.FullName, "A.cs"), "a");
        File.WriteAllText(Path.Combine(lvl2.FullName, "B.cs"), "b");
        File.WriteAllText(Path.Combine(lvl3.FullName, "C.cs"), "c");

        var remoteDir = RemoteDir();
        int count = await MakeHelper().CopyAsync(MakeServer(remoteDir), _localSrcDir, remoteDir);

        Assert.Equal(4, count);

        using var sftp = OpenSftp();
        sftp.Connect();
        Assert.True(sftp.Exists(remoteDir + "/L1/L2/L3/C.cs"));
        sftp.Disconnect();
    }

    [Fact]
    public async Task CopyAsync_HandlesMultipleSubdirsAtSameLevel()
    {
        if (!SshReachable(_host, Port)) return;

        foreach (var name in new[] { "Alpha", "Beta", "Gamma" })
        {
            var d = Directory.CreateDirectory(Path.Combine(_localSrcDir, name));
            File.WriteAllText(Path.Combine(d.FullName, "file.cs"), name);
        }

        var remoteDir = RemoteDir();
        int count = await MakeHelper().CopyAsync(MakeServer(remoteDir), _localSrcDir, remoteDir);

        Assert.Equal(3, count);

        using var sftp = OpenSftp();
        sftp.Connect();
        Assert.True(sftp.Exists(remoteDir + "/Alpha/file.cs"));
        Assert.True(sftp.Exists(remoteDir + "/Beta/file.cs"));
        Assert.True(sftp.Exists(remoteDir + "/Gamma/file.cs"));
        sftp.Disconnect();
    }

    [Fact]
    public async Task CopyAsync_HandlesEmptySourceDirectory()
    {
        if (!SshReachable(_host, Port)) return;

        var remoteDir = RemoteDir();
        int count = await MakeHelper().CopyAsync(MakeServer(remoteDir), _localSrcDir, remoteDir);

        Assert.Equal(0, count);
    }

    // ── Copy: edge cases ─────────────────────────────────────────────────────

    [Fact]
    public async Task CopyAsync_HandlesEmptyFile()
    {
        if (!SshReachable(_host, Port)) return;

        File.WriteAllBytes(Path.Combine(_localSrcDir, "empty.bin"), Array.Empty<byte>());

        var remoteDir = RemoteDir();
        int count = await MakeHelper().CopyAsync(MakeServer(remoteDir), _localSrcDir, remoteDir);

        Assert.Equal(1, count);

        using var sftp = OpenSftp();
        sftp.Connect();
        Assert.Equal(0L, sftp.GetAttributes(remoteDir + "/empty.bin").Size);
        sftp.Disconnect();
    }

    [Fact]
    public async Task CopyAsync_HandlesBinaryFile()
    {
        if (!SshReachable(_host, Port)) return;

        var bytes = new byte[4096];
        new Random(42).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(_localSrcDir, "data.bin"), bytes);

        var remoteDir = RemoteDir();
        await MakeHelper().CopyAsync(MakeServer(remoteDir), _localSrcDir, remoteDir);

        using var sftp = OpenSftp();
        sftp.Connect();
        using var ms = new MemoryStream();
        sftp.DownloadFile(remoteDir + "/data.bin", ms);
        sftp.Disconnect();

        Assert.Equal(bytes, ms.ToArray());
    }

    [Fact]
    public async Task CopyAsync_MtimeIsPreservedOnRemote()
    {
        if (!SshReachable(_host, Port)) return;

        var path = Path.Combine(_localSrcDir, "ts.cs");
        File.WriteAllText(path, "content");
        var localMtime = File.GetLastWriteTimeUtc(path);

        var remoteDir = RemoteDir();
        await MakeHelper().CopyAsync(MakeServer(remoteDir), _localSrcDir, remoteDir);

        using var sftp = OpenSftp();
        sftp.Connect();
        var attrs = sftp.GetAttributes(remoteDir + "/ts.cs");
        sftp.Disconnect();

        var diff = Math.Abs((attrs.LastWriteTime.ToUniversalTime() - localMtime).TotalSeconds);
        Assert.True(diff <= 2.0, $"mtime diff {diff:F1}s exceeds 2s tolerance");
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CopyAsync_DeletesRemovedFile_OnSecondRun()
    {
        if (!SshReachable(_host, Port)) return;

        File.WriteAllText(Path.Combine(_localSrcDir, "Keep.cs"), "keep");
        File.WriteAllText(Path.Combine(_localSrcDir, "Remove.cs"), "remove");

        var remoteDir = RemoteDir();
        var server = MakeServer(remoteDir);
        var helper = MakeHelper();

        await helper.CopyAsync(server, _localSrcDir, remoteDir);
        File.Delete(Path.Combine(_localSrcDir, "Remove.cs"));
        int secondCount = await helper.CopyAsync(server, _localSrcDir, remoteDir);

        Assert.Equal(0, secondCount);

        using var sftp = OpenSftp();
        sftp.Connect();
        Assert.True(sftp.Exists(remoteDir + "/Keep.cs"));
        Assert.False(sftp.Exists(remoteDir + "/Remove.cs"));
        sftp.Disconnect();
    }

    [Fact]
    public async Task CopyAsync_DeletesRemovedDirectory_OnSecondRun()
    {
        if (!SshReachable(_host, Port)) return;

        var subDir = Directory.CreateDirectory(Path.Combine(_localSrcDir, "OldSubdir"));
        File.WriteAllText(Path.Combine(_localSrcDir, "Root.cs"), "root");
        File.WriteAllText(Path.Combine(subDir.FullName, "Old.cs"), "old");

        var remoteDir = RemoteDir();
        var server = MakeServer(remoteDir);
        var helper = MakeHelper();

        await helper.CopyAsync(server, _localSrcDir, remoteDir);
        Directory.Delete(subDir.FullName, true);
        int secondCount = await helper.CopyAsync(server, _localSrcDir, remoteDir);

        Assert.Equal(0, secondCount);

        using var sftp = OpenSftp();
        sftp.Connect();
        Assert.True(sftp.Exists(remoteDir + "/Root.cs"));
        Assert.False(sftp.Exists(remoteDir + "/OldSubdir"));
        sftp.Disconnect();
    }

    [Fact]
    public async Task CopyAsync_DeletesMultipleRemovedFiles_KeepsRest()
    {
        if (!SshReachable(_host, Port)) return;

        foreach (var f in new[] { "A.cs", "B.cs", "C.cs", "D.cs" })
        {
            File.WriteAllText(Path.Combine(_localSrcDir, f), f);
        }

        var remoteDir = RemoteDir();
        var server = MakeServer(remoteDir);
        var helper = MakeHelper();

        await helper.CopyAsync(server, _localSrcDir, remoteDir);

        File.Delete(Path.Combine(_localSrcDir, "B.cs"));
        File.Delete(Path.Combine(_localSrcDir, "D.cs"));

        await helper.CopyAsync(server, _localSrcDir, remoteDir);

        using var sftp = OpenSftp();
        sftp.Connect();
        Assert.True(sftp.Exists(remoteDir + "/A.cs"));
        Assert.False(sftp.Exists(remoteDir + "/B.cs"));
        Assert.True(sftp.Exists(remoteDir + "/C.cs"));
        Assert.False(sftp.Exists(remoteDir + "/D.cs"));
        sftp.Disconnect();
    }

    // ── Mixed operations ──────────────────────────────────────────────────────

    [Fact]
    public async Task CopyAsync_HandlesMixedAddChangeDelete_InOneRun()
    {
        if (!SshReachable(_host, Port)) return;

        File.WriteAllText(Path.Combine(_localSrcDir, "Unchanged.cs"), "same");
        File.WriteAllText(Path.Combine(_localSrcDir, "ToChange.cs"), "old");
        File.WriteAllText(Path.Combine(_localSrcDir, "ToDelete.cs"), "bye");

        var remoteDir = RemoteDir();
        var server = MakeServer(remoteDir);
        var helper = MakeHelper();

        await helper.CopyAsync(server, _localSrcDir, remoteDir);

        await Task.Delay(3000);
        File.WriteAllText(Path.Combine(_localSrcDir, "ToChange.cs"), "new longer content");
        File.Delete(Path.Combine(_localSrcDir, "ToDelete.cs"));
        File.WriteAllText(Path.Combine(_localSrcDir, "Added.cs"), "new file");

        int count = await helper.CopyAsync(server, _localSrcDir, remoteDir);

        Assert.Equal(2, count);

        using var sftp = OpenSftp();
        sftp.Connect();
        Assert.True(sftp.Exists(remoteDir + "/Unchanged.cs"));
        Assert.True(sftp.Exists(remoteDir + "/ToChange.cs"));
        Assert.True(sftp.Exists(remoteDir + "/Added.cs"));
        Assert.False(sftp.Exists(remoteDir + "/ToDelete.cs"));
        sftp.Disconnect();
    }

    // ── Resume ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CopyAsync_ResumesPartialUpload()
    {
        if (!SshReachable(_host, Port)) return;

        var content = new byte[8192];
        new Random(1).NextBytes(content);
        var localPath = Path.Combine(_localSrcDir, "big.bin");
        File.WriteAllBytes(localPath, content);

        var remoteDir = RemoteDir();
        var remoteFinal = remoteDir + "/big.bin";
        var remoteTmp = remoteFinal + ".tmp~";

        using (var sftp = OpenSftp())
        {
            sftp.Connect();
            sftp.CreateDirectory(remoteDir);
            using var partial = new MemoryStream(content, 0, 4096);
            sftp.UploadFile(partial, remoteTmp);
            sftp.Disconnect();
        }

        await MakeHelper().CopyAsync(MakeServer(remoteDir), _localSrcDir, remoteDir);

        using var sftp2 = OpenSftp();
        sftp2.Connect();
        using var ms = new MemoryStream();
        sftp2.DownloadFile(remoteFinal, ms);
        Assert.False(sftp2.Exists(remoteTmp));
        sftp2.Disconnect();

        Assert.Equal(content, ms.ToArray());
    }

    [Fact]
    public async Task CopyAsync_FinalizesCompleted_TmpFile()
    {
        if (!SshReachable(_host, Port)) return;

        var content = System.Text.Encoding.UTF8.GetBytes("full content already uploaded");
        var localPath = Path.Combine(_localSrcDir, "done.bin");
        File.WriteAllBytes(localPath, content);

        var remoteDir = RemoteDir();
        var remoteFinal = remoteDir + "/done.bin";
        var remoteTmp = remoteFinal + ".tmp~";

        using (var sftp = OpenSftp())
        {
            sftp.Connect();
            sftp.CreateDirectory(remoteDir);
            using var ms = new MemoryStream(content);
            sftp.UploadFile(ms, remoteTmp);
            sftp.Disconnect();
        }

        await MakeHelper().CopyAsync(MakeServer(remoteDir), _localSrcDir, remoteDir);

        using var sftp2 = OpenSftp();
        sftp2.Connect();
        Assert.True(sftp2.Exists(remoteFinal));
        Assert.False(sftp2.Exists(remoteTmp));
        using var result = new MemoryStream();
        sftp2.DownloadFile(remoteFinal, result);
        sftp2.Disconnect();

        Assert.Equal(content, result.ToArray());
    }

    [Fact]
    public async Task CopyAsync_DeletesOversized_TmpFile_AndReUploads()
    {
        if (!SshReachable(_host, Port)) return;

        var content = System.Text.Encoding.UTF8.GetBytes("correct content");
        var localPath = Path.Combine(_localSrcDir, "file.bin");
        File.WriteAllBytes(localPath, content);

        var remoteDir = RemoteDir();
        var remoteFinal = remoteDir + "/file.bin";
        var remoteTmp = remoteFinal + ".tmp~";

        using (var sftp = OpenSftp())
        {
            sftp.Connect();
            sftp.CreateDirectory(remoteDir);
            var oversized = new byte[content.Length + 100];
            using var ms = new MemoryStream(oversized);
            sftp.UploadFile(ms, remoteTmp);
            sftp.Disconnect();
        }

        await MakeHelper().CopyAsync(MakeServer(remoteDir), _localSrcDir, remoteDir);

        using var sftp2 = OpenSftp();
        sftp2.Connect();
        Assert.True(sftp2.Exists(remoteFinal));
        Assert.False(sftp2.Exists(remoteTmp));
        using var result = new MemoryStream();
        sftp2.DownloadFile(remoteFinal, result);
        sftp2.Disconnect();

        Assert.Equal(content, result.ToArray());
    }

    [Fact]
    public async Task CopyAsync_DoesNotDelete_TmpFiles_OnRemote()
    {
        if (!SshReachable(_host, Port)) return;

        File.WriteAllText(Path.Combine(_localSrcDir, "real.cs"), "real");

        var remoteDir = RemoteDir();
        var server = MakeServer(remoteDir);
        var helper = MakeHelper();

        await helper.CopyAsync(server, _localSrcDir, remoteDir);

        using (var sftp = OpenSftp())
        {
            sftp.Connect();
            using var ms = new MemoryStream(new byte[] { 1, 2, 3 });
            sftp.UploadFile(ms, remoteDir + "/orphan.cs.tmp~");
            sftp.Disconnect();
        }

        await helper.CopyAsync(server, _localSrcDir, remoteDir);

        using var sftp2 = OpenSftp();
        sftp2.Connect();
        Assert.True(sftp2.Exists(remoteDir + "/orphan.cs.tmp~"),
            ".tmp~ files must not be deleted by sync (managed by upload logic)");
        sftp2.Disconnect();
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    [Fact]
    public async Task CopyAsync_RespectsCancellation()
    {
        if (!SshReachable(_host, Port)) return;

        for (int i = 0; i < 20; i++)
        {
            File.WriteAllText(Path.Combine(_localSrcDir, $"File{i:D2}.cs"), $"content {i}");
        }

        var remoteDir = RemoteDir();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            MakeHelper().CopyAsync(MakeServer(remoteDir), _localSrcDir, remoteDir, cts.Token));
    }

    // ── Exclude patterns ─────────────────────────────────────────────────────

    [Fact]
    public async Task CopyAsync_ExcludesFileByName()
    {
        if (!SshReachable(_host, Port)) return;

        File.WriteAllText(Path.Combine(_localSrcDir, "Keep.cs"), "keep");
        File.WriteAllText(Path.Combine(_localSrcDir, "Skip.log"), "skip");
        File.WriteAllText(Path.Combine(_localSrcDir, "Also.log"), "also skip");

        var remoteDir = RemoteDir();
        var server = MakeServer(remoteDir);
        server.FileCopyExcludePatterns = ["*.log"];

        int count = await MakeHelper().CopyAsync(server, _localSrcDir, remoteDir);

        Assert.Equal(1, count);

        using var sftp = OpenSftp();
        sftp.Connect();
        Assert.True(sftp.Exists(remoteDir + "/Keep.cs"));
        Assert.False(sftp.Exists(remoteDir + "/Skip.log"));
        Assert.False(sftp.Exists(remoteDir + "/Also.log"));
        sftp.Disconnect();
    }

    [Fact]
    public async Task CopyAsync_ExcludesDirectoryByName()
    {
        if (!SshReachable(_host, Port)) return;

        File.WriteAllText(Path.Combine(_localSrcDir, "Root.cs"), "root");
        var logsDir = Directory.CreateDirectory(Path.Combine(_localSrcDir, "logs"));
        File.WriteAllText(Path.Combine(logsDir.FullName, "app.log"), "log");
        var srcDir = Directory.CreateDirectory(Path.Combine(_localSrcDir, "src"));
        File.WriteAllText(Path.Combine(srcDir.FullName, "Code.cs"), "code");

        var remoteDir = RemoteDir();
        var server = MakeServer(remoteDir);
        server.FileCopyExcludePatterns = ["logs"];

        int count = await MakeHelper().CopyAsync(server, _localSrcDir, remoteDir);

        Assert.Equal(2, count);

        using var sftp = OpenSftp();
        sftp.Connect();
        Assert.True(sftp.Exists(remoteDir + "/Root.cs"));
        Assert.True(sftp.Exists(remoteDir + "/src/Code.cs"));
        Assert.False(sftp.Exists(remoteDir + "/logs"));
        sftp.Disconnect();
    }

    [Fact]
    public async Task CopyAsync_ExcludesByRelativePath()
    {
        if (!SshReachable(_host, Port)) return;

        var sub = Directory.CreateDirectory(Path.Combine(_localSrcDir, "conf"));
        File.WriteAllText(Path.Combine(_localSrcDir, "Root.cs"), "root");
        File.WriteAllText(Path.Combine(sub.FullName, "secret.config"), "secret");
        File.WriteAllText(Path.Combine(sub.FullName, "app.config"), "app");

        var remoteDir = RemoteDir();
        var server = MakeServer(remoteDir);
        server.FileCopyExcludePatterns = ["conf/secret.config"];

        int count = await MakeHelper().CopyAsync(server, _localSrcDir, remoteDir);

        Assert.Equal(2, count);

        using var sftp = OpenSftp();
        sftp.Connect();
        Assert.True(sftp.Exists(remoteDir + "/Root.cs"));
        Assert.True(sftp.Exists(remoteDir + "/conf/app.config"));
        Assert.False(sftp.Exists(remoteDir + "/conf/secret.config"));
        sftp.Disconnect();
    }

    [Fact]
    public async Task CopyAsync_DoesNotDelete_ExcludedFile_ExistingOnRemote()
    {
        if (!SshReachable(_host, Port)) return;

        File.WriteAllText(Path.Combine(_localSrcDir, "Keep.cs"), "keep");
        File.WriteAllText(Path.Combine(_localSrcDir, "Persistent.log"), "log");

        var remoteDir = RemoteDir();
        var server = MakeServer(remoteDir);

        await MakeHelper().CopyAsync(server, _localSrcDir, remoteDir);

        File.Delete(Path.Combine(_localSrcDir, "Persistent.log"));

        server.FileCopyExcludePatterns = ["*.log"];
        await MakeHelper().CopyAsync(server, _localSrcDir, remoteDir);

        using var sftp = OpenSftp();
        sftp.Connect();
        Assert.True(sftp.Exists(remoteDir + "/Keep.cs"));
        Assert.True(sftp.Exists(remoteDir + "/Persistent.log"),
            "excluded file must not be deleted from remote during mirror step");
        sftp.Disconnect();
    }

    [Fact]
    public async Task CopyAsync_DoesNotDelete_ExcludedDirectory_ExistingOnRemote()
    {
        if (!SshReachable(_host, Port)) return;

        File.WriteAllText(Path.Combine(_localSrcDir, "Root.cs"), "root");
        var logsDir = Directory.CreateDirectory(Path.Combine(_localSrcDir, "logs"));
        File.WriteAllText(Path.Combine(logsDir.FullName, "app.log"), "log");

        var remoteDir = RemoteDir();
        var server = MakeServer(remoteDir);

        await MakeHelper().CopyAsync(server, _localSrcDir, remoteDir);

        Directory.Delete(Path.Combine(_localSrcDir, "logs"), true);

        server.FileCopyExcludePatterns = ["logs"];
        await MakeHelper().CopyAsync(server, _localSrcDir, remoteDir);

        using var sftp = OpenSftp();
        sftp.Connect();
        Assert.True(sftp.Exists(remoteDir + "/Root.cs"));
        Assert.True(sftp.Exists(remoteDir + "/logs"),
            "excluded directory must not be deleted from remote during mirror step");
        sftp.Disconnect();
    }
}
