using System.Security.Cryptography;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Application.Operations;
using CreatioHelper.Domain.ValueObjects;

using CreatioHelper.Domain.Entities;
using CreatioHelper.Shared.Interfaces;
using CreatioHelper.UnitTests.Operations.Fakes;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests.Operations;

public class DeploymentOrchestratorTests
{
    private readonly Mock<IOutputWriter> _output = new(MockBehavior.Strict);
    private readonly Mock<IIisManager> _iisManager = new(MockBehavior.Strict);
    private readonly Mock<ISiteSynchronizer> _siteSynchronizer = new(MockBehavior.Strict);
    private readonly RecordingWorkspacePreparer _workspacePreparer = new();
    private readonly Mock<ICustomDescriptorUpdater> _customDescriptorUpdater = new(MockBehavior.Strict);
    private readonly Mock<IRedisManagerFactory> _redisManagerFactory = new(MockBehavior.Strict);
    private readonly Mock<IMetricsService> _metricsService = new(MockBehavior.Strict);
    private readonly Mock<IServerStatusService> _statusService = new(MockBehavior.Strict);
    private readonly Mock<IPackageFlagsResetter> _packageFlagsResetter = new(MockBehavior.Strict);
    private readonly Mock<IConfigurationBackupService> _configurationBackupService = new(MockBehavior.Strict);

    private readonly DeploymentOrchestrator _sut;

    public DeploymentOrchestratorTests()
    {
        _sut = new DeploymentOrchestrator(
            _output.Object,
            _iisManager.Object,
            _siteSynchronizer.Object,
            _workspacePreparer,
            _customDescriptorUpdater.Object,
            _redisManagerFactory.Object,
            _metricsService.Object,
            _statusService.Object,
            _packageFlagsResetter.Object,
            _configurationBackupService.Object);
    }

    [Fact]
    public async Task RunAsync_EmptySitePath_ReturnsFailWithoutInvokingServices()
    {
        AllowBaselineOutputWrites();

        var result = await _sut.RunAsync(new DeploymentOptions { SitePath = "" });

        Assert.False(result.Success);
        Assert.Equal("Site path is not specified.", result.ErrorMessage);
        Assert.Equal(0, _workspacePreparer.PrepareCount);
    }

    [Fact]
    public async Task RunAsync_NullSiteVersion_FailsAfterPrepare()
    {
        AllowBaselineOutputWrites();
        AllowBaselineMetricsWrites();

        var result = await _sut.RunAsync(new DeploymentOptions
        {
            SitePath = "C:\\fake",
            SiteVersion = null
        });

        Assert.False(result.Success);
        Assert.Equal(1, _workspacePreparer.PrepareCount);
    }

    [Fact]
    public async Task RunAsync_OutdatedSiteVersion_FailsAfterPrepare()
    {
        AllowBaselineOutputWrites();
        AllowBaselineMetricsWrites();

        var result = await _sut.RunAsync(new DeploymentOptions
        {
            SitePath = "C:\\fake",
            SiteVersion = new Version(7, 11, 0, 0)
        });

        Assert.False(result.Success);
        Assert.Equal(1, _workspacePreparer.PrepareCount);
    }

    [Fact]
    public async Task RunAsync_PrepareThrows_ReturnsFailWithExceptionMessage()
    {
        AllowBaselineOutputWrites();
        _metricsService.Setup(m => m.IncrementCounter(It.IsAny<string>(),
            It.IsAny<Dictionary<string, string>?>()));
        _workspacePreparer.PrepareException = new InvalidOperationException("prepare blew up");

        var result = await _sut.RunAsync(new DeploymentOptions
        {
            SitePath = "C:\\fake",
            SiteVersion = new Version(8, 0, 0, 0)
        });

        Assert.False(result.Success);
        Assert.Equal("prepare blew up", result.ErrorMessage);
    }

    [Fact]
    public async Task RunAsync_CancelledBeforePrepare_DoesNotInvokePrepare()
    {
        AllowBaselineOutputWrites();
        AllowBaselineMetricsWrites();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _sut.RunAsync(
            new DeploymentOptions { SitePath = "C:\\fake" },
            ui: null,
            cancellationToken: cts.Token);

        Assert.Equal(0, _workspacePreparer.PrepareCount);
        Assert.True(result.Cancelled || !result.Success);
    }

    [Fact]
    public async Task RunAsync_QuartzOriginalFalse_RestoresViaUpdateOutConfig()
    {
        AllowBaselineOutputWrites();
        AllowBaselineMetricsWrites();
        _workspacePreparer.QuartzReturn = false;

        try
        {
            await _sut.RunAsync(new DeploymentOptions
            {
                SitePath = "C:\\fake",
                SiteVersion = new Version(8, 0, 0, 0),
                Compile = CompileMode.None,
                SkipRedisClear = true,
                SkipServerRestart = true
            });
        }
        catch (MockException)
        {
        }

        Assert.Equal(1, _workspacePreparer.PrepareCount);
        Assert.NotEmpty(_workspacePreparer.UpdateOutConfigInvocations);
        Assert.False(_workspacePreparer.UpdateOutConfigInvocations[0].Quartz);
    }

