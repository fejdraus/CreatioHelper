namespace CreatioHelper.Infrastructure.Services.Sync.Diagnostics;

/// <summary>
/// HTTP request profiling service based on Syncthing's lib/api/api.go
///
/// Provides:
/// - Request timing and duration tracking
/// - Response size tracking
/// - Error rate monitoring
/// - Endpoint statistics
/// - Slow request detection
/// - Request tracing
/// </summary>
public interface IRequestProfiler
{
    /// <summary>
    /// Start profiling a request.
    /// </summary>
    /// <param name="method">HTTP method</param>
    /// <param name="path">Request path</param>
    /// <param name="remoteAddress">Client IP address</param>
    /// <returns>Profile context to complete when request finishes</returns>
    IRequestProfileContext StartRequest(string method, string path, string? remoteAddress = null);

    /// <summary>
    /// Get statistics for a specific endpoint.
    /// </summary>
    EndpointStatistics? GetEndpointStatistics(string method, string path);

    /// <summary>
    /// Get all endpoint statistics.
    /// </summary>
    IReadOnlyDictionary<string, EndpointStatistics> GetAllStatistics();

    /// <summary>
    /// Get recent slow requests.
    /// </summary>
    IReadOnlyList<RequestProfile> GetSlowRequests(int count = 10);

    /// <summary>
    /// Get recent failed requests.
    /// </summary>
    IReadOnlyList<RequestProfile> GetFailedRequests(int count = 10);

    /// <summary>
    /// Get overall profiling summary.
    /// </summary>
    RequestProfilerSummary GetSummary();

    /// <summary>
    /// Reset all statistics.
    /// </summary>
    void Reset();

    /// <summary>
    /// Update configuration.
    /// </summary>
    void UpdateConfiguration(RequestProfilerConfiguration configuration);

    /// <summary>
    /// Check if profiling is enabled.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Enable or disable profiling.
    /// </summary>
    void SetEnabled(bool enabled);
}

/// <summary>
/// Context for an active request profile.
/// </summary>
public interface IRequestProfileContext : IDisposable
{
    /// <summary>
    /// Request ID for tracing.
    /// </summary>
    string RequestId { get; }

    /// <summary>
    /// Add a custom property to the profile.
    /// </summary>
    void AddProperty(string key, object? value);

    /// <summary>
    /// Mark request as completed successfully.
    /// </summary>
    void Complete(int statusCode, long responseSize);

    /// <summary>
    /// Mark request as failed.
    /// </summary>
    void Fail(int statusCode, string? errorMessage = null);

    /// <summary>
    /// Mark a checkpoint in request processing.
    /// </summary>
    void Checkpoint(string name);
}

/// <summary>
/// Configuration for request profiling.
/// </summary>
public class RequestProfilerConfiguration
{
    /// <summary>
    /// Whether profiling is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Threshold in milliseconds for slow request detection.
    /// </summary>
    public int SlowRequestThresholdMs { get; set; } = 1000;

    /// <summary>
    /// Maximum number of recent requests to keep in history.
    /// </summary>
    public int MaxHistorySize { get; set; } = 1000;

    /// <summary>
    /// Maximum number of slow requests to keep.
    /// </summary>
    public int MaxSlowRequests { get; set; } = 100;

    /// <summary>
    /// Maximum number of failed requests to keep.
    /// </summary>
    public int MaxFailedRequests { get; set; } = 100;

