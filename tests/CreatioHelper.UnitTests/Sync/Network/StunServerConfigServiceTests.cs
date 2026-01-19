using CreatioHelper.Infrastructure.Services.Sync.Network;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.Network;

public class StunServerConfigServiceTests
{
    private readonly Mock<ILogger<StunServerConfigService>> _loggerMock;
    private readonly StunServerConfiguration _config;
    private readonly StunServerConfigService _service;

    public StunServerConfigServiceTests()
    {
        _loggerMock = new Mock<ILogger<StunServerConfigService>>();
        _config = new StunServerConfiguration();
        _service = new StunServerConfigService(_loggerMock.Object, _config);
    }

    #region GetServers Tests

    [Fact]
    public void GetServers_Default_ReturnsDefaultServers()
    {
        var servers = _service.GetServers();

        Assert.NotEmpty(servers);
        Assert.Contains(servers, s => s.Address == "stun.syncthing.net");
    }

    [Fact]
    public void GetServers_ReturnsSortedByPriority()
    {
        var servers = _service.GetServers();

        for (int i = 1; i < servers.Count; i++)
        {
            Assert.True(servers[i - 1].Priority <= servers[i].Priority);
        }
    }

    [Fact]
    public void GetServers_NoDefaultServers_ReturnsEmpty()
    {
        var config = new StunServerConfiguration { UseDefaultServers = false };
        var service = new StunServerConfigService(_loggerMock.Object, config);

        var servers = service.GetServers();

        Assert.Empty(servers);
    }

    #endregion

    #region AddServer Tests

    [Fact]
    public void AddServer_ValidAddress_AddsServer()
    {
        _service.AddServer("custom.stun.server");

        var servers = _service.GetServers();
        Assert.Contains(servers, s => s.Address == "custom.stun.server");
    }

    [Fact]
    public void AddServer_WithPort_ParsesCorrectly()
    {
        _service.AddServer("custom.stun.server:1234");

        var servers = _service.GetServers();
        var server = servers.FirstOrDefault(s => s.Address == "custom.stun.server");
        Assert.NotNull(server);
        Assert.Equal(1234, server.Port);
    }

    [Fact]
    public void AddServer_Duplicate_DoesNotAddTwice()
    {
        var initialCount = _service.GetServers().Count;

        _service.AddServer("custom.stun.server");
        _service.AddServer("custom.stun.server");

        Assert.Equal(initialCount + 1, _service.GetServers().Count);
    }

    [Fact]
    public void AddServer_WithPriority_SetsCorrectPriority()
    {
        _service.AddServer("custom.stun.server", 50);

        var servers = _service.GetServers();
        var server = servers.FirstOrDefault(s => s.Address == "custom.stun.server");
        Assert.NotNull(server);
        Assert.Equal(50, server.Priority);
    }

