using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CreatioHelper.Agent.Middleware;

/// <summary>
/// Configuration options for HTTP response caching.
/// </summary>
public class HttpCacheOptions
{
    /// <summary>
    /// Default cache duration for responses.
    /// </summary>
    public TimeSpan DefaultCacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum size of a single cached response in bytes.
    /// </summary>
    public long MaxCachedResponseSize { get; set; } = 10 * 1024 * 1024; // 10 MB

    /// <summary>
    /// Paths that should be cached. If empty, all GET requests are cached.
    /// Supports glob patterns like "/api/status/*".
    /// </summary>
    public List<string> CacheablePaths { get; set; } = new();

    /// <summary>
    /// Paths that should never be cached.
    /// </summary>
    public List<string> ExcludedPaths { get; set; } = new()
    {
        "/api/events",
        "/api/sync",
        "/api/auth"
    };

    /// <summary>
    /// Whether to enable gzip compression for cached responses.
    /// </summary>
    public bool EnableGzipCompression { get; set; } = true;

    /// <summary>
    /// Minimum size in bytes for responses to be compressed.
    /// </summary>
    public int GzipMinSize { get; set; } = 1024; // 1 KB

    /// <summary>
    /// Whether to include query string in cache key.
    /// </summary>
    public bool IncludeQueryStringInCacheKey { get; set; } = true;

    /// <summary>
    /// Whether to respect Cache-Control headers from the response.
    /// </summary>
    public bool RespectCacheControlHeaders { get; set; } = true;
}

/// <summary>
/// Cached response data structure.
/// </summary>
public class CachedResponse
{
    public int StatusCode { get; init; }
    public Dictionary<string, string> Headers { get; init; } = new();
    public byte[] Body { get; init; } = Array.Empty<byte>();
    public DateTime CachedAt { get; init; } = DateTime.UtcNow;
    public string? ETag { get; init; }
    public bool IsCompressed { get; init; }
    public string? ContentType { get; init; }
}

