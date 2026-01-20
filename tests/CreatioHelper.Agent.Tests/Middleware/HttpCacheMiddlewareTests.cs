using CreatioHelper.Agent.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using System.IO.Compression;
using System.Text;
using Xunit;

namespace CreatioHelper.Agent.Tests.Middleware;

public class HttpCacheMiddlewareTests
{
    private readonly Mock<ILogger<HttpCacheMiddleware>> _loggerMock;
    private readonly IMemoryCache _cache;

    public HttpCacheMiddlewareTests()
    {
        _loggerMock = new Mock<ILogger<HttpCacheMiddleware>>();
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    [Fact]
    public async Task InvokeAsync_CachesGetRequestResponse()
    {
        // Arrange
        var responseBody = """{"status":"ok"}""";
        var middleware = CreateMiddleware(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(responseBody);
        });

        var context1 = CreateHttpContext("/api/status", "GET");
        var context2 = CreateHttpContext("/api/status", "GET");

        // Act - First request
        await middleware.InvokeAsync(context1);
        var body1 = GetResponseBody(context1);

        // Act - Second request (should be from cache)
        await middleware.InvokeAsync(context2);
        var body2 = GetResponseBody(context2);

        // Assert
        Assert.Equal(responseBody, body1);
        Assert.Equal(responseBody, body2);
    }

