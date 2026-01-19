using CreatioHelper.Infrastructure.Services.Sync.Network;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.Network;

public class ConnectionLimitsServiceTests
{
    private readonly Mock<ILogger<ConnectionLimitsService>> _loggerMock;
    private readonly ConnectionLimitsConfiguration _config;
    private readonly ConnectionLimitsService _service;

    public ConnectionLimitsServiceTests()
    {
        _loggerMock = new Mock<ILogger<ConnectionLimitsService>>();
        _config = new ConnectionLimitsConfiguration();
        _service = new ConnectionLimitsService(_loggerMock.Object, _config);
    }

    #region CanConnect Tests

    [Fact]
    public void CanConnect_NoLimits_ReturnsTrue()
    {
        _service.SetMaxConnections("device1", 0); // Unlimited

        Assert.True(_service.CanConnect("device1"));
    }

    [Fact]
    public void CanConnect_BelowLimit_ReturnsTrue()
    {
        _service.SetMaxConnections("device1", 2);

        Assert.True(_service.CanConnect("device1"));
    }

    [Fact]
    public void CanConnect_AtLimit_ReturnsFalse()
    {
        _service.SetMaxConnections("device1", 1);
        _service.TryAcquireConnection("device1", out var handle);

        Assert.False(_service.CanConnect("device1"));
        handle?.Dispose();
    }

    [Fact]
    public void CanConnect_GlobalLimit_Respected()
    {
        _service.SetGlobalMaxConnections(1);
        _service.TryAcquireConnection("device1", out var handle);

        Assert.False(_service.CanConnect("device2"));
        handle?.Dispose();
    }