/// <summary>
/// HTTP response caching middleware with gzip support.
/// </summary>
/// <remarks>
/// Mirrors the functionality of lib/httpcache in Syncthing:
/// - Caches GET responses for configurable duration
/// - Supports gzip compression for cached responses
/// - Handles ETag and conditional requests
/// - Thread-safe concurrent access
///
/// This middleware is designed for caching API responses that don't change
/// frequently, such as configuration endpoints, status endpoints, etc.
/// </remarks>
public class HttpCacheMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IMemoryCache _cache;
    private readonly ILogger<HttpCacheMiddleware> _logger;
    private readonly HttpCacheOptions _options;

    public HttpCacheMiddleware(
        RequestDelegate next,
        IMemoryCache cache,
        ILogger<HttpCacheMiddleware> logger,
        IOptions<HttpCacheOptions>? options = null)
    {
        _next = next;
        _cache = cache;
        _logger = logger;
        _options = options?.Value ?? new HttpCacheOptions();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only cache GET requests
        if (!HttpMethods.IsGet(context.Request.Method))
        {
            await _next(context);
            return;
        }

        // Check if path should be cached
        if (!ShouldCachePath(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var cacheKey = GenerateCacheKey(context.Request);

        // Try to get cached response
        if (_cache.TryGetValue(cacheKey, out CachedResponse? cached) && cached != null)
        {
            // Handle conditional request (If-None-Match)
            var ifNoneMatch = context.Request.Headers.IfNoneMatch.FirstOrDefault();
            if (!string.IsNullOrEmpty(ifNoneMatch) && ifNoneMatch == cached.ETag)
            {
                context.Response.StatusCode = StatusCodes.Status304NotModified;
                context.Response.Headers.ETag = cached.ETag;
                _logger.LogDebug("Cache HIT (304 Not Modified): {Path}", context.Request.Path);
                return;
            }

            // Return cached response
            await ServeCachedResponse(context, cached);
            _logger.LogDebug("Cache HIT: {Path}", context.Request.Path);
            return;
        }

        // Cache miss - capture the response
        _logger.LogDebug("Cache MISS: {Path}", context.Request.Path);
        await CaptureAndCacheResponse(context, cacheKey);
    }

    private bool ShouldCachePath(PathString path)
    {
        var pathString = path.Value ?? string.Empty;

        // Check excluded paths first
        foreach (var excluded in _options.ExcludedPaths)
        {
            if (MatchesPath(pathString, excluded))
                return false;
        }

        // If no cacheable paths specified, cache all
        if (_options.CacheablePaths.Count == 0)
            return true;

        // Check if path matches any cacheable pattern
        foreach (var cacheable in _options.CacheablePaths)
        {
            if (MatchesPath(pathString, cacheable))
                return true;
        }

        return false;
    }

    private static bool MatchesPath(string path, string pattern)
    {
        if (pattern.EndsWith("*"))
        {
            return path.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        }

        return path.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private string GenerateCacheKey(HttpRequest request)
    {
        var keyBuilder = new StringBuilder("http_cache_");
        keyBuilder.Append(request.Path.Value?.ToLowerInvariant() ?? string.Empty);

        if (_options.IncludeQueryStringInCacheKey && request.QueryString.HasValue)
        {
            keyBuilder.Append(request.QueryString.Value);
        }

        return keyBuilder.ToString();
    }

    private async Task ServeCachedResponse(HttpContext context, CachedResponse cached)
    {
        context.Response.StatusCode = cached.StatusCode;

        // Set headers from cache
        foreach (var header in cached.Headers)
        {
            context.Response.Headers[header.Key] = header.Value;
        }

        // Set ETag if available
        if (!string.IsNullOrEmpty(cached.ETag))
        {
            context.Response.Headers.ETag = cached.ETag;
        }

        // Check if client accepts gzip
        var acceptsGzip = context.Request.Headers.AcceptEncoding
            .Any(e => e?.Contains("gzip", StringComparison.OrdinalIgnoreCase) == true);

        byte[] bodyToSend;

        if (cached.IsCompressed && acceptsGzip)
        {
            // Send compressed response
            context.Response.Headers.ContentEncoding = "gzip";
            bodyToSend = cached.Body;
        }
        else if (cached.IsCompressed && !acceptsGzip)
        {
            // Decompress for client that doesn't accept gzip
            bodyToSend = Decompress(cached.Body);
        }
        else
        {
            bodyToSend = cached.Body;
        }

        context.Response.ContentLength = bodyToSend.Length;
        if (!string.IsNullOrEmpty(cached.ContentType))
        {
            context.Response.ContentType = cached.ContentType;
        }

        await context.Response.Body.WriteAsync(bodyToSend);
    }

    private async Task CaptureAndCacheResponse(HttpContext context, string cacheKey)
    {
        var originalBody = context.Response.Body;

        using var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        try
        {
            await _next(context);

            // Only cache successful responses
            if (context.Response.StatusCode is < 200 or >= 300)
            {
                memoryStream.Position = 0;
                await memoryStream.CopyToAsync(originalBody);
                return;
            }

            // Check Cache-Control header
            if (_options.RespectCacheControlHeaders)
            {
                var cacheControl = context.Response.Headers.CacheControl.FirstOrDefault();
                if (!string.IsNullOrEmpty(cacheControl) &&
                    (cacheControl.Contains("no-store") || cacheControl.Contains("no-cache")))
                {
                    memoryStream.Position = 0;
                    await memoryStream.CopyToAsync(originalBody);
                    return;
                }
            }

            memoryStream.Position = 0;
            var body = memoryStream.ToArray();

            // Check size limit
            if (body.Length > _options.MaxCachedResponseSize)
            {
                _logger.LogDebug(
                    "Response too large to cache: {Size} bytes > {MaxSize} bytes",
                    body.Length,
                    _options.MaxCachedResponseSize);
                memoryStream.Position = 0;
                await memoryStream.CopyToAsync(originalBody);
                return;
            }

            // Determine if we should compress
            bool shouldCompress = _options.EnableGzipCompression &&
                                  body.Length >= _options.GzipMinSize &&
                                  IsCompressibleContentType(context.Response.ContentType);

            byte[] cachedBody;
            bool isCompressed;

            if (shouldCompress)
            {
                cachedBody = Compress(body);
                isCompressed = true;
                _logger.LogDebug(
                    "Compressed response from {OriginalSize} to {CompressedSize} bytes",
                    body.Length,
                    cachedBody.Length);
            }
            else
            {
                cachedBody = body;
                isCompressed = false;
            }

            // Generate ETag
            var etag = GenerateETag(body);

            // Extract cacheable headers
            var headers = new Dictionary<string, string>();
            foreach (var header in context.Response.Headers)
            {
                if (IsCacheableHeader(header.Key))
                {
                    headers[header.Key] = header.Value.ToString();
                }
            }

            // Create cached response
            var cached = new CachedResponse
            {
                StatusCode = context.Response.StatusCode,
                Headers = headers,
                Body = cachedBody,
                CachedAt = DateTime.UtcNow,
                ETag = etag,
                IsCompressed = isCompressed,
                ContentType = context.Response.ContentType
            };

            // Determine cache duration
            var cacheDuration = GetCacheDuration(context.Response);

            // Store in cache
            _cache.Set(cacheKey, cached, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = cacheDuration,
                Size = cachedBody.Length
            });

            _logger.LogDebug(
                "Cached response for {Path}, expires in {Duration}",
                context.Request.Path,
                cacheDuration);

            // Set ETag on the response so client can use it for conditional requests
            context.Response.Headers.ETag = etag;

            // Write original (uncompressed) response to client
            memoryStream.Position = 0;
            await memoryStream.CopyToAsync(originalBody);
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private TimeSpan GetCacheDuration(HttpResponse response)
    {
        if (_options.RespectCacheControlHeaders)
        {
            var cacheControl = response.Headers.CacheControl.FirstOrDefault();
            if (!string.IsNullOrEmpty(cacheControl))
            {
                // Parse max-age
                var maxAgeIndex = cacheControl.IndexOf("max-age=", StringComparison.OrdinalIgnoreCase);
                if (maxAgeIndex >= 0)
                {
                    var valueStart = maxAgeIndex + 8;
                    var valueEnd = cacheControl.IndexOf(',', valueStart);
                    if (valueEnd < 0) valueEnd = cacheControl.Length;

                    if (int.TryParse(cacheControl[valueStart..valueEnd], out var maxAge))
                    {
                        return TimeSpan.FromSeconds(maxAge);
                    }
                }
            }
        }

        return _options.DefaultCacheDuration;
    }

    private static bool IsCacheableHeader(string headerName)
    {
        // Headers we want to preserve in cache
        return headerName switch
        {
            "Content-Type" => true,
            "Content-Language" => true,
            "Last-Modified" => true,
            "Vary" => true,
            _ => false
        };
    }

    private static bool IsCompressibleContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
            return false;

        return contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("text/", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("application/xml", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("application/javascript", StringComparison.OrdinalIgnoreCase);
    }

    private static string GenerateETag(byte[] content)
    {
        var hash = SHA256.HashData(content);
        return $"\"{Convert.ToBase64String(hash[..8])}\"";
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] compressedData)
    {
        using var input = new MemoryStream(compressedData);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}

/// <summary>
/// Extension methods for configuring HTTP cache middleware.
/// </summary>
public static class HttpCacheMiddlewareExtensions
{
    /// <summary>
    /// Adds HTTP response caching middleware to the application pipeline.
    /// </summary>
    public static IApplicationBuilder UseHttpCache(this IApplicationBuilder app)
    {
        return app.UseMiddleware<HttpCacheMiddleware>();
    }

    /// <summary>
    /// Adds HTTP cache services to the dependency injection container.
    /// </summary>
    public static IServiceCollection AddHttpCache(
        this IServiceCollection services,
        Action<HttpCacheOptions>? configure = null)
    {
        if (configure != null)
        {
            services.Configure(configure);
        }
        else
        {
            services.Configure<HttpCacheOptions>(_ => { });
        }

        return services;
    }
}