    /// <summary>
    /// Paths to exclude from profiling (regex patterns).
    /// </summary>
    public HashSet<string> ExcludedPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        "/health",
        "/metrics",
        "/favicon.ico"
    };

    /// <summary>
    /// Whether to track request body size.
    /// </summary>
    public bool TrackRequestBodySize { get; set; } = true;

    /// <summary>
    /// Whether to record checkpoints.
    /// </summary>
    public bool RecordCheckpoints { get; set; } = true;

    /// <summary>
    /// Statistics retention period.
    /// </summary>
    public TimeSpan StatisticsRetentionPeriod { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>
/// Profile data for a single request.
/// </summary>
public class RequestProfile
{
    /// <summary>
    /// Unique request identifier.
    /// </summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>
    /// HTTP method.
    /// </summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// Request path.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Client IP address.
    /// </summary>
    public string? RemoteAddress { get; set; }

    /// <summary>
    /// Request start time.
    /// </summary>
    public DateTime StartTime { get; set; }

    /// <summary>
    /// Request end time.
    /// </summary>
    public DateTime? EndTime { get; set; }

    /// <summary>
    /// Request duration in milliseconds.
    /// </summary>
    public double DurationMs { get; set; }

    /// <summary>
    /// HTTP status code.
    /// </summary>
    public int StatusCode { get; set; }

    /// <summary>
    /// Response size in bytes.
    /// </summary>
    public long ResponseSize { get; set; }

    /// <summary>
    /// Request body size in bytes.
    /// </summary>
    public long? RequestBodySize { get; set; }

    /// <summary>
    /// Whether the request was successful (2xx).
    /// </summary>
    public bool IsSuccess => StatusCode >= 200 && StatusCode < 300;

    /// <summary>
    /// Whether the request was slow.
    /// </summary>
    public bool IsSlow { get; set; }

    /// <summary>
    /// Error message if failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Checkpoints with timestamps.
    /// </summary>
    public List<RequestCheckpoint> Checkpoints { get; set; } = new();

    /// <summary>
    /// Custom properties.
    /// </summary>
    public Dictionary<string, object?> Properties { get; set; } = new();
}

/// <summary>
/// A checkpoint in request processing.
/// </summary>
public class RequestCheckpoint
{
    public string Name { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public double ElapsedMs { get; set; }
}

/// <summary>
/// Statistics for a specific endpoint.
/// </summary>
public class EndpointStatistics
{
    /// <summary>
    /// Endpoint key (method + path).
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>
    /// HTTP method.
    /// </summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// Request path.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// Total request count.
    /// </summary>
    public long TotalRequests { get; set; }

    /// <summary>
    /// Successful requests (2xx).
    /// </summary>
    public long SuccessfulRequests { get; set; }

    /// <summary>
    /// Failed requests (4xx, 5xx).
    /// </summary>
    public long FailedRequests { get; set; }

    /// <summary>
    /// Slow requests count.
    /// </summary>
    public long SlowRequests { get; set; }

    /// <summary>
    /// Total response bytes.
    /// </summary>
    public long TotalResponseBytes { get; set; }

    /// <summary>
    /// Total request body bytes.
    /// </summary>
    public long TotalRequestBytes { get; set; }

    /// <summary>
    /// Minimum response time in milliseconds.
    /// </summary>
    public double MinDurationMs { get; set; } = double.MaxValue;

    /// <summary>
    /// Maximum response time in milliseconds.
    /// </summary>
    public double MaxDurationMs { get; set; }

    /// <summary>
    /// Total duration for average calculation.
    /// </summary>
    public double TotalDurationMs { get; set; }

    /// <summary>
    /// Average response time in milliseconds.
    /// </summary>
    public double AverageDurationMs => TotalRequests > 0 ? TotalDurationMs / TotalRequests : 0;

    /// <summary>
    /// Success rate percentage.
    /// </summary>
    public double SuccessRate => TotalRequests > 0 ? (double)SuccessfulRequests / TotalRequests * 100 : 0;

    /// <summary>
    /// Requests per second (recent).
    /// </summary>
    public double RequestsPerSecond { get; set; }

    /// <summary>
    /// Last request time.
    /// </summary>
    public DateTime? LastRequestTime { get; set; }

    /// <summary>
    /// First request time.
    /// </summary>
    public DateTime? FirstRequestTime { get; set; }

    /// <summary>
    /// Status code distribution.
    /// </summary>
    public Dictionary<int, long> StatusCodeDistribution { get; set; } = new();
}

/// <summary>
/// Overall profiler summary.
/// </summary>
public class RequestProfilerSummary
{
    /// <summary>
    /// Total requests profiled.
    /// </summary>
    public long TotalRequests { get; set; }

    /// <summary>
    /// Successful requests.
    /// </summary>
    public long SuccessfulRequests { get; set; }

    /// <summary>
    /// Failed requests.
    /// </summary>
    public long FailedRequests { get; set; }

    /// <summary>
    /// Slow requests.
    /// </summary>
    public long SlowRequests { get; set; }

    /// <summary>
    /// Total bytes sent.
    /// </summary>
    public long TotalBytesSent { get; set; }

    /// <summary>
    /// Total bytes received.
    /// </summary>
    public long TotalBytesReceived { get; set; }

    /// <summary>
    /// Average response time.
    /// </summary>
    public double AverageResponseTimeMs { get; set; }

    /// <summary>
    /// Success rate percentage.
    /// </summary>
    public double SuccessRate { get; set; }

    /// <summary>
    /// Requests per second (recent).
    /// </summary>
    public double RequestsPerSecond { get; set; }

    /// <summary>
    /// Number of tracked endpoints.
    /// </summary>
    public int TrackedEndpoints { get; set; }

    /// <summary>
    /// Profiler uptime.
    /// </summary>
    public TimeSpan Uptime { get; set; }

    /// <summary>
    /// Top endpoints by request count.
    /// </summary>
    public List<EndpointStatistics> TopEndpoints { get; set; } = new();

    /// <summary>
    /// Slowest endpoints by average duration.
    /// </summary>
    public List<EndpointStatistics> SlowestEndpoints { get; set; } = new();
}
