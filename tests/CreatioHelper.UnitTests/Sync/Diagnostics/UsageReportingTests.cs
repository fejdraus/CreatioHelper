using CreatioHelper.Infrastructure.Services.Sync.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using System.Net;

namespace CreatioHelper.UnitTests.Sync.Diagnostics;

public class UsageReportingTests : IDisposable
{
    private readonly Mock<ILogger<UsageReportingService>> _loggerMock;
    private readonly UsageReportingOptions _options;
    private readonly Mock<HttpMessageHandler> _httpHandlerMock;
    private readonly HttpClient _httpClient;

    public UsageReportingTests()
    {
        _loggerMock = new Mock<ILogger<UsageReportingService>>();
        _options = new UsageReportingOptions
        {
            Enabled = true,
            ReportUrl = "https://test.example.com/report"
        };
        _httpHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpHandlerMock.Object);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    [Fact]
    public void GenerateReport_ReturnsValidReport()
    {
        // Arrange
        var service = new UsageReportingService(_loggerMock.Object, _httpClient, _options);

        // Act
        var report = service.GenerateReport();

        // Assert
        Assert.NotNull(report);
        Assert.NotEmpty(report.UniqueId);
        Assert.NotEmpty(report.Version);
        Assert.NotEmpty(report.Platform);
        Assert.True(report.NumCpu > 0);
        Assert.NotEmpty(report.Date);
    }

    [Fact]
    public void GenerateReport_IncludesSha256Performance()
    {
        // Arrange
        var service = new UsageReportingService(_loggerMock.Object, _httpClient, _options);

        // Act
        var report = service.GenerateReport();

        // Assert
        Assert.True(report.Sha256Performance > 0);
    }

    [Fact]
    public void GenerateReport_WithDataProvider_UsesProvidedData()
    {
        // Arrange
        var data = new UsageReportData
        {
            NumFolders = 5,
            NumDevices = 3,
            TotalFiles = 1000,
            TotalBytes = 1024 * 1024 * 100
        };
        var service = new UsageReportingService(_loggerMock.Object, _httpClient, _options, () => data);

        // Act
        var report = service.GenerateReport();

        // Assert
        Assert.Equal(5, report.NumFolders);
        Assert.Equal(3, report.NumDevices);
        Assert.Equal(1000, report.TotalFiles);
        Assert.Equal(100, report.TotalMiB);
    }

    [Fact]
    public void Enabled_CanBeToggled()
    {
        // Arrange
        var service = new UsageReportingService(_loggerMock.Object, _httpClient, _options);

        // Act & Assert
        Assert.True(service.Enabled);
        service.Enabled = false;
        Assert.False(service.Enabled);
    }

