using CreatioHelper.Infrastructure.Services.Sync.Transfer;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.Tests.Sync.Transfer;

public class MaxConcurrentWritesServiceTests
{
    private readonly Mock<ILogger<MaxConcurrentWritesService>> _loggerMock;
    private readonly MaxConcurrentWritesConfiguration _config;
    private readonly MaxConcurrentWritesService _service;

    public MaxConcurrentWritesServiceTests()
    {
        _loggerMock = new Mock<ILogger<MaxConcurrentWritesService>>();
        _config = new MaxConcurrentWritesConfiguration();
        _service = new MaxConcurrentWritesService(_loggerMock.Object, _config);
    }

    #region AcquireWriteSlot Tests

    [Fact]
    public async Task AcquireWriteSlotAsync_FirstSlot_Succeeds()
    {
        using var slot = await _service.AcquireWriteSlotAsync("folder1", "file.txt");

        Assert.NotNull(slot);
        Assert.Equal(1, _service.GetActiveWriters("folder1", "file.txt"));
    }

    [Fact]
    public async Task AcquireWriteSlotAsync_WithinLimit_AllSucceed()
    {
        _service.SetMaxConcurrentWrites("folder1", 3);

        using var slot1 = await _service.AcquireWriteSlotAsync("folder1", "file.txt");
        using var slot2 = await _service.AcquireWriteSlotAsync("folder1", "file.txt");
        using var slot3 = await _service.AcquireWriteSlotAsync("folder1", "file.txt");

        Assert.Equal(3, _service.GetActiveWriters("folder1", "file.txt"));
    }

    [Fact]
    public async Task AcquireWriteSlotAsync_ReleasingSlot_AllowsNew()
    {
        _service.SetMaxConcurrentWrites("folder1", 1);

        var slot1 = await _service.AcquireWriteSlotAsync("folder1", "file.txt");
        Assert.Equal(1, _service.GetActiveWriters("folder1", "file.txt"));

        slot1.Dispose();
        Assert.Equal(0, _service.GetActiveWriters("folder1", "file.txt"));

        using var slot2 = await _service.AcquireWriteSlotAsync("folder1", "file.txt");
        Assert.Equal(1, _service.GetActiveWriters("folder1", "file.txt"));
    }

    [Fact]
    public async Task AcquireWriteSlotAsync_UnlimitedWrites_AllowsMany()
    {
        _service.SetMaxConcurrentWrites("folder1", 0); // 0 = unlimited

        var slots = new List<IDisposable>();
        for (int i = 0; i < 100; i++)
        {
            slots.Add(await _service.AcquireWriteSlotAsync("folder1", "file.txt"));
        }

        Assert.Equal(100, _service.GetActiveWriters("folder1", "file.txt"));

        foreach (var slot in slots)
        {
            slot.Dispose();
        }
    }

    [Fact]
    public async Task AcquireWriteSlotAsync_DifferentFiles_Independent()
    {
        _service.SetMaxConcurrentWrites("folder1", 1);

        using var slot1 = await _service.AcquireWriteSlotAsync("folder1", "file1.txt");
        using var slot2 = await _service.AcquireWriteSlotAsync("folder1", "file2.txt");

        Assert.Equal(1, _service.GetActiveWriters("folder1", "file1.txt"));
        Assert.Equal(1, _service.GetActiveWriters("folder1", "file2.txt"));
    }