    [Fact]
    public void AddServer_NullAddress_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.AddServer(null!));
    }

    #endregion

    #region RemoveServer Tests

    [Fact]
    public void RemoveServer_Exists_RemovesAndReturnsTrue()
    {
        _service.AddServer("custom.stun.server");

        var result = _service.RemoveServer("custom.stun.server");

        Assert.True(result);
        Assert.DoesNotContain(_service.GetServers(), s => s.Address == "custom.stun.server");
    }

    [Fact]
    public void RemoveServer_NotExists_ReturnsFalse()
    {
        var result = _service.RemoveServer("nonexistent.server");

        Assert.False(result);
    }

    [Fact]
    public void RemoveServer_NullAddress_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.RemoveServer(null!));
    }

    #endregion

    #region ClearServers Tests

    [Fact]
    public void ClearServers_RemovesAll()
    {
        _service.ClearServers();

        Assert.Empty(_service.GetServers());
    }

    #endregion

    #region ResetToDefaults Tests

    [Fact]
    public void ResetToDefaults_RestoresDefaultServers()
    {
        _service.ClearServers();
        _service.AddServer("custom.server");

        _service.ResetToDefaults();

        var servers = _service.GetServers();
        Assert.Contains(servers, s => s.Address == "stun.syncthing.net");
        Assert.DoesNotContain(servers, s => s.Address == "custom.server");
    }

    #endregion

    #region GetNextServer Tests

    [Fact]
    public void GetNextServer_Enabled_ReturnsServer()
    {
        var server = _service.GetNextServer();

        Assert.NotNull(server);
    }

    [Fact]
    public void GetNextServer_Disabled_ReturnsNull()
    {
        _service.SetEnabled(false);

        var server = _service.GetNextServer();

        Assert.Null(server);
    }

    [Fact]
    public void GetNextServer_NoServers_ReturnsNull()
    {
        _service.ClearServers();

        var server = _service.GetNextServer();

        Assert.Null(server);
    }

    [Fact]
    public void GetNextServer_RoundRobins()
    {
        var servers = new HashSet<string>();

        // Get servers multiple times
        for (int i = 0; i < 20; i++)
        {
            var server = _service.GetNextServer();
            if (server != null)
            {
                servers.Add(server.Address);
            }
        }

        // Should have cycled through multiple servers
        Assert.True(servers.Count > 1);
    }

    #endregion

    #region MarkServerFailed Tests

    [Fact]
    public void MarkServerFailed_UpdatesStats()
    {
        _service.MarkServerFailed("stun.syncthing.net", "Connection timeout");

        var stats = _service.GetStatistics();
        var serverStats = stats.FirstOrDefault(s => s.Address == "stun.syncthing.net");

        Assert.NotNull(serverStats);
        Assert.Equal(1, serverStats.FailureCount);
        Assert.Equal("Connection timeout", serverStats.LastFailureReason);
    }

    [Fact]
    public void MarkServerFailed_NullAddress_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.MarkServerFailed(null!));
    }

    #endregion

    #region MarkServerSuccess Tests

    [Fact]
    public void MarkServerSuccess_UpdatesStats()
    {
        _service.MarkServerSuccess("stun.syncthing.net", TimeSpan.FromMilliseconds(50));

        var stats = _service.GetStatistics();
        var serverStats = stats.FirstOrDefault(s => s.Address == "stun.syncthing.net");

        Assert.NotNull(serverStats);
        Assert.Equal(1, serverStats.SuccessCount);
        Assert.NotNull(serverStats.AverageLatency);
    }

    [Fact]
    public void MarkServerSuccess_TracksLatency()
    {
        _service.MarkServerSuccess("stun.syncthing.net", TimeSpan.FromMilliseconds(50));
        _service.MarkServerSuccess("stun.syncthing.net", TimeSpan.FromMilliseconds(100));

        var stats = _service.GetStatistics();
        var serverStats = stats.FirstOrDefault(s => s.Address == "stun.syncthing.net");

        Assert.NotNull(serverStats);
        Assert.Equal(TimeSpan.FromMilliseconds(50), serverStats.MinLatency);
        Assert.Equal(TimeSpan.FromMilliseconds(100), serverStats.MaxLatency);
    }

    [Fact]
    public void MarkServerSuccess_NullAddress_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.MarkServerSuccess(null!, TimeSpan.Zero));
    }

    #endregion

    #region IsEnabled Tests

    [Fact]
    public void IsEnabled_Default_ReturnsTrue()
    {
        Assert.True(_service.IsEnabled);
    }

    [Fact]
    public void SetEnabled_False_DisablesStun()
    {
        _service.SetEnabled(false);

        Assert.False(_service.IsEnabled);
    }

    [Fact]
    public void SetEnabled_True_EnablesStun()
    {
        _service.SetEnabled(false);
        _service.SetEnabled(true);

        Assert.True(_service.IsEnabled);
    }

    #endregion

    #region GetStatistics Tests

    [Fact]
    public void GetStatistics_Initial_ReturnsEmpty()
    {
        var stats = _service.GetStatistics();

        Assert.Empty(stats);
    }

    [Fact]
    public void GetStatistics_AfterUsage_ReturnsStats()
    {
        _service.MarkServerSuccess("stun.syncthing.net", TimeSpan.FromMilliseconds(50));
        _service.MarkServerFailed("stun.ekiga.net", "Timeout");

        var stats = _service.GetStatistics();

        Assert.Equal(2, stats.Count);
    }

    [Fact]
    public void GetStatistics_SuccessRate_CalculatesCorrectly()
    {
        _service.MarkServerSuccess("stun.syncthing.net", TimeSpan.FromMilliseconds(50));
        _service.MarkServerSuccess("stun.syncthing.net", TimeSpan.FromMilliseconds(50));
        _service.MarkServerFailed("stun.syncthing.net");

        var stats = _service.GetStatistics();
        var serverStats = stats.First(s => s.Address == "stun.syncthing.net");

        Assert.Equal(2.0 / 3.0 * 100, serverStats.SuccessRate, 1);
    }

    #endregion

    #region TestServerAsync Tests

    [Fact]
    public async Task TestServerAsync_ReturnsResult()
    {
        var result = await _service.TestServerAsync("stun.syncthing.net");

        Assert.NotNull(result);
        Assert.Equal("stun.syncthing.net", result.Address);
    }

    [Fact]
    public async Task TestServerAsync_Cancellation_ReturnsFailed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _service.TestServerAsync("stun.syncthing.net", cts.Token);

        Assert.False(result.Success);
    }

    [Fact]
    public async Task TestServerAsync_NullAddress_ThrowsArgumentNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.TestServerAsync(null!));
    }

    #endregion
}

public class StunServerStatsTests
{
    [Fact]
    public void IsAvailable_NoFailures_ReturnsTrue()
    {
        var stats = new StunServerStats
        {
            SuccessCount = 5,
            FailureCount = 0
        };

        Assert.True(stats.IsAvailable);
    }

    [Fact]
    public void IsAvailable_RecentSuccess_ReturnsTrue()
    {
        var stats = new StunServerStats
        {
            SuccessCount = 5,
            FailureCount = 1,
            LastSuccess = DateTime.UtcNow,
            LastFailure = DateTime.UtcNow.AddMinutes(-5)
        };

        Assert.True(stats.IsAvailable);
    }

    [Fact]
    public void IsAvailable_RecentFailure_ReturnsFalse()
    {
        var stats = new StunServerStats
        {
            SuccessCount = 5,
            FailureCount = 1,
            LastSuccess = DateTime.UtcNow.AddMinutes(-5),
            LastFailure = DateTime.UtcNow
        };

        Assert.False(stats.IsAvailable);
    }

    [Fact]
    public void SuccessRate_CalculatesCorrectly()
    {
        var stats = new StunServerStats
        {
            SuccessCount = 80,
            FailureCount = 20
        };

        Assert.Equal(80.0, stats.SuccessRate);
    }
}

public class StunServerConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new StunServerConfiguration();

        Assert.True(config.Enabled);
        Assert.True(config.UseDefaultServers);
        Assert.Equal(3478, config.DefaultPort);
        Assert.Equal(TimeSpan.FromSeconds(5), config.RequestTimeout);
        Assert.Equal(3, config.MaxRetries);
        Assert.Equal(TimeSpan.FromMinutes(5), config.FailedServerCooldown);
        Assert.NotEmpty(config.DefaultServers);
    }
}
