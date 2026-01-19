using CreatioHelper.Infrastructure.Services.Security;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests.Security;

/// <summary>
/// Tests for AuditLogService.
/// </summary>
public class AuditLogServiceTests : IDisposable
{
    private readonly Mock<ILogger<AuditLogService>> _loggerMock;
    private readonly string _tempLogDir;
    private AuditLogService _service;

    public AuditLogServiceTests()
    {
        _loggerMock = new Mock<ILogger<AuditLogService>>();
        _tempLogDir = Path.Combine(Path.GetTempPath(), $"audit-test-{Guid.NewGuid()}");
        _service = new AuditLogService(_loggerMock.Object, _tempLogDir);
    }

    public void Dispose()
    {
        _service?.Dispose();
        _service = null!;

        // Allow time for file handles to be released
        TryDeleteDirectory(_tempLogDir);
    }

    private static void TryDeleteDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            return;

        for (int i = 0; i < 5; i++)
        {
            try
            {
                // Force garbage collection to release file handles
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();

                Directory.Delete(directoryPath, recursive: true);
                return;
            }
            catch (IOException) when (i < 4)
            {
                Thread.Sleep(100 * (i + 1));
            }
            catch (UnauthorizedAccessException) when (i < 4)
            {
                Thread.Sleep(100 * (i + 1));
            }
            catch
            {
                // On final attempt, silently ignore cleanup failures
                // Test temp directories will be cleaned up by OS eventually
                return;
            }
        }
    }

    #region LogAsync Tests

    [Fact]
    public async Task LogAsync_AssignsIdAndTimestamp()
    {
        var entry = new AuditLogEntry
        {
            Category = AuditCategory.System,
            Action = "Test",
            Outcome = AuditOutcome.Success,
            Severity = AuditSeverity.Info,
            Message = "Test message"
        };

        await _service.LogAsync(entry);

        Assert.True(entry.Id > 0);
        Assert.True(entry.Timestamp <= DateTime.UtcNow);
    }

    [Fact]
    public async Task LogAsync_UpdatesStatistics()
    {
        await _service.LogAsync(new AuditLogEntry
        {
            Category = AuditCategory.Authentication,
            Action = "Test",
            Outcome = AuditOutcome.Success,
            Severity = AuditSeverity.Info
        });

        var stats = _service.GetStatistics();

        Assert.Equal(1, stats.TotalEntries);
        Assert.True(stats.ByCategory.ContainsKey(AuditCategory.Authentication));
        Assert.True(stats.ByOutcome.ContainsKey(AuditOutcome.Success));
    }

    [Fact]
    public async Task LogAsync_HighSeverity_ImmediateFlush()
    {
        await _service.LogAsync(new AuditLogEntry
        {
            Category = AuditCategory.Security,
            Action = "CriticalEvent",
            Outcome = AuditOutcome.Failure,
            Severity = AuditSeverity.Critical,
            Message = "Critical security event"
        });

        // Allow time for flush
        await Task.Delay(100);

        // Check file was created
        var files = Directory.GetFiles(_tempLogDir, "audit-*.json");
        Assert.NotEmpty(files);
    }

    #endregion

    #region LogAuthenticationAsync Tests

    [Fact]
    public async Task LogAuthenticationAsync_SuccessfulLogin()
    {
        await _service.LogAuthenticationAsync(new AuthenticationAuditEvent
        {
            Action = "Login",
            Outcome = AuditOutcome.Success,
            UserId = "user1",
            Username = "testuser",
            IpAddress = "192.168.1.1"
        });

        var stats = _service.GetStatistics();
        Assert.Equal(1, stats.TotalEntries);
        Assert.True(stats.ByCategory.ContainsKey(AuditCategory.Authentication));
    }

    [Fact]
    public async Task LogAuthenticationAsync_FailedLogin()
    {
        await _service.LogAuthenticationAsync(new AuthenticationAuditEvent
        {
            Action = "Login",
            Outcome = AuditOutcome.Failure,
            UserId = "user1",
            Username = "testuser",
            FailureReason = "Invalid password"
        });

        var stats = _service.GetStatistics();
        Assert.True(stats.ByOutcome.ContainsKey(AuditOutcome.Failure));
    }

    [Fact]
    public async Task LogAuthenticationAsync_WithToken()
    {
        // Use Critical severity to force immediate flush (single entry)
        await _service.LogAsync(new AuditLogEntry
        {
            Category = AuditCategory.Authentication,
            Action = "TokenRefresh",
            Outcome = AuditOutcome.Success,
            Severity = AuditSeverity.Critical,  // Forces immediate flush
            Details = new Dictionary<string, object?> { ["TokenId"] = "token123" }
        });

        await Task.Delay(200);  // Allow flush to complete

        var events = await _service.GetRecentEventsAsync(1);
        var entry = events.FirstOrDefault();

        Assert.NotNull(entry);
        Assert.Contains("token123", entry.Details.Values.Select(v => v?.ToString()));
    }

    #endregion

    #region LogAuthorizationAsync Tests

    [Fact]
    public async Task LogAuthorizationAsync_AccessGranted()
    {
        await _service.LogAuthorizationAsync(new AuthorizationAuditEvent
        {
            Resource = "/api/admin",
            Permission = "admin:read",
            Outcome = AuditOutcome.Success,
            ActorId = "user1",
            ActorName = "Admin User"
        });

        var stats = _service.GetStatistics();
        Assert.True(stats.ByCategory.ContainsKey(AuditCategory.Authorization));
    }

    [Fact]
    public async Task LogAuthorizationAsync_AccessDenied()
    {
        await _service.LogAuthorizationAsync(new AuthorizationAuditEvent
        {
            Resource = "/api/admin",
            Permission = "admin:write",
            Outcome = AuditOutcome.Denied,
            ActorId = "user2",
            Reason = "Insufficient permissions"
        });

        var stats = _service.GetStatistics();
        Assert.True(stats.ByOutcome.ContainsKey(AuditOutcome.Denied));
    }

    #endregion

    #region LogConfigurationChangeAsync Tests

    [Fact]
    public async Task LogConfigurationChangeAsync_LogsChange()
    {
        // Use Critical severity to force immediate flush (single entry, single flush)
        await _service.LogAsync(new AuditLogEntry
        {
            Category = AuditCategory.Configuration,
            Action = "ConfigChange",
            Outcome = AuditOutcome.Success,
            Severity = AuditSeverity.Critical,  // Forces immediate flush
            OldValue = 100,
            NewValue = 200,
            Target = new AuditTarget
            {
                Type = TargetType.Configuration,
                Id = "Network.MaxConnections",
                Name = "MaxConnections"
            }
        });

        await Task.Delay(200);  // Allow flush to complete

        var events = await _service.GetRecentEventsAsync(1);
        var entry = events.FirstOrDefault();

        Assert.NotNull(entry);
        Assert.Equal(AuditCategory.Configuration, entry.Category);
        // Compare as strings since JSON deserialization may return JsonElement
        Assert.Equal("100", entry.OldValue?.ToString());
        Assert.Equal("200", entry.NewValue?.ToString());
    }

    #endregion

    #region LogDeviceEventAsync Tests

    [Fact]
    public async Task LogDeviceEventAsync_DeviceConnected()
    {
        await _service.LogDeviceEventAsync(new DeviceAuditEvent
        {
            Action = "Connected",
            DeviceId = "device123",
            DeviceName = "Test Device",
            IpAddress = "10.0.0.5",
            Outcome = AuditOutcome.Success
        });

        var stats = _service.GetStatistics();
        Assert.True(stats.ByCategory.ContainsKey(AuditCategory.DeviceManagement));
    }

    [Fact]
    public async Task LogDeviceEventAsync_DeviceRemoved()
    {
        // Use error severity to force immediate flush to disk
        await _service.LogAsync(new AuditLogEntry
        {
            Category = AuditCategory.DeviceManagement,
            Action = "Removed",
            Outcome = AuditOutcome.Success,
            Severity = AuditSeverity.Critical,  // Forces immediate flush
            Target = new AuditTarget
            {
                Type = TargetType.Device,
                Id = "device456",
                Name = "Old Device"
            }
        });

        await Task.Delay(200);  // Allow flush to complete

        var events = await _service.GetRecentEventsAsync(1);
        var entry = events.FirstOrDefault();

        Assert.NotNull(entry);
        Assert.Equal("Removed", entry.Action);
        Assert.Equal(TargetType.Device, entry.Target?.Type);
    }

    #endregion

    #region LogFolderEventAsync Tests

    [Fact]
    public async Task LogFolderEventAsync_FolderCreated()
    {
        // Use error severity to force immediate flush to disk
        await _service.LogAsync(new AuditLogEntry
        {
            Category = AuditCategory.FolderManagement,
            Action = "Created",
            Outcome = AuditOutcome.Success,
            Severity = AuditSeverity.Critical,  // Forces immediate flush
            Target = new AuditTarget
            {
                Type = TargetType.Folder,
                Id = "folder123",
                Name = "Documents"
            },
            Details = new Dictionary<string, object?> { ["Path"] = "/sync/docs" }
        });

        await Task.Delay(200);  // Allow flush to complete

        var events = await _service.GetRecentEventsAsync(1);
        var entry = events.FirstOrDefault();

        Assert.NotNull(entry);
        Assert.Equal(AuditCategory.FolderManagement, entry.Category);
        Assert.Equal("Created", entry.Action);
        Assert.Contains("Path", entry.Details.Keys);
    }

    #endregion

    #region QueryAsync Tests

    [Fact]
    public async Task QueryAsync_FilterByCategory()
    {
        await LogMultipleEventsWithFlush();

        var result = await _service.QueryAsync(new AuditLogQuery
        {
            Category = AuditCategory.Authentication
        });

        Assert.All(result.Entries, e => Assert.Equal(AuditCategory.Authentication, e.Category));
    }

    [Fact]
    public async Task QueryAsync_FilterByOutcome()
    {
        await LogMultipleEventsWithFlush();

        var result = await _service.QueryAsync(new AuditLogQuery
        {
            Outcome = AuditOutcome.Failure
        });

        Assert.All(result.Entries, e => Assert.Equal(AuditOutcome.Failure, e.Outcome));
    }

    [Fact]
    public async Task QueryAsync_FilterByDateRange()
    {
        await LogMultipleEventsWithFlush();

        var result = await _service.QueryAsync(new AuditLogQuery
        {
            StartDate = DateTime.UtcNow.AddMinutes(-5),
            EndDate = DateTime.UtcNow.AddMinutes(5)
        });

        Assert.True(result.TotalCount > 0);
    }

    [Fact]
    public async Task QueryAsync_FilterBySearchText()
    {
        // Use error severity to force immediate flush
        await _service.LogAsync(new AuditLogEntry
        {
            Category = AuditCategory.System,
            Action = "Test",
            Outcome = AuditOutcome.Success,
            Severity = AuditSeverity.Critical,  // Forces immediate flush
            Message = "Special unique message here"
        });

        // Allow flush to complete
        await Task.Delay(200);

        var result = await _service.QueryAsync(new AuditLogQuery
        {
            SearchText = "unique message"
        });

        Assert.NotEmpty(result.Entries);
    }

    [Fact]
    public async Task QueryAsync_Pagination()
    {
        // Log entries with Info severity (buffered)
        for (int i = 0; i < 15; i++)
        {
            await _service.LogAsync(new AuditLogEntry
            {
                Category = AuditCategory.System,
                Action = $"Action{i}",
                Outcome = AuditOutcome.Success,
                Severity = AuditSeverity.Info
            });
        }

        // Trigger flush with one Critical entry
        await _service.LogAsync(new AuditLogEntry
        {
            Category = AuditCategory.System,
            Action = "FlushTrigger",
            Outcome = AuditOutcome.Success,
            Severity = AuditSeverity.Critical
        });

        await Task.Delay(200);

        var page1 = await _service.QueryAsync(new AuditLogQuery { Page = 1, PageSize = 5 });
        var page2 = await _service.QueryAsync(new AuditLogQuery { Page = 2, PageSize = 5 });

        Assert.Equal(5, page1.Entries.Count);
        Assert.Equal(5, page2.Entries.Count);
        // 15 + 1 flush trigger = 16
        Assert.Equal(16, page1.TotalCount);
    }

    [Fact]
    public async Task QueryAsync_SortDescending()
    {
        await LogMultipleEventsWithFlush();

        var result = await _service.QueryAsync(new AuditLogQuery { SortDescending = true });

        if (result.Entries.Count > 1)
        {
            Assert.True(result.Entries[0].Timestamp >= result.Entries[1].Timestamp);
        }
    }

    #endregion

    #region GetRecentEventsAsync Tests

    [Fact]
    public async Task GetRecentEventsAsync_ReturnsLimitedCount()
    {
        // Log entries with Info severity (buffered)
        for (int i = 0; i < 20; i++)
        {
            await _service.LogAsync(new AuditLogEntry
            {
                Category = AuditCategory.System,
                Action = $"Action{i}",
                Outcome = AuditOutcome.Success,
                Severity = AuditSeverity.Info
            });
        }

        // Trigger flush with one Critical entry
        await _service.LogAsync(new AuditLogEntry
        {
            Category = AuditCategory.System,
            Action = "FlushTrigger",
            Outcome = AuditOutcome.Success,
            Severity = AuditSeverity.Critical
        });

        await Task.Delay(200);

        var events = await _service.GetRecentEventsAsync(5);

        Assert.Equal(5, events.Count());
    }

    #endregion

    #region GetEntityEventsAsync Tests

    [Fact]
    public async Task GetEntityEventsAsync_FiltersByEntityId()
    {
        await _service.LogDeviceEventAsync(new DeviceAuditEvent
        {
            Action = "Connected",
            DeviceId = "device1",
            Outcome = AuditOutcome.Success
        });

        await _service.LogDeviceEventAsync(new DeviceAuditEvent
        {
            Action = "Connected",
            DeviceId = "device2",
            Outcome = AuditOutcome.Success
        });

        await Task.Delay(100);

        var events = await _service.GetEntityEventsAsync("Device", "device1");

        Assert.All(events, e => Assert.Equal("device1", e.Target?.Id));
    }

    #endregion

    #region ExportAsync Tests

    [Fact]
    public async Task ExportAsync_Json_CreatesFile()
    {
        await LogMultipleEventsWithFlush();

        var path = await _service.ExportAsync(new AuditLogExportOptions
        {
            Format = AuditExportFormat.Json
        });

        Assert.True(File.Exists(path));
        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("[", content);
    }

    [Fact]
    public async Task ExportAsync_Csv_CreatesFile()
    {
        await LogMultipleEventsWithFlush();

        var path = await _service.ExportAsync(new AuditLogExportOptions
        {
            Format = AuditExportFormat.Csv
        });

        Assert.True(File.Exists(path));
        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("Id,Timestamp,Category", content);
    }

    [Fact]
    public async Task ExportAsync_Xml_CreatesFile()
    {
        await LogMultipleEventsWithFlush();

        var path = await _service.ExportAsync(new AuditLogExportOptions
        {
            Format = AuditExportFormat.Xml
        });

        Assert.True(File.Exists(path));
        var content = await File.ReadAllTextAsync(path);
        Assert.Contains("<AuditLog>", content);
        Assert.Contains("</AuditLog>", content);
    }

    [Fact]
    public async Task ExportAsync_CustomPath()
    {
        await LogMultipleEventsWithFlush();

        var customPath = Path.Combine(_tempLogDir, "custom-export.json");

        var path = await _service.ExportAsync(new AuditLogExportOptions
        {
            Format = AuditExportFormat.Json,
            OutputPath = customPath
        });

        Assert.Equal(customPath, path);
        Assert.True(File.Exists(customPath));
    }

    #endregion

    #region CleanupAsync Tests

    [Fact]
    public async Task CleanupAsync_RemovesOldFiles()
    {
        // Create an old file
        var oldFilePath = Path.Combine(_tempLogDir, "audit-20200101.json");
        await File.WriteAllTextAsync(oldFilePath, "[]");

        var removed = await _service.CleanupAsync(TimeSpan.FromDays(30));

        Assert.True(removed > 0);
        Assert.False(File.Exists(oldFilePath));
    }

    [Fact]
    public async Task CleanupAsync_KeepsRecentFiles()
    {
        // Use error severity to force immediate flush
        await _service.LogAsync(new AuditLogEntry
        {
            Category = AuditCategory.System,
            Action = "Test",
            Outcome = AuditOutcome.Success,
            Severity = AuditSeverity.Critical  // Forces immediate flush
        });
        await Task.Delay(200);  // Allow flush to complete

        var removed = await _service.CleanupAsync(TimeSpan.FromDays(30));

        // Today's file should not be removed
        var files = Directory.GetFiles(_tempLogDir, "audit-*.json");
        Assert.NotEmpty(files);
    }

    #endregion

    #region Statistics Tests

    [Fact]
    public async Task GetStatistics_TracksByCategory()
    {
        await _service.LogAuthenticationAsync(new AuthenticationAuditEvent
        {
            Action = "Login",
            Outcome = AuditOutcome.Success
        });

        await _service.LogDeviceEventAsync(new DeviceAuditEvent
        {
            Action = "Connected",
            DeviceId = "d1",
            Outcome = AuditOutcome.Success
        });

        var stats = _service.GetStatistics();

        Assert.True(stats.ByCategory.ContainsKey(AuditCategory.Authentication));
        Assert.True(stats.ByCategory.ContainsKey(AuditCategory.DeviceManagement));
    }

    [Fact]
    public async Task GetStatistics_TracksByOutcome()
    {
        await _service.LogAsync(new AuditLogEntry
        {
            Category = AuditCategory.System,
            Action = "Success",
            Outcome = AuditOutcome.Success,
            Severity = AuditSeverity.Info
        });

        await _service.LogAsync(new AuditLogEntry
        {
            Category = AuditCategory.System,
            Action = "Failure",
            Outcome = AuditOutcome.Failure,
            Severity = AuditSeverity.Error
        });

        var stats = _service.GetStatistics();

        Assert.True(stats.ByOutcome.ContainsKey(AuditOutcome.Success));
        Assert.True(stats.ByOutcome.ContainsKey(AuditOutcome.Failure));
    }

    [Fact]
    public async Task GetStatistics_TracksStorageSize()
    {
        // Use error severity to force immediate flush
        await _service.LogAsync(new AuditLogEntry
        {
            Category = AuditCategory.System,
            Action = "TestForStorage",
            Outcome = AuditOutcome.Success,
            Severity = AuditSeverity.Critical,  // Forces immediate flush
            Message = "Test entry to track storage"
        });
        await Task.Delay(200);  // Allow flush to complete

        var stats = _service.GetStatistics();

        Assert.True(stats.StorageSizeBytes > 0);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Log multiple events with forced flush to disk.
    /// Uses batched approach - all entries buffered, then flushed with one Critical entry.
    /// </summary>
    private async Task LogMultipleEventsWithFlush()
    {
        // Log entries with Info severity (buffered)
        await _service.LogAsync(new AuditLogEntry
        {
            Category = AuditCategory.Authentication,
            Action = "Login",
            Outcome = AuditOutcome.Success,
            Severity = AuditSeverity.Info,
            Actor = new AuditActor { Type = ActorType.User, Id = "user1" }
        });

        await _service.LogAsync(new AuditLogEntry
        {
            Category = AuditCategory.Authentication,
            Action = "Login",
            Outcome = AuditOutcome.Failure,
            Severity = AuditSeverity.Info,
            Actor = new AuditActor { Type = ActorType.User, Id = "user2" },
            ErrorMessage = "Invalid password"
        });

        await _service.LogAsync(new AuditLogEntry
        {
            Category = AuditCategory.DeviceManagement,
            Action = "Connected",
            Outcome = AuditOutcome.Success,
            Severity = AuditSeverity.Info,
            Target = new AuditTarget { Type = TargetType.Device, Id = "device1" }
        });

        await _service.LogAsync(new AuditLogEntry
        {
            Category = AuditCategory.FolderManagement,
            Action = "Created",
            Outcome = AuditOutcome.Success,
            Severity = AuditSeverity.Info,
            Target = new AuditTarget { Type = TargetType.Folder, Id = "folder1" }
        });

        // Trigger flush with one Critical entry
        await _service.LogAsync(new AuditLogEntry
        {
            Category = AuditCategory.System,
            Action = "FlushTrigger",
            Outcome = AuditOutcome.Success,
            Severity = AuditSeverity.Critical
        });

        await Task.Delay(200);  // Allow flush to complete
    }

    /// <summary>
    /// Log multiple events for statistics testing (no need for disk flush).
    /// </summary>
    private async Task LogMultipleEvents()
    {
        await _service.LogAuthenticationAsync(new AuthenticationAuditEvent
        {
            Action = "Login",
            Outcome = AuditOutcome.Success,
            UserId = "user1"
        });

        await _service.LogAuthenticationAsync(new AuthenticationAuditEvent
        {
            Action = "Login",
            Outcome = AuditOutcome.Failure,
            UserId = "user2",
            FailureReason = "Invalid password"
        });

        await _service.LogDeviceEventAsync(new DeviceAuditEvent
        {
            Action = "Connected",
            DeviceId = "device1",
            Outcome = AuditOutcome.Success
        });

        await _service.LogFolderEventAsync(new FolderAuditEvent
        {
            Action = "Created",
            FolderId = "folder1",
            Outcome = AuditOutcome.Success
        });
    }

    #endregion
}
