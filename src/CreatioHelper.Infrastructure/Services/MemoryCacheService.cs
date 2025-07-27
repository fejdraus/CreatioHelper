using System.Collections.Concurrent;
using CreatioHelper.Application.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services;

public class MemoryCacheService : ICacheService
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<MemoryCacheService> _logger;
    private readonly IMetricsService _metrics;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public MemoryCacheService(IMemoryCache cache, ILogger<MemoryCacheService> logger, IMetricsService metrics)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
    }

    public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Key cannot be null or empty", nameof(key));

        return _metrics.MeasureAsync("cache_get", () =>
        {
            if (_cache.TryGetValue(key, out var value))
            {
                _metrics.IncrementCounter("cache_hit", new() { ["key"] = SanitizeKey(key) });
                _logger.LogTrace("Cache hit for key: {Key}", key);
                return Task.FromResult((T?)value);
            }

            _metrics.IncrementCounter("cache_miss", new() { ["key"] = SanitizeKey(key) });
            _logger.LogTrace("Cache miss for key: {Key}", key);
            return Task.FromResult(default(T));
        }, new() { ["operation"] = "get", ["key"] = SanitizeKey(key) });
    }

    public Task SetAsync<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Key cannot be null or empty", nameof(key));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = expiration,
                Priority = CacheItemPriority.Normal
            };

            // Добавляем callback для отслеживания истечения срока
            options.RegisterPostEvictionCallback((evictedKey, _, reason, _) =>
            {
                _metrics.IncrementCounter("cache_eviction", new()
                {
                    ["key"] = SanitizeKey(evictedKey.ToString() ?? "unknown"),
                    ["reason"] = reason.ToString()
                });
                _logger.LogTrace("Cache entry evicted: {Key}, Reason: {Reason}", evictedKey, reason);
            });

            _cache.Set(key, value, options);
            _metrics.IncrementCounter("cache_set_success", new() { ["key"] = SanitizeKey(key) });
            _logger.LogTrace("Cache entry set for key: {Key}, Expiration: {Expiration}", key, expiration);

            stopwatch.Stop();
            _metrics.RecordDuration("cache_set", stopwatch.Elapsed, new() { ["operation"] = "set", ["key"] = SanitizeKey(key) });

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.IncrementCounter("cache_set_error", new() { ["key"] = SanitizeKey(key) });
            _logger.LogError(ex, "Failed to set cache entry for key: {Key}", key);
            throw;
        }
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Key cannot be null or empty", nameof(key));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _cache.Remove(key);
            _metrics.IncrementCounter("cache_remove", new() { ["key"] = SanitizeKey(key) });
            _logger.LogTrace("Cache entry removed for key: {Key}", key);

            stopwatch.Stop();
            _metrics.RecordDuration("cache_remove", stopwatch.Elapsed, new() { ["operation"] = "remove", ["key"] = SanitizeKey(key) });
            
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.IncrementCounter("cache_remove_error", new() { ["key"] = SanitizeKey(key) });
            _logger.LogError(ex, "Failed to remove cache entry for key: {Key}", key);
            throw;
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // MemoryCache не имеет метода Clear, поэтому пересоздаем весь кэш
            if (_cache is MemoryCache memoryCache)
            {
                memoryCache.Compact(1.0); // Удаляем все записи
            }

            _metrics.IncrementCounter("cache_clear");
            _logger.LogInformation("Cache cleared");

            stopwatch.Stop();
            _metrics.RecordDuration("cache_clear", stopwatch.Elapsed, new() { ["operation"] = "clear" });
            
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.IncrementCounter("cache_clear_error");
            _logger.LogError(ex, "Failed to clear cache");
            throw;
        }
    }

    public async Task<T> GetOrSetAsync<T>(string key, Func<Task<T>> factory, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Key cannot be null or empty", nameof(key));
        if (factory == null)
            throw new ArgumentNullException(nameof(factory));

        // Проверяем кэш
        var cached = await GetAsync<T>(key, cancellationToken);
        if (cached != null)
        {
            return cached;
        }

        // Используем семафор для предотвращения race condition
        var lockKey = $"lock_{key}";
        var semaphore = _locks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));

        try
        {
            await semaphore.WaitAsync(cancellationToken);

            // Повторная проверка после получения блокировки
            cached = await GetAsync<T>(key, cancellationToken);
            if (cached != null)
            {
                return cached;
            }

            // Генерируем новое значение
            var value = await _metrics.MeasureAsync("cache_factory", factory,
                new() { ["key"] = SanitizeKey(key) });

            // Сохраняем в кэш
            await SetAsync(key, value, expiration, cancellationToken);

            _metrics.IncrementCounter("cache_get_or_set_generated", new() { ["key"] = SanitizeKey(key) });
            return value;
        }
        finally
        {
            semaphore.Release();

            // Очищаем семафор если никто не ждет
            if (semaphore.CurrentCount == 1)
            {
                _locks.TryRemove(lockKey, out _);
            }
        }
    }

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Key cannot be null or empty", nameof(key));

        return _metrics.MeasureAsync("cache_exists", () =>
        {
            var exists = _cache.TryGetValue(key, out _);
            _metrics.IncrementCounter("cache_exists_check", new()
            {
                ["key"] = SanitizeKey(key),
                ["exists"] = exists.ToString()
            });

            return Task.FromResult(exists);
        }, new() { ["operation"] = "exists", ["key"] = SanitizeKey(key) });
    }

    public Task RefreshAsync(string key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("Key cannot be null or empty", nameof(key));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            // Просто получаем значение, что обновляет время последнего доступа
            _cache.TryGetValue(key, out _);
            _metrics.IncrementCounter("cache_refresh", new() { ["key"] = SanitizeKey(key) });

            stopwatch.Stop();
            _metrics.RecordDuration("cache_refresh", stopwatch.Elapsed, new() { ["operation"] = "refresh", ["key"] = SanitizeKey(key) });

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _metrics.IncrementCounter("cache_refresh_error", new() { ["key"] = SanitizeKey(key) });
            _logger.LogError(ex, "Failed to refresh cache entry for key: {Key}", key);
            throw;
        }
    }

    private static string SanitizeKey(string key)
    {
        // Обрезаем длинные ключи для метрик, чтобы избежать кардинальности
        return key.Length > 50 ? $"{key[..47]}..." : key;
    }
}
