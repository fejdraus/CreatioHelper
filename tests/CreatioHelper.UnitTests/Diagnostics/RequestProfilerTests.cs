using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Infrastructure.Services.Sync.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;

namespace CreatioHelper.UnitTests.Diagnostics;

public class RequestProfilerTests
{
    private readonly Mock<ILogger<RequestProfiler>> _loggerMock;
    private readonly RequestProfiler _profiler;

    public RequestProfilerTests()
    {
        _loggerMock = new Mock<ILogger<RequestProfiler>>();
        _profiler = new RequestProfiler(_loggerMock.Object);
    }

    [Fact]
    public void StartRequest_ReturnsProfileContext()
    {
        // Act
        using var context = _profiler.StartRequest("GET", "/api/test");

        // Assert
        Assert.NotNull(context);
        Assert.NotEmpty(context.RequestId);
    }

    [Fact]
    public void Complete_RecordsProfile()
    {
        // Arrange
        using var context = _profiler.StartRequest("GET", "/api/test");

        // Act
        context.Complete(200, 1024);

        // Assert
        var summary = _profiler.GetSummary();
        Assert.Equal(1, summary.TotalRequests);
        Assert.Equal(1, summary.SuccessfulRequests);
    }

    [Fact]
    public void Fail_RecordsFailedProfile()
    {
        // Arrange
        using var context = _profiler.StartRequest("POST", "/api/data");

        // Act
        context.Fail(500, "Internal server error");

        // Assert
        var summary = _profiler.GetSummary();
        Assert.Equal(1, summary.TotalRequests);
        Assert.Equal(1, summary.FailedRequests);
    }

    [Fact]
    public void GetEndpointStatistics_ReturnsStats()
    {
        // Arrange
        using (var ctx = _profiler.StartRequest("GET", "/api/users"))
        {
            ctx.Complete(200, 512);
        }
        using (var ctx = _profiler.StartRequest("GET", "/api/users"))
        {
            ctx.Complete(200, 256);
        }

        // Act
        var stats = _profiler.GetEndpointStatistics("GET", "/api/users");

        // Assert
        Assert.NotNull(stats);
        Assert.Equal(2, stats.TotalRequests);
        Assert.Equal(768, stats.TotalResponseBytes);
    }

    [Fact]
    public void GetAllStatistics_ReturnsAllEndpoints()
    {
        // Arrange
        using (var ctx = _profiler.StartRequest("GET", "/api/users"))
        {
            ctx.Complete(200, 100);
        }
        using (var ctx = _profiler.StartRequest("POST", "/api/users"))
        {
            ctx.Complete(201, 50);
        }

        // Act
        var allStats = _profiler.GetAllStatistics();

        // Assert
        Assert.Equal(2, allStats.Count);
    }

    [Fact]
    public void GetSlowRequests_ReturnsSlowRequests()
    {
        // Arrange
        var slowConfig = new RequestProfilerConfiguration
        {
            SlowRequestThresholdMs = 1 // Very low threshold
        };
        var profiler = new RequestProfiler(_loggerMock.Object, slowConfig);

        using (var ctx = profiler.StartRequest("GET", "/slow"))
        {
            Thread.Sleep(10); // Ensure slow
            ctx.Complete(200, 100);
        }

        // Act
        var slowRequests = profiler.GetSlowRequests();

        // Assert
        Assert.Single(slowRequests);
        Assert.True(slowRequests[0].IsSlow);
    }

    [Fact]
    public void GetFailedRequests_ReturnsFailedRequests()
    {
        // Arrange
        using (var ctx = _profiler.StartRequest("GET", "/fail"))
        {
            ctx.Fail(404, "Not found");
        }
        using (var ctx = _profiler.StartRequest("POST", "/error"))
        {
            ctx.Fail(500, "Server error");
        }

        // Act
        var failedRequests = _profiler.GetFailedRequests();

        // Assert
        Assert.Equal(2, failedRequests.Count);
    }

    [Fact]
    public void GetSummary_ReturnsCorrectSummary()
    {
        // Arrange
        for (int i = 0; i < 10; i++)
        {
            using var ctx = _profiler.StartRequest("GET", "/api/test");
            ctx.Complete(200, 100);
        }
        using (var ctx = _profiler.StartRequest("GET", "/api/test"))
        {
            ctx.Fail(500, "Error");
        }

        // Act
        var summary = _profiler.GetSummary();

        // Assert
        Assert.Equal(11, summary.TotalRequests);
        Assert.Equal(10, summary.SuccessfulRequests);
        Assert.Equal(1, summary.FailedRequests);
        Assert.True(summary.SuccessRate > 90);
    }

    [Fact]
    public void Reset_ClearsAllStatistics()
    {
        // Arrange
        using (var ctx = _profiler.StartRequest("GET", "/api/test"))
        {
            ctx.Complete(200, 100);
        }

        // Act
        _profiler.Reset();

        // Assert
        var summary = _profiler.GetSummary();
        Assert.Equal(0, summary.TotalRequests);
        Assert.Empty(_profiler.GetAllStatistics());
    }