    [Fact]
    public async Task RunAsync_QuartzOriginalTrue_DoesNotCallUpdateOutConfig()
    {
        AllowBaselineOutputWrites();
        AllowBaselineMetricsWrites();
        _workspacePreparer.QuartzReturn = true;

        try
        {
            await _sut.RunAsync(new DeploymentOptions
            {
                SitePath = "C:\\fake",
                SiteVersion = new Version(8, 0, 0, 0),
                Compile = CompileMode.None,
                SkipRedisClear = true,
                SkipServerRestart = true
            });
        }
        catch (MockException)
        {
        }

        Assert.Equal(1, _workspacePreparer.PrepareCount);
        Assert.Empty(_workspacePreparer.UpdateOutConfigInvocations);
    }

    [Fact]
    public async Task RestoreConfiguration_EmptySitePath_ReturnsFail()
    {
        AllowBaselineOutputWrites();

        var result = await _sut.RestoreConfigurationAsync(new RestoreConfigurationOptions
        {
            SitePath = ""
        });

        Assert.False(result.Success);
        Assert.Equal("Site path is not specified.", result.ErrorMessage);
    }

    [Fact]
    public async Task RestoreConfiguration_NoBackup_ReturnsFail()
    {
        AllowBaselineOutputWrites();
        var backup = new ConfigurationBackup { Path = "C:\\none", Exists = false };
        _configurationBackupService
            .Setup(s => s.Read(It.IsAny<string>()))
            .Returns(backup);

        var result = await _sut.RestoreConfigurationAsync(new RestoreConfigurationOptions
        {
            SitePath = "C:\\fake"
        });

        Assert.False(result.Success);
        Assert.Equal("Backup directory not found.", result.ErrorMessage);
    }

    [Fact]
    public async Task RestoreConfiguration_EmptyBackup_ReturnsFail()
    {
        AllowBaselineOutputWrites();
        var backup = new ConfigurationBackup { Path = "C:\\empty", Exists = true };
        _configurationBackupService
            .Setup(s => s.Read(It.IsAny<string>()))
            .Returns(backup);
        _configurationBackupService
            .Setup(s => s.IsRestoreSupported(It.IsAny<string>()))
            .Returns(true);

        var result = await _sut.RestoreConfigurationAsync(new RestoreConfigurationOptions
        {
            SitePath = "C:\\fake"
        });

        Assert.False(result.Success);
        Assert.Equal("Backup is empty.", result.ErrorMessage);
    }

    [Fact]
    public async Task RestoreConfiguration_VersionNotSupported_ReturnsFail()
    {
        AllowBaselineOutputWrites();
        var backup = new ConfigurationBackup
        {
            Path = "C:\\ok",
            Exists = true,
            ChangedPackages = new[] { new ConfigurationBackupPackage { Name = "x", SizeBytes = 1 } }
        };
        _configurationBackupService
            .Setup(s => s.Read(It.IsAny<string>()))
            .Returns(backup);
        _configurationBackupService
            .Setup(s => s.IsRestoreSupported(It.IsAny<string>()))
            .Returns(false);

        var result = await _sut.RestoreConfigurationAsync(new RestoreConfigurationOptions
        {
            SitePath = "C:\\fake"
        });

        Assert.False(result.Success);
        Assert.Equal("Creatio version does not support restore.", result.ErrorMessage);
    }

    [Fact]
    public async Task StartAllIisAsync_NotSupportedOnPlatform_ReturnsSilently()
    {
        AllowBaselineOutputWrites();
        _iisManager.Setup(m => m.IsSupported()).Returns(false);

        await _sut.StartAllIisAsync(new[] { NewServer("dev1", "pool1", "site1") });

        _iisManager.Verify(m => m.StartAppPoolAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _iisManager.Verify(m => m.StartWebsiteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task StopAllIisAsync_NotSupportedOnPlatform_ReturnsSilently()
    {
        AllowBaselineOutputWrites();
        _iisManager.Setup(m => m.IsSupported()).Returns(false);

        await _sut.StopAllIisAsync(new[] { NewServer("dev1", "pool1", "site1") });

        _iisManager.Verify(m => m.StopAppPoolAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _iisManager.Verify(m => m.StopWebsiteAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private void AllowBaselineOutputWrites()
    {
        _output.Setup(w => w.Clear());
        _output.Setup(w => w.WriteSeparator(It.IsAny<string>()));
        _output.Setup(w => w.WriteLine(It.IsAny<string>()));
    }

    private void AllowBaselineMetricsWrites()
    {
        _metricsService.Setup(m => m.IncrementCounter(It.IsAny<string>(),
            It.IsAny<Dictionary<string, string>?>()));
        _metricsService.Setup(m => m.Measure(It.IsAny<string>(),
            It.IsAny<Action>(), It.IsAny<Dictionary<string, string>?>()));
        _metricsService.Setup(m => m.Measure(It.IsAny<string>(),
            It.IsAny<Func<int>>(), It.IsAny<Dictionary<string, string>?>()));
    }

    private static ServerInfo NewServer(string name, string pool, string site) => new()
    {
        Name = new ServerName(name),
        PoolName = pool,
        SiteName = site
    };
}
