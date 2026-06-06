using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Logging;
using CreatioHelper.Infrastructure.Services.Site;

namespace CreatioHelper.Tests;

public class SftpFileCopyHelperTests : IDisposable
{
    private readonly string _localSrcDir;
    private readonly string _host;
    private readonly string _user;
    private readonly string _pass;
    private const int Port = 22;

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

    [Fact]
    public async Task CopyAsync_CopiesAllFiles_WhenDestinationIsEmpty()
    {
        if (!SshReachable(_host, Port))
        {
            return;
        }

        File.WriteAllText(Path.Combine(_localSrcDir, "File1.cs"), "content one");
        File.WriteAllText(Path.Combine(_localSrcDir, "File2.cs"), "content two");

        var remoteDir = "/tmp/sftp_test_" + Guid.NewGuid().ToString("N")[..8];
        var server = MakeServer(remoteDir);
        var helper = MakeHelper();

        int count = await helper.CopyAsync(server, _localSrcDir, remoteDir);

        Assert.Equal(2, count);
    }

    [Fact]
    public async Task CopyAsync_SkipsUnchangedFiles_OnSecondRun()
    {
        if (!SshReachable(_host, Port))
        {
            return;
        }

        File.WriteAllText(Path.Combine(_localSrcDir, "Unchanged.cs"), "same content");
        File.WriteAllText(Path.Combine(_localSrcDir, "Changed.cs"), "original");

        var remoteDir = "/tmp/sftp_test_" + Guid.NewGuid().ToString("N")[..8];
        var server = MakeServer(remoteDir);
        var helper = MakeHelper();

        int firstCount = await helper.CopyAsync(server, _localSrcDir, remoteDir);
        Assert.Equal(2, firstCount);

        await Task.Delay(3000);
        File.WriteAllText(Path.Combine(_localSrcDir, "Changed.cs"), "modified content is now longer");

        int secondCount = await helper.CopyAsync(server, _localSrcDir, remoteDir);
        Assert.Equal(1, secondCount);
    }

    [Fact]
    public async Task CopyAsync_HandlesSubdirectories()
    {
        if (!SshReachable(_host, Port))
        {
            return;
        }

        var subDir = Directory.CreateDirectory(Path.Combine(_localSrcDir, "Subdir"));
        File.WriteAllText(Path.Combine(_localSrcDir, "Root.cs"), "root file");
        File.WriteAllText(Path.Combine(subDir.FullName, "Nested.cs"), "nested file");

        var remoteDir = "/tmp/sftp_test_" + Guid.NewGuid().ToString("N")[..8];
        var server = MakeServer(remoteDir);
        var helper = MakeHelper();

        int count = await helper.CopyAsync(server, _localSrcDir, remoteDir);

        Assert.Equal(2, count);
    }
}
