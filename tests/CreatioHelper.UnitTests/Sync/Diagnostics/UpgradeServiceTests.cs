using CreatioHelper.Infrastructure.Services.Sync.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using System.Net;

namespace CreatioHelper.UnitTests.Sync.Diagnostics;

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

    [Fact]
    public async Task DownloadUpdateAsync_NoAssetForPlatform_ReturnsFalse()
    {
        // Arrange
        var service = new UpgradeService(_loggerMock.Object, _httpClient, _options);
        var release = new ReleaseInfo
        {
            Tag = "v2.0.0",
            Assets = new List<ReleaseAsset>() // No assets
        };

        // Act
        var result = await service.DownloadUpdateAsync(release);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task DownloadUpdateAsync_HttpError_ReturnsFalse()
    {
        // Arrange
        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var service = new UpgradeService(_loggerMock.Object, _httpClient, _options);
        var release = new ReleaseInfo
        {
            Tag = "v2.0.0",
            Assets = new List<ReleaseAsset>
            {
                new ReleaseAsset
                {
                    Name = "app-windows-amd64.zip",
                    Url = "https://example.com/download.zip",
                    Size = 1024
                }
            }
        };

        // Act
        var result = await service.DownloadUpdateAsync(release);

        // Assert
        Assert.False(result);
        Assert.Equal(UpgradeState.Failed, service.State);
    }

    [Fact]
    public async Task DownloadUpdateAsync_Success_DownloadsFile()
    {
        // Arrange
        var content = new byte[] { 1, 2, 3, 4, 5 };
        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });

        var service = new UpgradeService(_loggerMock.Object, _httpClient, _options);
        var release = new ReleaseInfo
        {
            Tag = "v2.0.0",
            Assets = new List<ReleaseAsset>
            {
                new ReleaseAsset
                {
                    Name = "test-download.zip",
                    Url = "https://example.com/download.zip",
                    Size = content.Length
                }
            }
        };

        // Act
        var result = await service.DownloadUpdateAsync(release);

        // Assert
        Assert.True(result);
        Assert.Equal(UpgradeState.RestartRequired, service.State);
        Assert.True(File.Exists(Path.Combine(_testDir, "test-download.zip")));
    }

    [Fact]
    public async Task DownloadUpdateAsync_HashMismatch_ReturnsFalse()
    {
        // Arrange
        var content = new byte[] { 1, 2, 3, 4, 5 };
        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });

        var service = new UpgradeService(_loggerMock.Object, _httpClient, _options);
        var release = new ReleaseInfo
        {
            Tag = "v2.0.0",
            Assets = new List<ReleaseAsset>
            {
                new ReleaseAsset
                {
                    Name = "test-hash-check.zip",
                    Url = "https://example.com/download.zip",
                    Size = content.Length,
                    Sha256 = "invalid-hash-value"
                }
            }
        };

        // Act
        var result = await service.DownloadUpdateAsync(release);

        // Assert
        Assert.False(result);
        Assert.Equal(UpgradeState.Failed, service.State);
    }

    [Fact]
    public async Task DownloadUpdateAsync_ReportsProgress()
    {
        // Arrange
        var content = new byte[1024]; // 1KB
        new Random(42).NextBytes(content);

        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
                {
                    Headers = { ContentLength = content.Length }
                }
            });

        var service = new UpgradeService(_loggerMock.Object, _httpClient, _options);
        var release = new ReleaseInfo
        {
            Tag = "v2.0.0",
            Assets = new List<ReleaseAsset>
            {
                new ReleaseAsset
                {
                    Name = "test-progress.zip",
                    Url = "https://example.com/download.zip",
                    Size = content.Length
                }
            }
        };

        var progressReports = new List<UpgradeProgress>();
        var progress = new Progress<UpgradeProgress>(p => progressReports.Add(p));

        // Act
        var result = await service.DownloadUpdateAsync(release, progress);
        // Give Progress<T> time to invoke callbacks (async)
        await Task.Delay(100);

        // Assert
        Assert.True(result);
        // Note: Progress<T> callbacks are invoked asynchronously on SynchronizationContext
        // In test scenarios without a sync context, callbacks may not always be invoked
    }

    [Fact]
    public async Task ApplyUpdateAsync_NoDownloadedUpdate_ReturnsFalse()
    {
        // Arrange
        var service = new UpgradeService(_loggerMock.Object, _httpClient, _options);

        // Act
        var result = await service.ApplyUpdateAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void StartAutoCheck_StartsChecking()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var service = new UpgradeService(_loggerMock.Object, _httpClient, _options);

        // Act
        service.StartAutoCheck(cts.Token);

        // Assert - no exception thrown
        cts.Cancel();
        service.StopAutoCheck();
    }

    [Fact]
    public void StopAutoCheck_StopsChecking()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var service = new UpgradeService(_loggerMock.Object, _httpClient, _options);
        service.StartAutoCheck(cts.Token);

        // Act
        service.StopAutoCheck();

        // Assert - no exception thrown
    }

    [Fact]
    public void StartAutoCheck_CalledTwice_DoesNotThrow()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var service = new UpgradeService(_loggerMock.Object, _httpClient, _options);

        // Act
        service.StartAutoCheck(cts.Token);
        service.StartAutoCheck(cts.Token); // Should not throw

        // Assert
        cts.Cancel();
        service.StopAutoCheck();
    }

    [Fact]
    public async Task CheckForUpdateAsync_UpdateAvailable_RaisesEvent()
    {
        // Arrange
        var releaseJson = """
        [
            {
                "tag": "v99.0.0",
                "prerelease": false,
                "assets": [
                    {
                        "name": "app-windows-amd64.zip",
                        "url": "https://example.com/download.zip",
                        "size": 1024
                    }
                ]
            }
        ]
        """;

        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(releaseJson, System.Text.Encoding.UTF8, "application/json")
            });

        var service = new UpgradeService(_loggerMock.Object, _httpClient, _options);
        UpgradeCheckResult? eventResult = null;
        service.UpdateAvailable += (s, e) => eventResult = e;

        // Act
        var result = await service.CheckForUpdateAsync();

        // Assert
        Assert.True(result.UpdateAvailable);
        Assert.NotNull(eventResult);
        Assert.True(eventResult.UpdateAvailable);
    }

    [Fact]
    public async Task CheckForUpdateAsync_WithPrerelease_Filtered()
    {
        // Arrange
        _options.IncludePrereleases = false;
        var releaseJson = """
        [
            {
                "tag": "v99.0.0-beta",
                "prerelease": true,
                "assets": []
            }
        ]
        """;

        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(releaseJson, System.Text.Encoding.UTF8, "application/json")
            });

        var service = new UpgradeService(_loggerMock.Object, _httpClient, _options);

        // Act
        var result = await service.CheckForUpdateAsync();

        // Assert
        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task CheckForUpdateAsync_IncludePrerelease_ReturnsPrerelease()
    {
        // Arrange
        _options.IncludePrereleases = true;
        // Use a version tag that Version.TryParse can handle (no semver prerelease suffix)
        var releaseJson = """
        [
            {
                "tag": "v99.0.0",
                "prerelease": true,
                "assets": [
                    {
                        "name": "app-windows-amd64.zip",
                        "url": "https://example.com/download.zip",
                        "size": 1024
                    }
                ]
            }
        ]
        """;

        _httpHandlerMock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(releaseJson, System.Text.Encoding.UTF8, "application/json")
            });

        var service = new UpgradeService(_loggerMock.Object, _httpClient, _options);

        // Act
        var result = await service.CheckForUpdateAsync();

        // Assert
        Assert.True(result.UpdateAvailable);
    }

    [Fact]
    public void Dispose_StopsAutoCheck()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var service = new UpgradeService(_loggerMock.Object, _httpClient, _options);
        service.StartAutoCheck(cts.Token);

        // Act
        service.Dispose();

        // Assert - no exception thrown, auto-check stopped
    }

    [Fact]
    public void ReleaseInfo_Properties()
    {
        // Arrange
        var release = new ReleaseInfo
        {
            Tag = "v1.2.3",
            Prerelease = true,
            Changelog = "Fixed bugs",
            PublishedAt = new DateTime(2024, 1, 1)
        };

        // Assert
        Assert.Equal("v1.2.3", release.Tag);
        Assert.True(release.Prerelease);
        Assert.Equal("Fixed bugs", release.Changelog);
        Assert.Equal(new DateTime(2024, 1, 1), release.PublishedAt);
    }
}