    [Fact]
    public async Task InvokeAsync_DoesNotCachePostRequests()
    {
        // Arrange
        var callCount = 0;
        var middleware = CreateMiddleware(async context =>
        {
            callCount++;
            await context.Response.WriteAsync($"call-{callCount}");
        });

        var context1 = CreateHttpContext("/api/data", "POST");
        var context2 = CreateHttpContext("/api/data", "POST");

        // Act
        await middleware.InvokeAsync(context1);
        await middleware.InvokeAsync(context2);

        // Assert - Both requests should hit the handler
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task InvokeAsync_RespectsExcludedPaths()
    {
        // Arrange
        var options = Options.Create(new HttpCacheOptions
        {
            ExcludedPaths = new List<string> { "/api/events" }
        });

        var callCount = 0;
        var middleware = CreateMiddleware(async context =>
        {
            callCount++;
            await context.Response.WriteAsync($"call-{callCount}");
        }, options);

        var context1 = CreateHttpContext("/api/events", "GET");
        var context2 = CreateHttpContext("/api/events", "GET");

        // Act
        await middleware.InvokeAsync(context1);
        await middleware.InvokeAsync(context2);

        // Assert - Both requests should hit the handler (path is excluded)
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task InvokeAsync_CachesMatchingPathPatterns()
    {
        // Arrange
        var options = Options.Create(new HttpCacheOptions
        {
            CacheablePaths = new List<string> { "/api/status/*" }
        });

        var callCount = 0;
        var middleware = CreateMiddleware(async context =>
        {
            callCount++;
            await context.Response.WriteAsync($"call-{callCount}");
        }, options);

        var context1 = CreateHttpContext("/api/status/health", "GET");
        var context2 = CreateHttpContext("/api/status/health", "GET");
        var context3 = CreateHttpContext("/api/other", "GET");

        // Act
        await middleware.InvokeAsync(context1);
        await middleware.InvokeAsync(context2); // Should be cached
        await middleware.InvokeAsync(context3); // Should not be cached (different path)

        // Assert
        Assert.Equal(2, callCount); // First request + /api/other (status/health was cached)
    }

    [Fact]
    public async Task InvokeAsync_Returns304_ForConditionalRequest()
    {
        // Arrange
        var responseBody = """{"status":"ok"}""";
        var middleware = CreateMiddleware(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(responseBody);
        });

        // First request to populate cache
        var context1 = CreateHttpContext("/api/status", "GET");
        await middleware.InvokeAsync(context1);

        var etag = context1.Response.Headers.ETag.FirstOrDefault();

        // Second request with If-None-Match
        var context2 = CreateHttpContext("/api/status", "GET");
        context2.Request.Headers.IfNoneMatch = etag;
        await middleware.InvokeAsync(context2);

        // Assert
        Assert.NotNull(etag);
        Assert.Equal(StatusCodes.Status304NotModified, context2.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_IncludesQueryStringInCacheKey()
    {
        // Arrange
        var options = Options.Create(new HttpCacheOptions
        {
            IncludeQueryStringInCacheKey = true
        });

        var callCount = 0;
        var middleware = CreateMiddleware(async context =>
        {
            callCount++;
            await context.Response.WriteAsync($"call-{callCount}");
        }, options);

        var context1 = CreateHttpContext("/api/status?v=1", "GET");
        var context2 = CreateHttpContext("/api/status?v=2", "GET");

        // Act
        await middleware.InvokeAsync(context1);
        await middleware.InvokeAsync(context2);

        // Assert - Different query strings = different cache entries
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task InvokeAsync_DoesNotCacheErrorResponses()
    {
        // Arrange
        var callCount = 0;
        var middleware = CreateMiddleware(async context =>
        {
            callCount++;
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync("error");
        });

        var context1 = CreateHttpContext("/api/status", "GET");
        var context2 = CreateHttpContext("/api/status", "GET");

        // Act
        await middleware.InvokeAsync(context1);
        await middleware.InvokeAsync(context2);

        // Assert - Error responses should not be cached
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task InvokeAsync_RespectsNoCacheHeader()
    {
        // Arrange
        var options = Options.Create(new HttpCacheOptions
        {
            RespectCacheControlHeaders = true
        });

        var callCount = 0;
        var middleware = CreateMiddleware(async context =>
        {
            callCount++;
            context.Response.Headers.CacheControl = "no-cache";
            await context.Response.WriteAsync($"call-{callCount}");
        }, options);

        var context1 = CreateHttpContext("/api/status", "GET");
        var context2 = CreateHttpContext("/api/status", "GET");

        // Act
        await middleware.InvokeAsync(context1);
        await middleware.InvokeAsync(context2);

        // Assert - no-cache responses should not be cached
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task InvokeAsync_RespectsNoStoreHeader()
    {
        // Arrange
        var options = Options.Create(new HttpCacheOptions
        {
            RespectCacheControlHeaders = true
        });

        var callCount = 0;
        var middleware = CreateMiddleware(async context =>
        {
            callCount++;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync($"call-{callCount}");
        }, options);

        var context1 = CreateHttpContext("/api/status", "GET");
        var context2 = CreateHttpContext("/api/status", "GET");

        // Act
        await middleware.InvokeAsync(context1);
        await middleware.InvokeAsync(context2);

        // Assert - no-store responses should not be cached
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task InvokeAsync_CompressesLargeResponses()
    {
        // Arrange
        var largeResponse = new string('x', 2000); // > GzipMinSize (1024)
        var options = Options.Create(new HttpCacheOptions
        {
            EnableGzipCompression = true,
            GzipMinSize = 1024
        });

        var middleware = CreateMiddleware(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(largeResponse);
        }, options);

        var context1 = CreateHttpContext("/api/data", "GET");
        context1.Request.Headers.AcceptEncoding = "gzip";
        await middleware.InvokeAsync(context1);

        // Second request should get compressed response
        var context2 = CreateHttpContext("/api/data", "GET");
        context2.Request.Headers.AcceptEncoding = "gzip";
        await middleware.InvokeAsync(context2);

        // Assert
        var encoding = context2.Response.Headers.ContentEncoding.FirstOrDefault();
        Assert.Equal("gzip", encoding);
    }

    [Fact]
    public async Task InvokeAsync_DoesNotCompressSmallResponses()
    {
        // Arrange
        var smallResponse = """{"ok":true}"""; // < GzipMinSize (1024)
        var options = Options.Create(new HttpCacheOptions
        {
            EnableGzipCompression = true,
            GzipMinSize = 1024
        });

        var middleware = CreateMiddleware(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(smallResponse);
        }, options);

        var context = CreateHttpContext("/api/data", "GET");
        context.Request.Headers.AcceptEncoding = "gzip";
        await middleware.InvokeAsync(context);

        // Assert - small responses should not be compressed
        var encoding = context.Response.Headers.ContentEncoding.FirstOrDefault();
        Assert.Null(encoding);
    }

    [Fact]
    public async Task InvokeAsync_DoesNotCacheOversizedResponses()
    {
        // Arrange
        var largeResponse = new string('x', 100);
        var options = Options.Create(new HttpCacheOptions
        {
            MaxCachedResponseSize = 50
        });

        var callCount = 0;
        var middleware = CreateMiddleware(async context =>
        {
            callCount++;
            await context.Response.WriteAsync(largeResponse);
        }, options);

        var context1 = CreateHttpContext("/api/data", "GET");
        var context2 = CreateHttpContext("/api/data", "GET");

        // Act
        await middleware.InvokeAsync(context1);
        await middleware.InvokeAsync(context2);

        // Assert - oversized responses should not be cached
        Assert.Equal(2, callCount);
    }

    [Fact]
    public void HttpCacheOptions_HasCorrectDefaults()
    {
        // Arrange
        var options = new HttpCacheOptions();

        // Assert
        Assert.Equal(TimeSpan.FromMinutes(5), options.DefaultCacheDuration);
        Assert.Equal(10 * 1024 * 1024, options.MaxCachedResponseSize);
        Assert.True(options.EnableGzipCompression);
        Assert.Equal(1024, options.GzipMinSize);
        Assert.True(options.IncludeQueryStringInCacheKey);
        Assert.True(options.RespectCacheControlHeaders);
        Assert.Empty(options.CacheablePaths);
        Assert.Contains("/api/events", options.ExcludedPaths);
        Assert.Contains("/api/sync", options.ExcludedPaths);
        Assert.Contains("/api/auth", options.ExcludedPaths);
    }

    [Fact]
    public void CachedResponse_HasCorrectProperties()
    {
        // Arrange
        var now = DateTime.UtcNow;
        var cached = new CachedResponse
        {
            StatusCode = 200,
            Headers = new Dictionary<string, string> { ["X-Test"] = "value" },
            Body = new byte[] { 1, 2, 3 },
            CachedAt = now,
            ETag = "\"abc123\"",
            IsCompressed = true,
            ContentType = "application/json"
        };

        // Assert
        Assert.Equal(200, cached.StatusCode);
        Assert.Single(cached.Headers);
        Assert.Equal(3, cached.Body.Length);
        Assert.Equal(now, cached.CachedAt);
        Assert.Equal("\"abc123\"", cached.ETag);
        Assert.True(cached.IsCompressed);
        Assert.Equal("application/json", cached.ContentType);
    }

    [Fact]
    public async Task InvokeAsync_DecompressesForNonGzipClients()
    {
        // Arrange
        var largeResponse = new string('x', 2000);
        var options = Options.Create(new HttpCacheOptions
        {
            EnableGzipCompression = true,
            GzipMinSize = 1024
        });

        var middleware = CreateMiddleware(async context =>
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(largeResponse);
        }, options);

        // First request with gzip support (populates cache with compressed data)
        var context1 = CreateHttpContext("/api/data", "GET");
        context1.Request.Headers.AcceptEncoding = "gzip";
        await middleware.InvokeAsync(context1);

        // Second request without gzip support (should get decompressed)
        var context2 = CreateHttpContext("/api/data", "GET");
        // No AcceptEncoding header = doesn't accept gzip
        await middleware.InvokeAsync(context2);

        // Assert
        var encoding = context2.Response.Headers.ContentEncoding.FirstOrDefault();
        Assert.Null(encoding); // Should not have gzip encoding
        var body = GetResponseBody(context2);
        Assert.Equal(largeResponse, body);
    }

    private HttpCacheMiddleware CreateMiddleware(
        RequestDelegate next,
        IOptions<HttpCacheOptions>? options = null)
    {
        return new HttpCacheMiddleware(
            next,
            _cache,
            _loggerMock.Object,
            options);
    }

    private static DefaultHttpContext CreateHttpContext(string path, string method)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;

        // Parse path and query string
        var queryIndex = path.IndexOf('?');
        if (queryIndex >= 0)
        {
            context.Request.Path = path[..queryIndex];
            context.Request.QueryString = new QueryString(path[queryIndex..]);
        }
        else
        {
            context.Request.Path = path;
        }

        // Set up response body stream
        context.Response.Body = new MemoryStream();

        return context;
    }

    private static string GetResponseBody(HttpContext context)
    {
        if (context.Response.Body is MemoryStream ms)
        {
            ms.Position = 0;
            using var reader = new StreamReader(ms, Encoding.UTF8, leaveOpen: true);
            return reader.ReadToEnd();
        }
        return string.Empty;
    }
}
