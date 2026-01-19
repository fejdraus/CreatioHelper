using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Diagnostics;

/// <summary>
/// Implementation of request profiling service.
/// Based on Syncthing's lib/api/api.go profiling patterns.
/// </summary>
public class RequestProfiler : IRequestProfiler
{
    private readonly ILogger<RequestProfiler>? _logger;
    private readonly ConcurrentDictionary<string, EndpointStatistics> _endpointStats = new();
    private readonly ConcurrentQueue<RequestProfile> _slowRequests = new();
    private readonly ConcurrentQueue<RequestProfile> _failedRequests = new();
    private readonly ConcurrentQueue<RequestProfile> _recentRequests = new();
    private readonly object _statsLock = new();
    private readonly DateTime _startTime;

    private RequestProfilerConfiguration _config;
    private bool _enabled;
    private long _totalRequests;
    private long _successfulRequests;
    private long _failedRequestCount;
    private long _slowRequestCount;
    private long _totalBytesSent;
    private long _totalBytesReceived;
    private double _totalDurationMs;

    public RequestProfiler(
        ILogger<RequestProfiler>? logger = null,
        RequestProfilerConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new RequestProfilerConfiguration();
        _enabled = _config.Enabled;
        _startTime = DateTime.UtcNow;
    }

    public bool IsEnabled => _enabled;

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        _logger?.LogInformation("Request profiling {Status}", enabled ? "enabled" : "disabled");
    }

    public IRequestProfileContext StartRequest(string method, string path, string? remoteAddress = null)
    {
        if (!_enabled || IsExcludedPath(path))
        {
            return new NoOpProfileContext();
        }

        var profile = new RequestProfile
        {
            RequestId = GenerateRequestId(),
            Method = method.ToUpperInvariant(),
            Path = NormalizePath(path),
            RemoteAddress = remoteAddress,
            StartTime = DateTime.UtcNow
        };

        return new ProfileContext(this, profile, _config.RecordCheckpoints);
    }

    public EndpointStatistics? GetEndpointStatistics(string method, string path)
    {
        var key = GetEndpointKey(method, path);
        return _endpointStats.TryGetValue(key, out var stats) ? stats : null;
    }

    public IReadOnlyDictionary<string, EndpointStatistics> GetAllStatistics()
    {
        return _endpointStats;
    }

    public IReadOnlyList<RequestProfile> GetSlowRequests(int count = 10)
    {
        return _slowRequests.TakeLast(count).Reverse().ToList();
    }

    public IReadOnlyList<RequestProfile> GetFailedRequests(int count = 10)
    {
        return _failedRequests.TakeLast(count).Reverse().ToList();
    }

    public RequestProfilerSummary GetSummary()
    {
        var uptime = DateTime.UtcNow - _startTime;
        var requestsPerSecond = uptime.TotalSeconds > 0
            ? Interlocked.Read(ref _totalRequests) / uptime.TotalSeconds
            : 0;

        var totalRequests = Interlocked.Read(ref _totalRequests);
        var successfulRequests = Interlocked.Read(ref _successfulRequests);

        var allStats = _endpointStats.Values.ToList();
        var topEndpoints = allStats
            .OrderByDescending(s => s.TotalRequests)
            .Take(10)
            .ToList();

        var slowestEndpoints = allStats
            .Where(s => s.TotalRequests > 0)
            .OrderByDescending(s => s.AverageDurationMs)
            .Take(10)
            .ToList();

        return new RequestProfilerSummary
        {
            TotalRequests = totalRequests,
            SuccessfulRequests = successfulRequests,
            FailedRequests = Interlocked.Read(ref _failedRequestCount),
            SlowRequests = Interlocked.Read(ref _slowRequestCount),
            TotalBytesSent = Interlocked.Read(ref _totalBytesSent),
            TotalBytesReceived = Interlocked.Read(ref _totalBytesReceived),
            AverageResponseTimeMs = totalRequests > 0 ? _totalDurationMs / totalRequests : 0,
            SuccessRate = totalRequests > 0 ? (double)successfulRequests / totalRequests * 100 : 0,
            RequestsPerSecond = requestsPerSecond,
            TrackedEndpoints = _endpointStats.Count,
            Uptime = uptime,
            TopEndpoints = topEndpoints,
            SlowestEndpoints = slowestEndpoints
        };
    }

    public void Reset()
    {
        _endpointStats.Clear();

        while (_slowRequests.TryDequeue(out _)) { }
        while (_failedRequests.TryDequeue(out _)) { }
        while (_recentRequests.TryDequeue(out _)) { }

        Interlocked.Exchange(ref _totalRequests, 0);
        Interlocked.Exchange(ref _successfulRequests, 0);
        Interlocked.Exchange(ref _failedRequestCount, 0);
        Interlocked.Exchange(ref _slowRequestCount, 0);
        Interlocked.Exchange(ref _totalBytesSent, 0);
        Interlocked.Exchange(ref _totalBytesReceived, 0);

        lock (_statsLock)
        {
            _totalDurationMs = 0;
        }

        _logger?.LogInformation("Request profiler statistics reset");
    }

    public void UpdateConfiguration(RequestProfilerConfiguration configuration)
    {
        _config = configuration;
        _enabled = configuration.Enabled;
        _logger?.LogInformation("Request profiler configuration updated");
    }

    internal void RecordProfile(RequestProfile profile)
    {
        // Update totals
        Interlocked.Increment(ref _totalRequests);

        if (profile.IsSuccess)
        {
            Interlocked.Increment(ref _successfulRequests);
        }
        else
        {
            Interlocked.Increment(ref _failedRequestCount);
        }

        if (profile.IsSlow)
        {
            Interlocked.Increment(ref _slowRequestCount);
        }

        Interlocked.Add(ref _totalBytesSent, profile.ResponseSize);
        if (profile.RequestBodySize.HasValue)
        {
            Interlocked.Add(ref _totalBytesReceived, profile.RequestBodySize.Value);
        }

        lock (_statsLock)
        {
            _totalDurationMs += profile.DurationMs;
        }

        // Update endpoint statistics
        var key = GetEndpointKey(profile.Method, profile.Path);
        _endpointStats.AddOrUpdate(
            key,
            _ => CreateEndpointStats(profile),
            (_, stats) => UpdateEndpointStats(stats, profile));

        // Track slow requests
        if (profile.IsSlow)
        {
            _slowRequests.Enqueue(profile);
            TrimQueue(_slowRequests, _config.MaxSlowRequests);
        }

        // Track failed requests
        if (!profile.IsSuccess)
        {
            _failedRequests.Enqueue(profile);
            TrimQueue(_failedRequests, _config.MaxFailedRequests);
        }

        // Add to recent requests
        _recentRequests.Enqueue(profile);
        TrimQueue(_recentRequests, _config.MaxHistorySize);

        // Log slow or failed requests
        if (profile.IsSlow)
        {
            _logger?.LogWarning(
                "Slow request: {Method} {Path} took {Duration}ms (threshold: {Threshold}ms)",
                profile.Method, profile.Path, profile.DurationMs, _config.SlowRequestThresholdMs);
        }

        if (!profile.IsSuccess && profile.StatusCode >= 500)
        {
            _logger?.LogError(
                "Failed request: {Method} {Path} returned {StatusCode}: {Error}",
                profile.Method, profile.Path, profile.StatusCode, profile.ErrorMessage);
        }
    }

    private EndpointStatistics CreateEndpointStats(RequestProfile profile)
    {
        var stats = new EndpointStatistics
        {
            Endpoint = GetEndpointKey(profile.Method, profile.Path),
            Method = profile.Method,
            Path = profile.Path,
            TotalRequests = 1,
            SuccessfulRequests = profile.IsSuccess ? 1 : 0,
            FailedRequests = profile.IsSuccess ? 0 : 1,
            SlowRequests = profile.IsSlow ? 1 : 0,
            TotalResponseBytes = profile.ResponseSize,
            TotalRequestBytes = profile.RequestBodySize ?? 0,
            MinDurationMs = profile.DurationMs,
            MaxDurationMs = profile.DurationMs,
            TotalDurationMs = profile.DurationMs,
            FirstRequestTime = profile.StartTime,
            LastRequestTime = profile.EndTime
        };

        stats.StatusCodeDistribution[profile.StatusCode] = 1;

        return stats;
    }

    private EndpointStatistics UpdateEndpointStats(EndpointStatistics stats, RequestProfile profile)
    {
        stats.TotalRequests++;

        if (profile.IsSuccess)
            stats.SuccessfulRequests++;
        else
            stats.FailedRequests++;

        if (profile.IsSlow)
            stats.SlowRequests++;

        stats.TotalResponseBytes += profile.ResponseSize;
        stats.TotalRequestBytes += profile.RequestBodySize ?? 0;
        stats.TotalDurationMs += profile.DurationMs;

        if (profile.DurationMs < stats.MinDurationMs)
            stats.MinDurationMs = profile.DurationMs;

        if (profile.DurationMs > stats.MaxDurationMs)
            stats.MaxDurationMs = profile.DurationMs;

        stats.LastRequestTime = profile.EndTime;

        if (!stats.StatusCodeDistribution.ContainsKey(profile.StatusCode))
            stats.StatusCodeDistribution[profile.StatusCode] = 0;
        stats.StatusCodeDistribution[profile.StatusCode]++;

        // Calculate requests per second based on time window
        if (stats.FirstRequestTime.HasValue && stats.LastRequestTime.HasValue)
        {
            var duration = (stats.LastRequestTime.Value - stats.FirstRequestTime.Value).TotalSeconds;
            stats.RequestsPerSecond = duration > 0 ? stats.TotalRequests / duration : stats.TotalRequests;
        }

        return stats;
    }

    private bool IsExcludedPath(string path)
    {
        foreach (var pattern in _config.ExcludedPaths)
        {
            if (path.Equals(pattern, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string GetEndpointKey(string method, string path)
    {
        return $"{method.ToUpperInvariant()} {NormalizePath(path)}";
    }

    private static string NormalizePath(string path)
    {
        // Normalize path parameters (e.g., /api/users/123 -> /api/users/{id})
        // This groups similar endpoints together
        path = Regex.Replace(path, @"/\d+(?=/|$)", "/{id}");
        path = Regex.Replace(path, @"/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "/{guid}", RegexOptions.IgnoreCase);
        return path;
    }

    private static string GenerateRequestId()
    {
        return $"req-{Guid.NewGuid():N}"[..16];
    }

    private static void TrimQueue<T>(ConcurrentQueue<T> queue, int maxSize)
    {
        while (queue.Count > maxSize)
        {
            queue.TryDequeue(out _);
        }
    }

    /// <summary>
    /// Profile context implementation.
    /// </summary>
    private class ProfileContext : IRequestProfileContext
    {
        private readonly RequestProfiler _profiler;
        private readonly RequestProfile _profile;
        private readonly Stopwatch _stopwatch;
        private readonly bool _recordCheckpoints;
        private bool _completed;

        public ProfileContext(RequestProfiler profiler, RequestProfile profile, bool recordCheckpoints)
        {
            _profiler = profiler;
            _profile = profile;
            _recordCheckpoints = recordCheckpoints;
            _stopwatch = Stopwatch.StartNew();
        }

        public string RequestId => _profile.RequestId;

        public void AddProperty(string key, object? value)
        {
            _profile.Properties[key] = value;
        }

        public void Complete(int statusCode, long responseSize)
        {
            if (_completed) return;
            _completed = true;

            _stopwatch.Stop();
            _profile.EndTime = DateTime.UtcNow;
            _profile.DurationMs = _stopwatch.Elapsed.TotalMilliseconds;
            _profile.StatusCode = statusCode;
            _profile.ResponseSize = responseSize;
            _profile.IsSlow = _profile.DurationMs >= _profiler._config.SlowRequestThresholdMs;

            _profiler.RecordProfile(_profile);
        }

        public void Fail(int statusCode, string? errorMessage = null)
        {
            if (_completed) return;
            _completed = true;

            _stopwatch.Stop();
            _profile.EndTime = DateTime.UtcNow;
            _profile.DurationMs = _stopwatch.Elapsed.TotalMilliseconds;
            _profile.StatusCode = statusCode;
            _profile.ErrorMessage = errorMessage;
            _profile.IsSlow = _profile.DurationMs >= _profiler._config.SlowRequestThresholdMs;

            _profiler.RecordProfile(_profile);
        }

        public void Checkpoint(string name)
        {
            if (!_recordCheckpoints) return;

            _profile.Checkpoints.Add(new RequestCheckpoint
            {
                Name = name,
                Timestamp = DateTime.UtcNow,
                ElapsedMs = _stopwatch.Elapsed.TotalMilliseconds
            });
        }

        public void Dispose()
        {
            if (!_completed)
            {
                // If not explicitly completed, assume success with unknown status
                Complete(200, 0);
            }
        }
    }

    /// <summary>
    /// No-op profile context for excluded/disabled requests.
    /// </summary>
    private class NoOpProfileContext : IRequestProfileContext
    {
        public string RequestId => "noop";
        public void AddProperty(string key, object? value) { }
        public void Complete(int statusCode, long responseSize) { }
        public void Fail(int statusCode, string? errorMessage = null) { }
        public void Checkpoint(string name) { }
        public void Dispose() { }
    }
}