    [Fact]
    public async Task AcquireWriteSlotAsync_Cancellation_ThrowsOperationCanceled()
    {
        _service.SetMaxConcurrentWrites("folder1", 1);
        using var slot = await _service.AcquireWriteSlotAsync("folder1", "file.txt");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _service.AcquireWriteSlotAsync("folder1", "file.txt", cts.Token));
    }

    [Fact]
    public async Task AcquireWriteSlotAsync_WaitsForSlot()
    {
        _service.SetMaxConcurrentWrites("folder1", 1);
        var slot1 = await _service.AcquireWriteSlotAsync("folder1", "file.txt");

        var acquireTask = _service.AcquireWriteSlotAsync("folder1", "file.txt");

        // Should not complete immediately
        await Task.Delay(50);
        Assert.False(acquireTask.IsCompleted);

        // Release first slot
        slot1.Dispose();

        // Now should complete
        using var slot2 = await acquireTask;
        Assert.NotNull(slot2);
    }

    #endregion

    #region TryAcquireWriteSlot Tests

    [Fact]
    public void TryAcquireWriteSlot_Available_ReturnsTrue()
    {
        var result = _service.TryAcquireWriteSlot("folder1", "file.txt", out var slot);

        Assert.True(result);
        Assert.NotNull(slot);
        slot!.Dispose();
    }

    [Fact]
    public async Task TryAcquireWriteSlot_AtLimit_ReturnsFalse()
    {
        _service.SetMaxConcurrentWrites("folder1", 1);
        using var existingSlot = await _service.AcquireWriteSlotAsync("folder1", "file.txt");

        var result = _service.TryAcquireWriteSlot("folder1", "file.txt", out var slot);

        Assert.False(result);
        Assert.Null(slot);
    }

    [Fact]
    public void TryAcquireWriteSlot_Unlimited_AlwaysSucceeds()
    {
        _service.SetMaxConcurrentWrites("folder1", 0);

        for (int i = 0; i < 10; i++)
        {
            var result = _service.TryAcquireWriteSlot("folder1", "file.txt", out var slot);
            Assert.True(result);
            // Don't dispose - we're testing accumulation
        }

        Assert.Equal(10, _service.GetActiveWriters("folder1", "file.txt"));
    }

    #endregion

    #region GetActiveWriters Tests

    [Fact]
    public void GetActiveWriters_NoSlots_ReturnsZero()
    {
        var count = _service.GetActiveWriters("folder1", "file.txt");

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task GetActiveWriters_AfterDispose_Decrements()
    {
        var slot = await _service.AcquireWriteSlotAsync("folder1", "file.txt");
        Assert.Equal(1, _service.GetActiveWriters("folder1", "file.txt"));

        slot.Dispose();
        Assert.Equal(0, _service.GetActiveWriters("folder1", "file.txt"));
    }

    [Fact]
    public void GetActiveWriters_UnknownFolder_ReturnsZero()
    {
        Assert.Equal(0, _service.GetActiveWriters("unknown", "file.txt"));
    }

    #endregion

    #region MaxConcurrentWrites Configuration Tests

    [Fact]
    public void GetMaxConcurrentWrites_Default_ReturnsConfigDefault()
    {
        var max = _service.GetMaxConcurrentWrites("folder1");

        Assert.Equal(_config.DefaultMaxConcurrentWrites, max);
    }

    [Fact]
    public void SetMaxConcurrentWrites_ValidValue_SetsCorrectly()
    {
        _service.SetMaxConcurrentWrites("folder1", 5);

        Assert.Equal(5, _service.GetMaxConcurrentWrites("folder1"));
    }

    [Fact]
    public void SetMaxConcurrentWrites_Zero_AllowsUnlimited()
    {
        _service.SetMaxConcurrentWrites("folder1", 0);

        Assert.Equal(0, _service.GetMaxConcurrentWrites("folder1"));
    }

    [Fact]
    public void SetMaxConcurrentWrites_Negative_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _service.SetMaxConcurrentWrites("folder1", -1));
    }

    [Fact]
    public void SetMaxConcurrentWrites_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.SetMaxConcurrentWrites(null!, 5));
    }

    #endregion

    #region Stats Tests

    [Fact]
    public void GetStats_Initial_ReturnsZeros()
    {
        var stats = _service.GetStats("folder1");

        Assert.Equal("folder1", stats.FolderId);
        Assert.Equal(0, stats.CurrentActiveWrites);
        Assert.Equal(0, stats.TotalWritesAcquired);
        Assert.Equal(0, stats.TotalWritesReleased);
    }

    [Fact]
    public async Task GetStats_AfterAcquireAndRelease_TracksCorrectly()
    {
        using (var slot = await _service.AcquireWriteSlotAsync("folder1", "file.txt"))
        {
            var stats = _service.GetStats("folder1");
            Assert.Equal(1, stats.CurrentActiveWrites);
            Assert.Equal(1, stats.TotalWritesAcquired);
        }

        var finalStats = _service.GetStats("folder1");
        Assert.Equal(0, finalStats.CurrentActiveWrites);
        Assert.Equal(1, finalStats.TotalWritesReleased);
    }

    [Fact]
    public async Task GetStats_MultipleFiles_TracksAll()
    {
        using var slot1 = await _service.AcquireWriteSlotAsync("folder1", "file1.txt");
        using var slot2 = await _service.AcquireWriteSlotAsync("folder1", "file2.txt");

        var stats = _service.GetStats("folder1");

        Assert.Equal(2, stats.CurrentActiveWrites);
        Assert.Equal(2, stats.TotalFilesWithActiveWrites);
    }

    #endregion

    #region Path Normalization Tests

    [Fact]
    public async Task PathNormalization_BackslashAndForwardSlash_TreatedSame()
    {
        _service.SetMaxConcurrentWrites("folder1", 1);

        using var slot = await _service.AcquireWriteSlotAsync("folder1", "path/to/file.txt");

        Assert.Equal(1, _service.GetActiveWriters("folder1", "path\\to\\file.txt"));
    }

    [Fact]
    public async Task PathNormalization_CaseInsensitive()
    {
        _service.SetMaxConcurrentWrites("folder1", 1);

        using var slot = await _service.AcquireWriteSlotAsync("folder1", "File.TXT");

        Assert.Equal(1, _service.GetActiveWriters("folder1", "file.txt"));
    }

    #endregion

    #region Null Argument Tests

    [Fact]
    public async Task AcquireWriteSlotAsync_NullFolderId_ThrowsArgumentNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.AcquireWriteSlotAsync(null!, "file.txt"));
    }

    [Fact]
    public async Task AcquireWriteSlotAsync_NullFilePath_ThrowsArgumentNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _service.AcquireWriteSlotAsync("folder1", null!));
    }

    [Fact]
    public void TryAcquireWriteSlot_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.TryAcquireWriteSlot(null!, "file.txt", out _));
    }

    [Fact]
    public void GetActiveWriters_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetActiveWriters(null!, "file.txt"));
    }

    [Fact]
    public void GetStats_NullFolderId_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.GetStats(null!));
    }

    #endregion
}

public class MaxConcurrentWritesConfigurationTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        var config = new MaxConcurrentWritesConfiguration();

        Assert.Equal(2, config.DefaultMaxConcurrentWrites);
        Assert.Equal(TimeSpan.FromMinutes(5), config.AcquireTimeout);
    }

    [Fact]
    public void GetEffectiveMaxWrites_NoOverride_ReturnsDefault()
    {
        var config = new MaxConcurrentWritesConfiguration { DefaultMaxConcurrentWrites = 5 };

        Assert.Equal(5, config.GetEffectiveMaxWrites("folder1"));
    }

    [Fact]
    public void GetEffectiveMaxWrites_WithOverride_ReturnsOverride()
    {
        var config = new MaxConcurrentWritesConfiguration { DefaultMaxConcurrentWrites = 5 };
        config.FolderMaxWrites["folder1"] = 10;

        Assert.Equal(10, config.GetEffectiveMaxWrites("folder1"));
        Assert.Equal(5, config.GetEffectiveMaxWrites("folder2"));
    }
}