    [Fact]
    public void CanConnect_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.CanConnect(null!));
    }

    #endregion

    #region TryAcquireConnection Tests

    [Fact]
    public void TryAcquireConnection_Available_ReturnsTrue()
    {
        var result = _service.TryAcquireConnection("device1", out var handle);

        Assert.True(result);
        Assert.NotNull(handle);
        handle!.Dispose();
    }

    [Fact]
    public void TryAcquireConnection_AtLimit_ReturnsFalse()
    {
        _service.SetMaxConnections("device1", 1);
        _service.TryAcquireConnection("device1", out var handle1);

        var result = _service.TryAcquireConnection("device1", out var handle2);

        Assert.False(result);
        Assert.Null(handle2);
        handle1?.Dispose();
    }

    [Fact]
    public void TryAcquireConnection_AfterRelease_Succeeds()
    {
        _service.SetMaxConnections("device1", 1);
        _service.TryAcquireConnection("device1", out var handle1);
        handle1?.Dispose();

        var result = _service.TryAcquireConnection("device1", out var handle2);

        Assert.True(result);
        handle2?.Dispose();
    }

    [Fact]
    public void TryAcquireConnection_Unlimited_AlwaysSucceeds()
    {
        _service.SetMaxConnections("device1", 0); // Unlimited

        var handles = new List<IDisposable>();
        for (int i = 0; i < 100; i++)
        {
            var result = _service.TryAcquireConnection("device1", out var handle);
            Assert.True(result);
            handles.Add(handle!);
        }

        Assert.Equal(100, _service.GetActiveConnections("device1"));

        foreach (var h in handles) h.Dispose();
    }

    [Fact]
    public void TryAcquireConnection_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.TryAcquireConnection(null!, out _));
    }

    #endregion

    #region AcquireConnectionAsync Tests

    [Fact]
    public async Task AcquireConnectionAsync_Available_ReturnsImmediately()
    {
        using var handle = await _service.AcquireConnectionAsync("device1");

        Assert.NotNull(handle);
    }

    [Fact]
    public async Task AcquireConnectionAsync_WaitsForRelease()
    {
        _service.SetMaxConnections("device1", 1);
        var handle1 = await _service.AcquireConnectionAsync("device1");

        var acquireTask = _service.AcquireConnectionAsync("device1");

        await Task.Delay(50);
        Assert.False(acquireTask.IsCompleted);

        handle1.Dispose();

        using var handle2 = await acquireTask;
        Assert.NotNull(handle2);
    }

    [Fact]
    public async Task AcquireConnectionAsync_Cancellation_ThrowsOperationCanceled()
    {
        _service.SetMaxConnections("device1", 1);
        using var handle = await _service.AcquireConnectionAsync("device1");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _service.AcquireConnectionAsync("device1", cts.Token));
    }

    [Fact]
    public async Task AcquireConnectionAsync_Timeout_ThrowsException()
    {
        var config = new ConnectionLimitsConfiguration
        {
            DefaultMaxConnectionsPerDevice = 1,
            AcquireTimeout = TimeSpan.FromMilliseconds(100)
        };
        var service = new ConnectionLimitsService(_loggerMock.Object, config);

        using var handle = await service.AcquireConnectionAsync("device1");

        // May throw TimeoutException or OperationCanceledException depending on timing
        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            service.AcquireConnectionAsync("device1"));

        Assert.True(ex is TimeoutException or OperationCanceledException);
    }

    #endregion

    #region GetActiveConnections Tests

    [Fact]
    public void GetActiveConnections_NoConnections_ReturnsZero()
    {
        Assert.Equal(0, _service.GetActiveConnections("device1"));
    }

    [Fact]
    public void GetActiveConnections_WithConnections_ReturnsCount()
    {
        _service.SetMaxConnections("device1", 5); // Allow multiple connections
        _service.TryAcquireConnection("device1", out var handle1);
        _service.TryAcquireConnection("device1", out var handle2);

        Assert.Equal(2, _service.GetActiveConnections("device1"));

        handle1?.Dispose();
        handle2?.Dispose();
    }

    [Fact]
    public void GetActiveConnections_AfterRelease_Decrements()
    {
        _service.TryAcquireConnection("device1", out var handle);
        Assert.Equal(1, _service.GetActiveConnections("device1"));

        handle?.Dispose();
        Assert.Equal(0, _service.GetActiveConnections("device1"));
    }

    [Fact]
    public void GetActiveConnections_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetActiveConnections(null!));
    }

    #endregion

    #region MaxConnections Configuration Tests

    [Fact]
    public void GetMaxConnections_Default_ReturnsConfigDefault()
    {
        Assert.Equal(_config.DefaultMaxConnectionsPerDevice, _service.GetMaxConnections("device1"));
    }

    [Fact]
    public void SetMaxConnections_ValidValue_SetsCorrectly()
    {
        _service.SetMaxConnections("device1", 5);

        Assert.Equal(5, _service.GetMaxConnections("device1"));
    }

    [Fact]
    public void SetMaxConnections_Zero_AllowsUnlimited()
    {
        _service.SetMaxConnections("device1", 0);

        Assert.Equal(0, _service.GetMaxConnections("device1"));
    }

    [Fact]
    public void SetMaxConnections_Negative_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _service.SetMaxConnections("device1", -1));
    }

    [Fact]
    public void SetMaxConnections_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.SetMaxConnections(null!, 5));
    }

    #endregion

    #region Global Limits Tests

    [Fact]
    public void GetGlobalMaxConnections_Default_ReturnsConfigured()
    {
        Assert.Equal(_config.GlobalMaxConnections, _service.GetGlobalMaxConnections());
    }

    [Fact]
    public void SetGlobalMaxConnections_ValidValue_SetsCorrectly()
    {
        _service.SetGlobalMaxConnections(10);

        Assert.Equal(10, _service.GetGlobalMaxConnections());
    }

    [Fact]
    public void SetGlobalMaxConnections_Negative_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _service.SetGlobalMaxConnections(-1));
    }

    [Fact]
    public void GetTotalActiveConnections_MultipleDevices_SumsAll()
    {
        _service.SetMaxConnections("device1", 5);
        _service.SetMaxConnections("device2", 5);
        _service.TryAcquireConnection("device1", out var h1);
        _service.TryAcquireConnection("device2", out var h2);
        _service.TryAcquireConnection("device2", out var h3);

        Assert.Equal(3, _service.GetTotalActiveConnections());

        h1?.Dispose();
        h2?.Dispose();
        h3?.Dispose();
    }

    #endregion

    #region GetStats Tests

    [Fact]
    public void GetStats_Initial_ReturnsZeros()
    {
        var stats = _service.GetStats();

        Assert.Equal(0, stats.TotalActiveConnections);
        Assert.Equal(0, stats.TotalConnectionsEstablished);
    }

    [Fact]
    public void GetStats_AfterConnections_TracksCorrectly()
    {
        _service.TryAcquireConnection("device1", out var h1);
        _service.TryAcquireConnection("device2", out var h2);

        var stats = _service.GetStats();

        Assert.Equal(2, stats.TotalActiveConnections);
        Assert.Equal(2, stats.TotalConnectionsEstablished);

        h1?.Dispose();
        h2?.Dispose();
    }

    [Fact]
    public void GetStats_RejectedConnections_TracksCorrectly()
    {
        _service.SetMaxConnections("device1", 1);
        _service.TryAcquireConnection("device1", out var h1);
        _service.TryAcquireConnection("device1", out _); // Rejected

        var stats = _service.GetStats();

        Assert.Equal(1, stats.TotalConnectionsRejected);
        h1?.Dispose();
    }

    #endregion

    #region GetDeviceStats Tests

    [Fact]
    public void GetDeviceStats_NewDevice_ReturnsEmpty()
    {
        var stats = _service.GetDeviceStats("device1");

        Assert.Equal("device1", stats.DeviceId);
        Assert.Equal(0, stats.ActiveConnections);
    }

    [Fact]
    public void GetDeviceStats_AfterConnection_TracksCorrectly()
    {
        _service.TryAcquireConnection("device1", out var handle);

        var stats = _service.GetDeviceStats("device1");

        Assert.Equal(1, stats.ActiveConnections);
        Assert.Equal(1, stats.ConnectionsEstablished);
        Assert.NotNull(stats.LastConnectionTime);

        handle?.Dispose();
    }

    [Fact]
    public void GetDeviceStats_NullDeviceId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetDeviceStats(null!));
    }

    #endregion
}

public class ConnectionLimitsConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new ConnectionLimitsConfiguration();

        Assert.Equal(0, config.GlobalMaxConnections);
        Assert.Equal(1, config.DefaultMaxConnectionsPerDevice);
        Assert.Equal(TimeSpan.FromSeconds(30), config.AcquireTimeout);
    }

    [Fact]
    public void GetEffectiveMaxConnections_NoOverride_ReturnsDefault()
    {
        var config = new ConnectionLimitsConfiguration { DefaultMaxConnectionsPerDevice = 5 };

        Assert.Equal(5, config.GetEffectiveMaxConnections("device1"));
    }

    [Fact]
    public void GetEffectiveMaxConnections_WithOverride_ReturnsOverride()
    {
        var config = new ConnectionLimitsConfiguration { DefaultMaxConnectionsPerDevice = 5 };
        config.DeviceMaxConnections["device1"] = 10;

        Assert.Equal(10, config.GetEffectiveMaxConnections("device1"));
        Assert.Equal(5, config.GetEffectiveMaxConnections("device2"));
    }
}
