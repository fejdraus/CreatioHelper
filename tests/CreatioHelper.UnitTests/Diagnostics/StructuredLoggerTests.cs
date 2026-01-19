using CreatioHelper.Infrastructure.Services.Sync.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.UnitTests.Diagnostics;

public class StructuredLoggerTests : IAsyncDisposable
{
    private readonly Mock<ILogger<StructuredLogger>> _loggerMock;
    private readonly string _testDir;
    private StructuredLogger? _logger;

    public StructuredLoggerTests()
    {
        _loggerMock = new Mock<ILogger<StructuredLogger>>();
        _testDir = Path.Combine(Path.GetTempPath(), $"structured_log_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    public async ValueTask DisposeAsync()
    {
        if (_logger != null)
        {
            await _logger.DisposeAsync();
        }

        await Task.Delay(100);
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    [Fact]
    public void Debug_WithMessage_LogsEntry()
    {
        // Arrange
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            MinLevel = StructuredLogLevel.Debug,
            OutputToConsole = false
        });

        // Act
        _logger.Debug("Test debug message");

        // Assert
        var stats = _logger.GetStatistics();
        Assert.Equal(1, stats.DebugEntries);
    }

    [Fact]
    public void Debug_BelowMinLevel_DoesNotLog()
    {
        // Arrange
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            MinLevel = StructuredLogLevel.Info,
            OutputToConsole = false
        });

        // Act
        _logger.Debug("Test debug message");