    [Fact]
    public async Task SubmitReportAsync_WhenDisabled_ReturnsFalse()
    {
        // Arrange
        _options.Enabled = false;
        var service = new UsageReportingService(_loggerMock.Object, _httpClient, _options);

        // Act
        var result = await service.SubmitReportAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task SubmitReportAsync_WhenEnabled_SendsReport()
    {
        // Arrange
        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

        var service = new UsageReportingService(_loggerMock.Object, _httpClient, _options);

        // Act
        var result = await service.SubmitReportAsync();

        // Assert
        Assert.True(result);
        _httpHandlerMock.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req => req.Method == HttpMethod.Post),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task SubmitReportAsync_OnFailure_ReturnsFalse()
    {
        // Arrange
        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var service = new UsageReportingService(_loggerMock.Object, _httpClient, _options);

        // Act
        var result = await service.SubmitReportAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void UsageReportingOptions_DefaultValues()
    {
        // Arrange
        var options = new UsageReportingOptions();

        // Assert
        Assert.False(options.Enabled);
        Assert.Equal("https://data.syncthing.net/newdata", options.ReportUrl);
        Assert.Equal(TimeSpan.FromHours(24), options.ReportInterval);
        Assert.Empty(options.UniqueId);
    }

    [Fact]
    public void UsageReport_DateFormat()
    {
        // Arrange
        var report = new UsageReport();

        // Assert
        Assert.Matches(@"\d{4}-\d{2}-\d{2}", report.Date);
    }

    [Fact]
    public void FolderUsageStats_DefaultValues()
    {
        // Arrange
        var stats = new FolderUsageStats();

        // Assert
        Assert.Equal(0, stats.SendOnly);
        Assert.Equal(0, stats.SendReceive);
        Assert.Equal(0, stats.ReceiveOnly);
        Assert.Equal(0, stats.ReceiveEncrypted);
    }

    [Fact]
    public void DeviceUsageStats_DefaultValues()
    {
        // Arrange
        var stats = new DeviceUsageStats();

        // Assert
        Assert.Equal(0, stats.CompressAlways);
        Assert.Equal(0, stats.CompressMetadata);
        Assert.Equal(0, stats.CompressNever);
        Assert.Equal(0, stats.Introducer);
        Assert.Equal(0, stats.Untrusted);
    }

    [Fact]
    public void UsageReportData_DefaultValues()
    {
        // Arrange
        var data = new UsageReportData();

        // Assert
        Assert.Equal(0, data.NumFolders);
        Assert.Equal(0, data.NumDevices);
        Assert.Equal(0, data.TotalFiles);
        Assert.NotNull(data.FolderUses);
        Assert.NotNull(data.DeviceUses);
    }
}

public class UpgradeServiceTests : IDisposable
{
    private readonly Mock<ILogger<UpgradeService>> _loggerMock;
    private readonly UpgradeOptions _options;
    private readonly Mock<HttpMessageHandler> _httpHandlerMock;
    private readonly HttpClient _httpClient;
    private readonly string _testDir;

    public UpgradeServiceTests()
    {
        _loggerMock = new Mock<ILogger<UpgradeService>>();
        _testDir = Path.Combine(Path.GetTempPath(), $"upgrade_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _options = new UpgradeOptions
        {
            ReleaseUrl = "https://api.github.com/test/releases",
            DownloadDirectory = _testDir
        };
        _httpHandlerMock = new Mock<HttpMessageHandler>();
        _httpClient = new HttpClient(_httpHandlerMock.Object);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        if (Directory.Exists(_testDir))
        {
            try { Directory.Delete(_testDir, true); } catch { }
        }
    }

    [Fact]
    public void State_InitiallyIdle()
    {
        // Arrange
        var service = new UpgradeService(_loggerMock.Object, _httpClient, _options);

        // Assert
        Assert.Equal(UpgradeState.Idle, service.State);
    }

    [Fact]
    public void GetCurrentVersion_ReturnsValidVersion()
    {
        // Arrange
        var service = new UpgradeService(_loggerMock.Object, _httpClient, _options);

        // Act
        var version = service.GetCurrentVersion();

        // Assert
        Assert.NotNull(version);
    }

    [Fact]
    public void GetProgress_ReturnsValidProgress()
    {
        // Arrange
        var service = new UpgradeService(_loggerMock.Object, _httpClient, _options);

        // Act
        var progress = service.GetProgress();

        // Assert
        Assert.NotNull(progress);
        Assert.Equal(UpgradeState.Idle, progress.State);
    }

    [Fact]
    public async Task CheckForUpdateAsync_NoReleases_ReturnsNoUpdate()
    {
        // Arrange
        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]")
            });

        var service = new UpgradeService(_loggerMock.Object, _httpClient, _options);

        // Act
        var result = await service.CheckForUpdateAsync();

        // Assert
        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task CheckForUpdateAsync_OnError_ReturnsError()
    {
        // Arrange
        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var service = new UpgradeService(_loggerMock.Object, _httpClient, _options);

        // Act
        var result = await service.CheckForUpdateAsync();

        // Assert
        Assert.False(result.UpdateAvailable);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void UpgradeOptions_DefaultValues()
    {
        // Arrange
        var options = new UpgradeOptions();

        // Assert
        Assert.True(options.AutoCheck);
        Assert.False(options.AutoDownload);
        Assert.False(options.AutoUpgrade);
        Assert.False(options.IncludePrereleases);
        Assert.Equal(TimeSpan.FromHours(12), options.CheckInterval);
    }

    [Fact]
    public void ReleaseInfo_ParsedVersion()
    {
        // Arrange
        var release = new ReleaseInfo { Tag = "v1.2.3" };

        // Assert
        Assert.NotNull(release.ParsedVersion);
        Assert.Equal(1, release.ParsedVersion.Major);
        Assert.Equal(2, release.ParsedVersion.Minor);
        Assert.Equal(3, release.ParsedVersion.Build);
    }

    [Fact]
    public void ReleaseInfo_InvalidTag_ParsedVersionNull()
    {
        // Arrange
        var release = new ReleaseInfo { Tag = "invalid" };

        // Assert
        Assert.Null(release.ParsedVersion);
    }

    [Fact]
    public void UpgradeCheckResult_Valid_Constructor()
    {
        // Arrange
        var result = new UpgradeCheckResult
        {
            UpdateAvailable = true,
            CurrentVersion = new Version(1, 0, 0),
            NewVersion = new Version(2, 0, 0)
        };

        // Assert
        Assert.True(result.UpdateAvailable);
        Assert.Equal(new Version(1, 0, 0), result.CurrentVersion);
        Assert.Equal(new Version(2, 0, 0), result.NewVersion);
    }

    [Fact]
    public void UpgradeProgress_Properties()
    {
        // Arrange
        var progress = new UpgradeProgress
        {
            State = UpgradeState.DownloadingUpdate,
            ProgressPercent = 50,
            Message = "Downloading...",
            BytesDownloaded = 512,
            TotalBytes = 1024
        };

        // Assert
        Assert.Equal(UpgradeState.DownloadingUpdate, progress.State);
        Assert.Equal(50, progress.ProgressPercent);
        Assert.Equal("Downloading...", progress.Message);
        Assert.Equal(512, progress.BytesDownloaded);
        Assert.Equal(1024, progress.TotalBytes);
    }

    [Theory]
    [InlineData(UpgradeState.Idle)]
    [InlineData(UpgradeState.CheckingForUpdate)]
    [InlineData(UpgradeState.DownloadingUpdate)]
    [InlineData(UpgradeState.VerifyingUpdate)]
    [InlineData(UpgradeState.ExtractingUpdate)]
    [InlineData(UpgradeState.InstallingUpdate)]
    [InlineData(UpgradeState.RestartRequired)]
    [InlineData(UpgradeState.Failed)]
    [InlineData(UpgradeState.UpToDate)]
    public void UpgradeState_AllValuesValid(UpgradeState state)
    {
        // Assert
        Assert.True(Enum.IsDefined(typeof(UpgradeState), state));
    }

    [Fact]
    public void ReleaseAsset_Properties()
    {
        // Arrange
        var asset = new ReleaseAsset
        {
            Name = "app-windows-amd64.zip",
            Url = "https://example.com/download",
            Size = 1024 * 1024,
            Sha256 = "abc123"
        };

        // Assert
        Assert.Equal("app-windows-amd64.zip", asset.Name);
        Assert.Equal("https://example.com/download", asset.Url);
        Assert.Equal(1024 * 1024, asset.Size);
        Assert.Equal("abc123", asset.Sha256);
    }
}