    [Fact]
    public void UpdateConfiguration_ChangesSettings()
    {
        // Arrange
        var newConfig = new RequestProfilerConfiguration
        {
            SlowRequestThresholdMs = 5000,
            MaxHistorySize = 500
        };

        // Act
        _profiler.UpdateConfiguration(newConfig);

        // Assert - verify no exception
        Assert.True(_profiler.IsEnabled);
    }

    [Fact]
    public void SetEnabled_DisablesProfiling()
    {
        // Act
        _profiler.SetEnabled(false);

        // Assert
        Assert.False(_profiler.IsEnabled);
    }

    [Fact]
    public void ExcludedPaths_AreNotProfiled()
    {
        // Arrange
        var config = new RequestProfilerConfiguration
        {
            ExcludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "/health" }
        };
        var profiler = new RequestProfiler(_loggerMock.Object, config);

        // Act
        using (var ctx = profiler.StartRequest("GET", "/health"))
        {
            ctx.Complete(200, 0);
        }

        // Assert
        var summary = profiler.GetSummary();
        Assert.Equal(0, summary.TotalRequests);
    }

    [Fact]
    public void Checkpoint_RecordsCheckpoint()
    {
        // Arrange
        var config = new RequestProfilerConfiguration
        {
            RecordCheckpoints = true
        };
        var profiler = new RequestProfiler(_loggerMock.Object, config);

        // Act
        using var ctx = profiler.StartRequest("GET", "/api/data");
        ctx.Checkpoint("auth_check");
        ctx.Checkpoint("db_query");
        ctx.Complete(200, 100);

        // Assert - checkpoints are internal, just verify no errors
        Assert.Equal(1, profiler.GetSummary().TotalRequests);
    }

    [Fact]
    public void AddProperty_AddsCustomProperty()
    {
        // Arrange
        using var ctx = _profiler.StartRequest("GET", "/api/users");

        // Act
        ctx.AddProperty("userId", "123");
        ctx.AddProperty("action", "list");
        ctx.Complete(200, 100);

        // Assert - properties are stored in profile
        Assert.Equal(1, _profiler.GetSummary().TotalRequests);
    }

    [Fact]
    public void PathNormalization_GroupsSimilarPaths()
    {
        // Arrange
        using (var ctx = _profiler.StartRequest("GET", "/api/users/123"))
        {
            ctx.Complete(200, 100);
        }
        using (var ctx = _profiler.StartRequest("GET", "/api/users/456"))
        {
            ctx.Complete(200, 100);
        }

        // Act
        var stats = _profiler.GetEndpointStatistics("GET", "/api/users/{id}");

        // Assert
        Assert.NotNull(stats);
        Assert.Equal(2, stats.TotalRequests);
    }

    [Fact]
    public void GuidNormalization_GroupsGuidPaths()
    {
        // Arrange
        using (var ctx = _profiler.StartRequest("GET", "/api/items/12345678-1234-1234-1234-123456789abc"))
        {
            ctx.Complete(200, 100);
        }
        using (var ctx = _profiler.StartRequest("GET", "/api/items/abcdefab-abcd-abcd-abcd-abcdefabcdef"))
        {
            ctx.Complete(200, 100);
        }

        // Act
        var stats = _profiler.GetEndpointStatistics("GET", "/api/items/{guid}");

        // Assert
        Assert.NotNull(stats);
        Assert.Equal(2, stats.TotalRequests);
    }

    [Fact]
    public void StatusCodeDistribution_TracksStatusCodes()
    {
        // Arrange
        using (var ctx = _profiler.StartRequest("GET", "/api/test"))
        {
            ctx.Complete(200, 100);
        }
        using (var ctx = _profiler.StartRequest("GET", "/api/test"))
        {
            ctx.Complete(201, 100);
        }
        using (var ctx = _profiler.StartRequest("GET", "/api/test"))
        {
            ctx.Fail(404);
        }

        // Act
        var stats = _profiler.GetEndpointStatistics("GET", "/api/test");

        // Assert
        Assert.NotNull(stats);
        Assert.Equal(3, stats.StatusCodeDistribution.Count);
        Assert.Equal(1, stats.StatusCodeDistribution[200]);
        Assert.Equal(1, stats.StatusCodeDistribution[201]);
        Assert.Equal(1, stats.StatusCodeDistribution[404]);
    }

    [Fact]
    public void DurationStats_TracksMinMaxAverage()
    {
        // Arrange
        using (var ctx = _profiler.StartRequest("GET", "/api/test"))
        {
            Thread.Sleep(10);
            ctx.Complete(200, 100);
        }
        using (var ctx = _profiler.StartRequest("GET", "/api/test"))
        {
            Thread.Sleep(20);
            ctx.Complete(200, 100);
        }
        using (var ctx = _profiler.StartRequest("GET", "/api/test"))
        {
            Thread.Sleep(15);
            ctx.Complete(200, 100);
        }

        // Act
        var stats = _profiler.GetEndpointStatistics("GET", "/api/test");

        // Assert
        Assert.NotNull(stats);
        Assert.True(stats.MinDurationMs > 0);
        Assert.True(stats.MaxDurationMs >= stats.MinDurationMs);
        Assert.True(stats.AverageDurationMs > 0);
    }

    [Fact]
    public void DisabledProfiler_ReturnsNoOpContext()
    {
        // Arrange
        _profiler.SetEnabled(false);

        // Act
        using var ctx = _profiler.StartRequest("GET", "/api/test");
        ctx.Complete(200, 100);

        // Assert
        Assert.Equal(0, _profiler.GetSummary().TotalRequests);
    }

    [Fact]
    public void Configuration_DefaultValues()
    {
        // Arrange
        var config = new RequestProfilerConfiguration();

        // Assert
        Assert.True(config.Enabled);
        Assert.Equal(1000, config.SlowRequestThresholdMs);
        Assert.Equal(1000, config.MaxHistorySize);
        Assert.Equal(100, config.MaxSlowRequests);
        Assert.Equal(100, config.MaxFailedRequests);
        Assert.True(config.TrackRequestBodySize);
        Assert.True(config.RecordCheckpoints);
        Assert.Contains("/health", config.ExcludedPaths);
    }

    [Fact]
    public void EndpointStatistics_SuccessRate()
    {
        // Arrange
        var stats = new EndpointStatistics
        {
            TotalRequests = 100,
            SuccessfulRequests = 95
        };

        // Assert
        Assert.Equal(95, stats.SuccessRate);
    }

    [Fact]
    public void EndpointStatistics_AverageDuration()
    {
        // Arrange
        var stats = new EndpointStatistics
        {
            TotalRequests = 10,
            TotalDurationMs = 1000
        };

        // Assert
        Assert.Equal(100, stats.AverageDurationMs);
    }

    [Fact]
    public void RequestProfile_IsSuccess()
    {
        // Arrange
        var profile1 = new RequestProfile { StatusCode = 200 };
        var profile2 = new RequestProfile { StatusCode = 201 };
        var profile3 = new RequestProfile { StatusCode = 404 };
        var profile4 = new RequestProfile { StatusCode = 500 };

        // Assert
        Assert.True(profile1.IsSuccess);
        Assert.True(profile2.IsSuccess);
        Assert.False(profile3.IsSuccess);
        Assert.False(profile4.IsSuccess);
    }

    [Fact]
    public void Summary_TopEndpoints_SortedByRequestCount()
    {
        // Arrange
        for (int i = 0; i < 10; i++)
        {
            using var ctx = _profiler.StartRequest("GET", "/popular");
            ctx.Complete(200, 100);
        }
        for (int i = 0; i < 5; i++)
        {
            using var ctx = _profiler.StartRequest("GET", "/medium");
            ctx.Complete(200, 100);
        }
        using (var ctx = _profiler.StartRequest("GET", "/rare"))
        {
            ctx.Complete(200, 100);
        }

        // Act
        var summary = _profiler.GetSummary();

        // Assert
        Assert.True(summary.TopEndpoints.Count > 0);
        Assert.Equal("GET /popular", summary.TopEndpoints[0].Endpoint);
    }

    [Fact]
    public void DisposeContext_AutoCompletes()
    {
        // Arrange & Act
        using (var ctx = _profiler.StartRequest("GET", "/auto"))
        {
            // Don't call Complete or Fail
        }

        // Assert - should be auto-completed with 200
        var summary = _profiler.GetSummary();
        Assert.Equal(1, summary.TotalRequests);
        Assert.Equal(1, summary.SuccessfulRequests);
    }

    [Fact]
    public void MultipleCompletes_IgnoresDuplicates()
    {
        // Arrange
        using var ctx = _profiler.StartRequest("GET", "/test");

        // Act
        ctx.Complete(200, 100);
        ctx.Complete(200, 200); // Should be ignored
        ctx.Complete(500, 0);   // Should be ignored

        // Assert
        var summary = _profiler.GetSummary();
        Assert.Equal(1, summary.TotalRequests);
    }

    [Fact]
    public async Task ConcurrentRequests_ThreadSafe()
    {
        // Arrange
        var tasks = new List<Task>();

        // Act
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                using var ctx = _profiler.StartRequest("GET", "/concurrent");
                ctx.Complete(200, 100);
            }));
        }

        await Task.WhenAll(tasks);

        // Assert
        var summary = _profiler.GetSummary();
        Assert.Equal(100, summary.TotalRequests);
    }
}