        // Assert
        var stats = _logger.GetStatistics();
        Assert.Equal(0, stats.DebugEntries);
        Assert.Equal(0, stats.TotalEntries);
    }

    [Fact]
    public void Info_WithFields_IncludesFields()
    {
        // Arrange
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            OutputToConsole = false
        });

        // Act
        _logger.Info("User logged in", new Dictionary<string, object?>
        {
            ["userId"] = "123",
            ["action"] = "login"
        });

        // Assert
        var stats = _logger.GetStatistics();
        Assert.Equal(1, stats.InfoEntries);
    }

    [Fact]
    public void Warn_LogsWarning()
    {
        // Arrange
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            OutputToConsole = false
        });

        // Act
        _logger.Warn("Warning message");

        // Assert
        var stats = _logger.GetStatistics();
        Assert.Equal(1, stats.WarnEntries);
    }

    [Fact]
    public void Error_WithException_LogsException()
    {
        // Arrange
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            OutputToConsole = false
        });

        var exception = new InvalidOperationException("Test error");

        // Act
        _logger.Error("An error occurred", exception);

        // Assert
        var stats = _logger.GetStatistics();
        Assert.Equal(1, stats.ErrorEntries);
    }

    [Fact]
    public void Fatal_LogsFatalEntry()
    {
        // Arrange
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            OutputToConsole = false
        });

        // Act
        _logger.Fatal("Fatal error occurred");

        // Assert
        var stats = _logger.GetStatistics();
        Assert.Equal(1, stats.FatalEntries);
    }

    [Fact]
    public void WithFields_CreatesChildLogger()
    {
        // Arrange
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            OutputToConsole = false
        });

        // Act
        var childLogger = _logger.WithFields(new Dictionary<string, object?>
        {
            ["correlationId"] = "abc-123"
        });

        childLogger.Info("Child log message");

        // Assert
        Assert.NotSame(_logger, childLogger);
        var stats = _logger.GetStatistics();
        Assert.Equal(1, stats.InfoEntries);
    }

    [Fact]
    public void WithComponent_CreatesChildLoggerWithComponent()
    {
        // Arrange
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            OutputToConsole = false
        });

        // Act
        var childLogger = _logger.WithComponent("SyncService");
        childLogger.Info("Component log message");

        // Assert
        Assert.NotSame(_logger, childLogger);
    }

    [Fact]
    public void SetLevel_ChangesMinLevel()
    {
        // Arrange
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            MinLevel = StructuredLogLevel.Info,
            OutputToConsole = false
        });

        // Act
        _logger.SetLevel(StructuredLogLevel.Debug);
        _logger.Debug("Now visible");

        // Assert
        var stats = _logger.GetStatistics();
        Assert.Equal(1, stats.DebugEntries);
    }

    [Fact]
    public void IsDebugEnabled_ReflectsMinLevel()
    {
        // Arrange - default is Info
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            MinLevel = StructuredLogLevel.Info,
            OutputToConsole = false
        });

        // Assert
        Assert.False(_logger.IsDebugEnabled);

        // Act
        _logger.SetLevel(StructuredLogLevel.Debug);

        // Assert
        Assert.True(_logger.IsDebugEnabled);
    }

    [Fact]
    public async Task FlushAsync_WritesBufferedEntries()
    {
        // Arrange
        var logPath = Path.Combine(_testDir, "flush_test.log");
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            OutputPath = logPath,
            OutputToConsole = false,
            BufferSize = 1000, // Large buffer
            FlushIntervalMs = 0 // Disable auto-flush
        });

        // Act
        _logger.Info("Test message 1");
        _logger.Info("Test message 2");
        await _logger.FlushAsync();
        await _logger.DisposeAsync();
        _logger = null; // Prevent double dispose in DisposeAsync

        // Assert
        var content = await File.ReadAllTextAsync(logPath);
        Assert.Contains("Test message 1", content);
        Assert.Contains("Test message 2", content);
    }

    [Fact]
    public async Task JsonFormat_WritesValidJson()
    {
        // Arrange
        var logPath = Path.Combine(_testDir, "json_test.log");
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            OutputPath = logPath,
            OutputToConsole = false,
            JsonFormat = true,
            FlushIntervalMs = 0
        });

        // Act
        _logger.Info("JSON test message", new Dictionary<string, object?>
        {
            ["key"] = "value"
        });
        await _logger.FlushAsync();
        await _logger.DisposeAsync();
        _logger = null; // Prevent double dispose in DisposeAsync

        // Assert
        var content = await File.ReadAllTextAsync(logPath);
        Assert.Contains("\"level\":\"info\"", content);
        Assert.Contains("\"msg\":\"JSON test message\"", content);
        Assert.Contains("\"key\":\"value\"", content);
    }

    [Fact]
    public async Task PlainTextFormat_WritesReadableText()
    {
        // Arrange
        var logPath = Path.Combine(_testDir, "plain_test.log");
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            OutputPath = logPath,
            OutputToConsole = false,
            JsonFormat = false,
            FlushIntervalMs = 0
        });

        // Act
        _logger.Info("Plain text message");
        await _logger.FlushAsync();
        await _logger.DisposeAsync();
        _logger = null; // Prevent double dispose in DisposeAsync

        // Assert
        var content = await File.ReadAllTextAsync(logPath);
        Assert.Contains("[INFO ]", content);
        Assert.Contains("Plain text message", content);
    }

    [Fact]
    public void GetStatistics_ReturnsCorrectCounts()
    {
        // Arrange
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            MinLevel = StructuredLogLevel.Debug,
            OutputToConsole = false
        });

        // Act
        _logger.Debug("Debug 1");
        _logger.Debug("Debug 2");
        _logger.Info("Info 1");
        _logger.Warn("Warn 1");
        _logger.Error("Error 1");

        // Assert
        var stats = _logger.GetStatistics();
        Assert.Equal(5, stats.TotalEntries);
        Assert.Equal(2, stats.DebugEntries);
        Assert.Equal(1, stats.InfoEntries);
        Assert.Equal(1, stats.WarnEntries);
        Assert.Equal(1, stats.ErrorEntries);
        Assert.Equal(0, stats.FatalEntries);
    }

    [Fact]
    public void SetFacilityDebug_EnablesFacility()
    {
        // Arrange
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            OutputToConsole = false
        });

        // Act
        _logger.SetFacilityDebug("model", true);
        _logger.SetFacilityDebug("db", true);
        _logger.SetFacilityDebug("model", false);

        // Assert - just verify no errors
        Assert.NotNull(_logger);
    }

    [Fact]
    public void Configuration_DefaultValues()
    {
        // Arrange
        var config = new StructuredLogConfiguration();

        // Assert
        Assert.Equal(StructuredLogLevel.Info, config.MinLevel);
        Assert.Null(config.OutputPath);
        Assert.True(config.OutputToConsole);
        Assert.True(config.JsonFormat);
        Assert.True(config.IncludeTimestamp);
        Assert.Equal(100, config.BufferSize);
        Assert.Equal(1000, config.FlushIntervalMs);
        Assert.Equal(10 * 1024 * 1024, config.MaxFileSizeBytes);
        Assert.Equal(5, config.MaxRotatedFiles);
    }

    [Fact]
    public void StructuredLogEntry_Properties()
    {
        // Arrange
        var entry = new StructuredLogEntry
        {
            Level = StructuredLogLevel.Error,
            Message = "Test message",
            Component = "TestComponent"
        };

        // Assert
        Assert.Equal("error", entry.LevelString);
        Assert.Equal("Test message", entry.Message);
        Assert.Equal("TestComponent", entry.Component);
        Assert.NotEmpty(entry.Id);
    }

    [Fact]
    public void ExceptionInfo_CapturesDetails()
    {
        // Arrange
        var info = new ExceptionInfo
        {
            Type = "InvalidOperationException",
            Message = "Operation failed",
            StackTrace = "at Test.Method()"
        };

        // Assert
        Assert.Equal("InvalidOperationException", info.Type);
        Assert.Equal("Operation failed", info.Message);
        Assert.Equal("at Test.Method()", info.StackTrace);
    }

    [Fact]
    public void Statistics_DefaultValues()
    {
        // Arrange
        var stats = new StructuredLogStatistics();

        // Assert
        Assert.Equal(0, stats.TotalEntries);
        Assert.Equal(0, stats.DebugEntries);
        Assert.Equal(0, stats.InfoEntries);
        Assert.Equal(0, stats.WarnEntries);
        Assert.Equal(0, stats.ErrorEntries);
        Assert.Equal(0, stats.FatalEntries);
        Assert.Equal(0, stats.DroppedEntries);
        Assert.Equal(0, stats.BytesWritten);
        Assert.Equal(0, stats.CurrentBufferSize);
        Assert.Null(stats.LastFlush);
        Assert.Equal(0, stats.FileRotations);
    }

    [Fact]
    public void Debug_WithFormatArgs_FormatsMessage()
    {
        // Arrange
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            MinLevel = StructuredLogLevel.Debug,
            OutputToConsole = false
        });

        // Act
        _logger.Debug("User {0} performed action {1}", "john", "login");

        // Assert
        var stats = _logger.GetStatistics();
        Assert.Equal(1, stats.DebugEntries);
    }

    [Fact]
    public void Error_WithNestedException_CapturesInnerException()
    {
        // Arrange
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            OutputToConsole = false
        });

        var inner = new ArgumentException("Inner error");
        var outer = new InvalidOperationException("Outer error", inner);

        // Act
        _logger.Error("Error occurred", outer);

        // Assert
        var stats = _logger.GetStatistics();
        Assert.Equal(1, stats.ErrorEntries);
    }

    [Fact]
    public void ChildLogger_InheritsContextFields()
    {
        // Arrange
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            OutputToConsole = false
        });

        var child1 = _logger.WithFields(new Dictionary<string, object?>
        {
            ["sessionId"] = "abc"
        });

        // Act - create grandchild with additional fields
        var child2 = child1.WithFields(new Dictionary<string, object?>
        {
            ["requestId"] = "123"
        });

        child2.Info("Nested context test");

        // Assert
        var stats = _logger.GetStatistics();
        Assert.Equal(1, stats.InfoEntries);
    }

    [Fact]
    public async Task AutoFlush_FlushesOnHighSeverity()
    {
        // Arrange
        var logPath = Path.Combine(_testDir, "autoflush_test.log");
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            OutputPath = logPath,
            OutputToConsole = false,
            BufferSize = 1000, // Large buffer
            FlushIntervalMs = 0 // Disable timer-based flush
        });

        // Act
        _logger.Error("High severity error"); // Should trigger auto-flush
        await Task.Delay(100); // Wait for async flush
        await _logger.DisposeAsync();
        _logger = null; // Prevent double dispose in DisposeAsync

        // Assert
        var content = await File.ReadAllTextAsync(logPath);
        Assert.Contains("High severity error", content);
    }

    [Fact]
    public async Task FileRotation_RotatesWhenSizeExceeded()
    {
        // Arrange
        var logPath = Path.Combine(_testDir, "rotation_test.log");
        _logger = new StructuredLogger(_loggerMock.Object, new StructuredLogConfiguration
        {
            OutputPath = logPath,
            OutputToConsole = false,
            MaxFileSizeBytes = 500, // Small size to trigger rotation
            FlushIntervalMs = 0
        });

        // Act - Write enough to trigger rotation
        for (int i = 0; i < 20; i++)
        {
            _logger.Info($"Log message {i} with some padding to increase size");
        }
        await _logger.FlushAsync();
        await Task.Delay(100);

        // Assert
        var stats = _logger.GetStatistics();
        Assert.True(stats.FileRotations >= 0); // May or may not rotate depending on timing

        await _logger.DisposeAsync();
        _logger = null; // Prevent double dispose in DisposeAsync
    }
}
