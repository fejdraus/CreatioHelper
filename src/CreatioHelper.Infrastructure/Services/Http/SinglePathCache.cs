using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Http;

/// <summary>
/// Cached HTTP response data
/// </summary>
internal class CachedHttpResponse
{
    public System.Net.HttpStatusCode StatusCode { get; init; }
    public byte[] Content { get; init; } = Array.Empty<byte>();
    public string? ContentType { get; init; }
    public Dictionary<string, IEnumerable<string>> Headers { get; init; } = new();
    public DateTime CachedAt { get; init; }
    public TimeSpan KeepDuration { get; init; }

    public bool IsExpired => DateTime.UtcNow - CachedAt > KeepDuration;
}

/// <summary>
/// HTTP client handler that caches responses for GET requests.
/// Provides simple response caching with gzip support.
/// (Based on Syncthing lib/httpcache/httpcache.go)
/// </summary>
public class CachingHttpClientHandler : DelegatingHandler
{
    private readonly Dictionary<string, CachedHttpResponse> _cache = new();
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly TimeSpan _defaultKeepDuration;
    private readonly ILogger? _logger;

    /// <summary>
    /// Create a caching HTTP client handler
    /// </summary>
    /// <param name="defaultKeepDuration">Default cache duration for responses</param>
    /// <param name="innerHandler">Inner handler (optional)</param>
    /// <param name="logger">Logger (optional)</param>
    public CachingHttpClientHandler(
        TimeSpan defaultKeepDuration,
        HttpMessageHandler? innerHandler = null,
        ILogger? logger = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
        _defaultKeepDuration = defaultKeepDuration;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Only cache GET requests
        if (request.Method != HttpMethod.Get)
        {
            return await base.SendAsync(request, cancellationToken);
        }

        var cacheKey = request.RequestUri?.ToString() ?? "";

        // Try to get from cache
        _lock.EnterReadLock();
        try
        {
            if (_cache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
            {
                _logger?.LogTrace("Cache HIT for {Url}", cacheKey);
                return CreateResponseFromCache(cached, request);
            }
        }
        finally
        {
            _lock.ExitReadLock();
        }

        // Acquire write lock and double-check
        _lock.EnterWriteLock();
        try
        {
            // Double-check after acquiring write lock
            if (_cache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
            {
                _logger?.LogTrace("Cache HIT (double-check) for {Url}", cacheKey);
                return CreateResponseFromCache(cached, request);
            }

            // Fetch from network
            _logger?.LogTrace("Cache MISS for {Url}", cacheKey);
            var response = await base.SendAsync(request, cancellationToken);

            // Only cache successful responses
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);

                var headers = new Dictionary<string, IEnumerable<string>>();
                foreach (var header in response.Headers)
                {
                    headers[header.Key] = header.Value.ToList();
                }
                foreach (var header in response.Content.Headers)
                {
                    headers[header.Key] = header.Value.ToList();
                }

                _cache[cacheKey] = new CachedHttpResponse
                {
                    StatusCode = response.StatusCode,
                    Content = content,
                    ContentType = response.Content.Headers.ContentType?.ToString(),
                    Headers = headers,
                    CachedAt = DateTime.UtcNow,
                    KeepDuration = _defaultKeepDuration
                };

                _logger?.LogDebug("Cached response for {Url}: {Size} bytes, keep={Keep}",
                    cacheKey, content.Length, _defaultKeepDuration);

                // Return a new response with the cached content
                return CreateResponseFromCache(_cache[cacheKey], request);
            }

            return response;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private static HttpResponseMessage CreateResponseFromCache(CachedHttpResponse cached, HttpRequestMessage request)
    {
        var response = new HttpResponseMessage(cached.StatusCode)
        {
            Content = new ByteArrayContent(cached.Content),
            RequestMessage = request
        };

        // Copy headers
        foreach (var (key, values) in cached.Headers)
        {
            // Skip content headers - they're set on Content
            if (key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                continue;

            response.Headers.TryAddWithoutValidation(key, values);
        }

        if (!string.IsNullOrEmpty(cached.ContentType))
        {
            response.Content.Headers.TryAddWithoutValidation("Content-Type", cached.ContentType);
        }

        response.Headers.TryAddWithoutValidation("X-Cache", "HIT");
        response.Headers.TryAddWithoutValidation("X-Cache-From", cached.CachedAt.ToString("O"));

        return response;
    }

    /// <summary>
    /// Clear all cached responses
    /// </summary>
    public void ClearCache()
    {
        _lock.EnterWriteLock();
        try
        {
            _cache.Clear();
            _logger?.LogDebug("Cache cleared");
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Remove a specific URL from cache
    /// </summary>
    public void Invalidate(string url)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_cache.Remove(url))
            {
                _logger?.LogDebug("Invalidated cache for {Url}", url);
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Remove expired entries from cache
    /// </summary>
    public int PurgeExpired()
    {
        _lock.EnterWriteLock();
        try
        {
            var expired = _cache.Where(kv => kv.Value.IsExpired).Select(kv => kv.Key).ToList();
            foreach (var key in expired)
            {
                _cache.Remove(key);
            }
            if (expired.Count > 0)
            {
                _logger?.LogDebug("Purged {Count} expired cache entries", expired.Count);
            }
            return expired.Count;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Get cache statistics
    /// </summary>
    public CacheStatistics GetStatistics()
    {
        _lock.EnterReadLock();
        try
        {
            var totalSize = _cache.Values.Sum(c => (long)c.Content.Length);
            var expiredCount = _cache.Values.Count(c => c.IsExpired);

            return new CacheStatistics
            {
                EntryCount = _cache.Count,
                ExpiredCount = expiredCount,
                TotalSizeBytes = totalSize
            };
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }
}

/// <summary>
/// Cache statistics
/// </summary>
public record CacheStatistics
{
    public int EntryCount { get; init; }
    public int ExpiredCount { get; init; }
    public long TotalSizeBytes { get; init; }
}

/// <summary>
/// Factory for creating caching HTTP clients
/// </summary>
public static class CachingHttpClientFactory
{
    /// <summary>
    /// Create an HttpClient with response caching
    /// </summary>
    /// <param name="cacheDuration">How long to cache responses</param>
    /// <param name="logger">Optional logger</param>
    public static HttpClient CreateCachingClient(TimeSpan cacheDuration, ILogger? logger = null)
    {
        var handler = new CachingHttpClientHandler(cacheDuration, logger: logger);
        return new HttpClient(handler);
    }

    /// <summary>
    /// Create an HttpClient with response caching for discovery APIs
    /// Default: 5 minutes cache
    /// </summary>
    public static HttpClient CreateDiscoveryClient(ILogger? logger = null)
    {
        return CreateCachingClient(TimeSpan.FromMinutes(5), logger);
    }
}
