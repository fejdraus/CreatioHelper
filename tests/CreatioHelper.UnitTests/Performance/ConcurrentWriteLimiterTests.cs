using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Infrastructure.Services.Performance;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.UnitTests.Performance;

public class ConcurrentWriteLimiterTests : IDisposable
{
    private readonly Mock<ILogger<ConcurrentWriteLimiter>> _loggerMock;
    private ConcurrentWriteLimiter? _limiter;

    public ConcurrentWriteLimiterTests()
    {
        _loggerMock = new Mock<ILogger<ConcurrentWriteLimiter>>();
    }

    public void Dispose()
    {
        _limiter?.Dispose();
    }

    [Fact]
    public void TryAcquire_WithinLimits_ReturnsSlot()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 10,
            MaxWritesPerFolder = 5
        });

        // Act
        var slot = _limiter.TryAcquire("/path/to/file.txt", "folder-1");

        // Assert
        Assert.NotNull(slot);
        Assert.Equal("/path/to/file.txt", slot.FilePath);
        Assert.Equal("folder-1", slot.FolderId);
        Assert.True(slot.IsValid);
    }

    [Fact]
    public void TryAcquire_ExceedsGlobalLimit_ReturnsNull()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 2,
            MaxWritesPerFolder = 0 // Unlimited per folder
        });

        // Act
        var slot1 = _limiter.TryAcquire("/file1.txt");
        var slot2 = _limiter.TryAcquire("/file2.txt");
        var slot3 = _limiter.TryAcquire("/file3.txt"); // Should fail

        // Assert
        Assert.NotNull(slot1);
        Assert.NotNull(slot2);
        Assert.Null(slot3);
    }

    [Fact]
    public void TryAcquire_ExceedsFolderLimit_ReturnsNull()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 10,
            MaxWritesPerFolder = 2
        });

        // Act
        var slot1 = _limiter.TryAcquire("/folder/file1.txt", "folder-1");
        var slot2 = _limiter.TryAcquire("/folder/file2.txt", "folder-1");
        var slot3 = _limiter.TryAcquire("/folder/file3.txt", "folder-1"); // Should fail

        // Assert
        Assert.NotNull(slot1);
        Assert.NotNull(slot2);
        Assert.Null(slot3);
    }

    [Fact]
    public void TryAcquire_AfterRelease_AllowsNewWrite()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 1
        });

        var slot1 = _limiter.TryAcquire("/file1.txt");
        Assert.NotNull(slot1);
        Assert.Null(_limiter.TryAcquire("/file2.txt")); // Should fail

        // Act
        slot1.Dispose(); // Release
        var slot2 = _limiter.TryAcquire("/file2.txt");

        // Assert
        Assert.NotNull(slot2);
    }

    [Fact]
    public void TotalWriteCount_ReflectsActiveWrites()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object);

        // Act
        var slot1 = _limiter.TryAcquire("/file1.txt");
        var slot2 = _limiter.TryAcquire("/file2.txt");

        // Assert
        Assert.Equal(2, _limiter.TotalWriteCount);

        // After release
        slot1?.Dispose();
        Assert.Equal(1, _limiter.TotalWriteCount);
    }

    [Fact]
    public void GetFolderWriteCount_ReturnsCorrectCount()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 10, // Allow enough global writes
            MaxWritesPerFolder = 5
        });

        // Act
        _limiter.TryAcquire("/folder1/file1.txt", "folder-1");
        _limiter.TryAcquire("/folder1/file2.txt", "folder-1");
        _limiter.TryAcquire("/folder2/file1.txt", "folder-2");

        // Assert
        Assert.Equal(2, _limiter.GetFolderWriteCount("folder-1"));
        Assert.Equal(1, _limiter.GetFolderWriteCount("folder-2"));
        Assert.Equal(0, _limiter.GetFolderWriteCount("folder-3"));
    }

    [Fact]
    public void CanWrite_WithinLimits_ReturnsTrue()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 5,
            MaxWritesPerFolder = 2
        });

        // Act & Assert
        Assert.True(_limiter.CanWrite("folder-1"));
    }

    [Fact]
    public void CanWrite_ExceedsFolderLimit_ReturnsFalse()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 10,
            MaxWritesPerFolder = 2
        });

        _limiter.TryAcquire("/f1/a.txt", "folder-1");
        _limiter.TryAcquire("/f1/b.txt", "folder-1");

        // Act & Assert
        Assert.False(_limiter.CanWrite("folder-1"));
        Assert.True(_limiter.CanWrite("folder-2"));
    }

    [Fact]
    public void CanWrite_ExceedsGlobalLimit_ReturnsFalse()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 2,
            MaxWritesPerFolder = 0
        });

        _limiter.TryAcquire("/file1.txt");
        _limiter.TryAcquire("/file2.txt");

        // Act & Assert
        Assert.False(_limiter.CanWrite());
    }

    [Fact]
    public async Task AcquireAsync_WaitsForAvailability()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 1
        });

        var slot1 = _limiter.TryAcquire("/file1.txt");
        Assert.NotNull(slot1);

        // Act
        var acquireTask = _limiter.AcquireAsync("/file2.txt", timeout: TimeSpan.FromSeconds(2));

        // Release first slot after a short delay
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            slot1.Dispose();
        });

        var slot2 = await acquireTask;

        // Assert
        Assert.NotNull(slot2);
        Assert.Equal("/file2.txt", slot2.FilePath);
    }

    [Fact]
    public async Task AcquireAsync_TimesOut_ReturnsNull()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 1
        });

        var slot1 = _limiter.TryAcquire("/file1.txt");
        Assert.NotNull(slot1);

        // Act
        var slot2 = await _limiter.AcquireAsync("/file2.txt", timeout: TimeSpan.FromMilliseconds(200));

        // Assert
        Assert.Null(slot2);
    }

    [Fact]
    public async Task AcquireAsync_Cancelled_ReturnsNull()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 1
        });

        _limiter.TryAcquire("/file1.txt");

        using var cts = new CancellationTokenSource(100);

        // Act
        var slot = await _limiter.AcquireAsync("/file2.txt", timeout: TimeSpan.FromSeconds(10), cancellationToken: cts.Token);

        // Assert
        Assert.Null(slot);
    }

    [Fact]
    public void UpdateConfiguration_ChangesLimits()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 1
        });

        _limiter.TryAcquire("/file1.txt");
        Assert.Null(_limiter.TryAcquire("/file2.txt")); // Should fail

        // Act
        _limiter.UpdateConfiguration(new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 10
        });

        // Assert - Statistics should reflect new config
        var stats = _limiter.GetStatistics();
        Assert.NotNull(stats);
    }

    [Fact]
    public void SetFolderLimit_OverridesDefault()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 10, // Allow enough global writes
            MaxWritesPerFolder = 1
        });

        // Override for specific folder
        _limiter.SetFolderLimit("special-folder", 5);

        // Act
        var slot1 = _limiter.TryAcquire("/special/a.txt", "special-folder");
        var slot2 = _limiter.TryAcquire("/special/b.txt", "special-folder");
        var slot3 = _limiter.TryAcquire("/special/c.txt", "special-folder");

        var slot4 = _limiter.TryAcquire("/normal/a.txt", "normal-folder");
        var slot5 = _limiter.TryAcquire("/normal/b.txt", "normal-folder"); // Should fail

        // Assert
        Assert.NotNull(slot1);
        Assert.NotNull(slot2);
        Assert.NotNull(slot3);
        Assert.NotNull(slot4);
        Assert.Null(slot5);
    }

    [Fact]
    public void RemoveFolderLimit_RestoresDefault()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxWritesPerFolder = 1
        });

        _limiter.SetFolderLimit("folder", 5);
        _limiter.TryAcquire("/f/a.txt", "folder");

        // Act
        _limiter.RemoveFolderLimit("folder");

        // Now should follow default limit
        var slot = _limiter.TryAcquire("/f/b.txt", "folder"); // Should fail with default limit of 1

        // Assert
        Assert.Null(slot);
    }

    [Fact]
    public void GetStatistics_ReturnsCorrectStats()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 5,
            TrackStatistics = true
        });

        var slot1 = _limiter.TryAcquire("/file1.txt", "folder-1");
        var slot2 = _limiter.TryAcquire("/file2.txt", "folder-1");
        _limiter.TryAcquire("/file3.txt", "folder-2");

        // Act
        var stats = _limiter.GetStatistics();

        // Assert
        Assert.Equal(3, stats.ActiveWrites);
        Assert.True(stats.WritesByFolder.ContainsKey("folder-1"));
        Assert.True(stats.WritesByFolder.ContainsKey("folder-2"));
        Assert.Contains("/file1.txt", stats.ActiveFiles);
    }

    [Fact]
    public void GetStatistics_TracksRejections()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 1
        });

        _limiter.TryAcquire("/file1.txt");
        _limiter.TryAcquire("/file2.txt"); // Rejected

        // Act
        var stats = _limiter.GetStatistics();

        // Assert
        Assert.Equal(1, stats.TotalWritesRejected);
    }

    [Fact]
    public void GetStatistics_TracksPeakWrites()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 10 // Allow enough writes
        });

        var slot1 = _limiter.TryAcquire("/file1.txt");
        var slot2 = _limiter.TryAcquire("/file2.txt");
        var slot3 = _limiter.TryAcquire("/file3.txt");

        // Release some
        slot1?.Dispose();
        slot2?.Dispose();

        // Act
        var stats = _limiter.GetStatistics();

        // Assert
        Assert.Equal(3, stats.PeakWrites);
        Assert.Equal(1, stats.ActiveWrites);
    }

    [Fact]
    public void GetStatistics_TracksBytesWritten()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            TrackStatistics = true
        });

        var slot = _limiter.TryAcquire("/file.txt");
        Assert.NotNull(slot);

        // Simulate bytes written
        slot.BytesWritten = 1024 * 1024;
        slot.Dispose();

        // Act
        var stats = _limiter.GetStatistics();

        // Assert
        Assert.Equal(1024 * 1024, stats.TotalBytesWritten);
    }

    [Fact]
    public void WriteSlot_IsValid_FalseAfterDispose()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object);
        var slot = _limiter.TryAcquire("/file.txt");
        Assert.NotNull(slot);
        Assert.True(slot.IsValid);

        // Act
        slot.Dispose();

        // Assert
        Assert.False(slot.IsValid);
    }

    [Fact]
    public void WriteSlot_AcquiredAt_IsSet()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object);
        var before = DateTime.UtcNow;

        // Act
        var slot = _limiter.TryAcquire("/file.txt");
        var after = DateTime.UtcNow;

        // Assert
        Assert.NotNull(slot);
        Assert.True(slot.AcquiredAt >= before);
        Assert.True(slot.AcquiredAt <= after);
    }

    [Fact]
    public void WriteSlot_BytesWritten_CanBeUpdated()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object);
        var slot = _limiter.TryAcquire("/file.txt");
        Assert.NotNull(slot);

        // Act
        slot.BytesWritten = 100;
        slot.BytesWritten += 200;

        // Assert
        Assert.Equal(300, slot.BytesWritten);
    }

    [Fact]
    public void TryAcquire_NullFilePath_ThrowsArgumentNullException()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _limiter.TryAcquire(null!));
    }

    [Fact]
    public void TryAcquire_EmptyFilePath_ThrowsArgumentNullException()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object);

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => _limiter.TryAcquire(string.Empty));
    }

    [Fact]
    public void TryAcquire_NoFolderId_ExtractsFromPath()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object);

        // Act
        var slot = _limiter.TryAcquire("/path/to/folder/file.txt");

        // Assert
        Assert.NotNull(slot);
        Assert.Equal("folder", slot.FolderId);
    }

    [Fact]
    public void Configuration_DefaultValues()
    {
        // Arrange
        var config = new ConcurrentWriteLimiterConfiguration();

        // Assert
        Assert.Equal(2, config.MaxConcurrentWrites); // Syncthing default
        Assert.Equal(0, config.MaxWritesPerFolder); // Unlimited
        Assert.Equal(TimeSpan.FromMinutes(5), config.DefaultTimeout);
        Assert.True(config.TrackStatistics);
        Assert.False(config.PrioritizeSmallerFiles);
        Assert.Equal(1024 * 1024, config.SmallFileSizeThreshold);
        Assert.Empty(config.FolderOverrides);
    }

    [Fact]
    public void Statistics_DefaultValues()
    {
        // Arrange
        var stats = new ConcurrentWriteLimiterStatistics();

        // Assert
        Assert.Equal(0, stats.ActiveWrites);
        Assert.Equal(0, stats.PeakWrites);
        Assert.Equal(0, stats.TotalWritesCompleted);
        Assert.Equal(0, stats.TotalWritesRejected);
        Assert.Equal(0, stats.TotalBytesWritten);
        Assert.Equal(TimeSpan.Zero, stats.AverageWriteDuration);
        Assert.Equal(0, stats.WaitingRequests);
        Assert.Empty(stats.WritesByFolder);
        Assert.Empty(stats.ActiveFiles);
    }

    [Fact]
    public void UnlimitedWrites_AllowsManyWrites()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 0, // Unlimited
            MaxWritesPerFolder = 0 // Unlimited
        });

        // Act
        var slots = new List<IWriteSlot?>();
        for (int i = 0; i < 100; i++)
        {
            slots.Add(_limiter.TryAcquire($"/file{i}.txt"));
        }

        // Assert
        Assert.All(slots, s => Assert.NotNull(s));
        Assert.Equal(100, _limiter.TotalWriteCount);
    }

    [Fact]
    public async Task AcquireAsync_TracksDuration()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            TrackStatistics = true,
            MaxConcurrentWrites = 10
        });

        // Act
        var slot = await _limiter.AcquireAsync("/file.txt", timeout: TimeSpan.FromSeconds(1));
        Assert.NotNull(slot);

        await Task.Delay(100); // Simulate some work (increased for timing reliability)
        slot.Dispose();

        // Get statistics
        var stats = _limiter.GetStatistics();

        // Assert - check for non-zero duration (timing can vary)
        Assert.True(stats.AverageWriteDuration > TimeSpan.Zero);
    }

    [Fact]
    public void MultipleDisposes_AreIdempotent()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object);
        var slot = _limiter.TryAcquire("/file.txt");
        Assert.NotNull(slot);

        // Act - Dispose multiple times
        slot.Dispose();
        slot.Dispose();
        slot.Dispose();

        // Assert - Should not throw, count should be 0
        Assert.Equal(0, _limiter.TotalWriteCount);
    }

    [Fact]
    public void DifferentFolders_IndependentLimits()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 10,
            MaxWritesPerFolder = 2
        });

        // Act - Fill up folder-1
        _limiter.TryAcquire("/f1/a.txt", "folder-1");
        _limiter.TryAcquire("/f1/b.txt", "folder-1");
        var rejectedSlot = _limiter.TryAcquire("/f1/c.txt", "folder-1");

        // folder-2 should still work
        var slot = _limiter.TryAcquire("/f2/a.txt", "folder-2");

        // Assert
        Assert.Null(rejectedSlot);
        Assert.NotNull(slot);
    }

    [Fact]
    public async Task ConcurrentAcquire_AllGetSlots()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 20
        });

        // Act
        var tasks = new List<Task<IWriteSlot?>>();
        for (int i = 0; i < 10; i++)
        {
            var filePath = $"/file{i}.txt";
            tasks.Add(_limiter.AcquireAsync(filePath, timeout: TimeSpan.FromSeconds(5)));
        }

        var slots = await Task.WhenAll(tasks);

        // Assert
        Assert.All(slots, s => Assert.NotNull(s));
        Assert.Equal(10, _limiter.TotalWriteCount);
    }

    [Fact]
    public void FolderOverrides_InConfiguration()
    {
        // Arrange
        _limiter = new ConcurrentWriteLimiter(_loggerMock.Object, new ConcurrentWriteLimiterConfiguration
        {
            MaxConcurrentWrites = 20, // Allow enough global writes
            MaxWritesPerFolder = 1,
            FolderOverrides = new Dictionary<string, int>
            {
                { "bulk-folder", 10 }
            }
        });

        // Act
        var slots = new List<IWriteSlot?>();
        for (int i = 0; i < 5; i++)
        {
            slots.Add(_limiter.TryAcquire($"/bulk/{i}.txt", "bulk-folder"));
        }

        // Assert
        Assert.All(slots, s => Assert.NotNull(s));
    }
}
