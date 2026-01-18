using System.Diagnostics;

namespace CreatioHelper.Infrastructure.Services.Sync.Transfer;

/// <summary>
/// Token bucket based bandwidth limiter for rate limiting data transfers.
/// Compatible with Syncthing's rate limiting approach.
/// </summary>
public class BandwidthLimiter
{
    private readonly long _bytesPerSecond;
    private readonly long _burstSize;
    private readonly object _lock = new();
    private readonly Stopwatch _stopwatch;

    private long _tokens;
    private long _lastRefillTicks;

    /// <summary>
    /// Creates a new bandwidth limiter.
    /// </summary>
    /// <param name="bytesPerSecond">Maximum bytes per second (0 = unlimited).</param>
    /// <param name="burstSize">Maximum burst size in bytes (default: 4MB or 1 second worth, whichever is larger).</param>
    public BandwidthLimiter(long bytesPerSecond, long burstSize = 0)
    {
        _bytesPerSecond = bytesPerSecond;

        // Default burst size: max of 4MB or 1 second worth of bandwidth
        _burstSize = burstSize > 0
            ? burstSize
            : Math.Max(4 * 1024 * 1024, bytesPerSecond);

        _tokens = _burstSize;
        _stopwatch = Stopwatch.StartNew();
        _lastRefillTicks = _stopwatch.ElapsedTicks;
    }

    /// <summary>
    /// Gets whether rate limiting is enabled.
    /// </summary>
    public bool IsLimited => _bytesPerSecond > 0;

    /// <summary>
    /// Gets the configured bytes per second limit.
    /// </summary>
    public long BytesPerSecond => _bytesPerSecond;

    /// <summary>
    /// Gets the configured burst size.
    /// </summary>
    public long BurstSize => _burstSize;

    /// <summary>
    /// Gets current available tokens (bytes).
    /// </summary>
    public long AvailableTokens
    {
        get
        {
            lock (_lock)
            {
                RefillTokens();
                return _tokens;
            }
        }
    }

    /// <summary>
    /// Waits until the specified number of bytes can be sent/received.
    /// </summary>
    /// <param name="bytes">Number of bytes to acquire.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task that completes when bytes can be transferred.</returns>
    public async ValueTask AcquireAsync(long bytes, CancellationToken cancellationToken = default)
    {
        if (!IsLimited || bytes <= 0)
        {
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TimeSpan waitTime;
            lock (_lock)
            {
                RefillTokens();

                if (_tokens >= bytes)
                {
                    _tokens -= bytes;
                    return;
                }

                // Calculate wait time for enough tokens
                var neededTokens = bytes - _tokens;
                var secondsToWait = (double)neededTokens / _bytesPerSecond;
                waitTime = TimeSpan.FromSeconds(Math.Max(secondsToWait, 0.001)); // Min 1ms
            }

            // Wait outside the lock
            await Task.Delay(waitTime, cancellationToken);
        }
    }

    /// <summary>
    /// Tries to acquire bytes without waiting.
    /// </summary>
    /// <param name="bytes">Number of bytes to acquire.</param>
    /// <returns>True if bytes were acquired, false otherwise.</returns>
    public bool TryAcquire(long bytes)
    {
        if (!IsLimited || bytes <= 0)
        {
            return true;
        }

        lock (_lock)
        {
            RefillTokens();

            if (_tokens >= bytes)
            {
                _tokens -= bytes;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Acquires as many bytes as possible up to the requested amount.
    /// </summary>
    /// <param name="maxBytes">Maximum bytes to acquire.</param>
    /// <returns>Number of bytes actually acquired.</returns>
    public long AcquirePartial(long maxBytes)
    {
        if (!IsLimited || maxBytes <= 0)
        {
            return maxBytes;
        }

        lock (_lock)
        {
            RefillTokens();

            var acquired = Math.Min(_tokens, maxBytes);
            _tokens -= acquired;
            return acquired;
        }
    }

    /// <summary>
    /// Returns unused bytes back to the bucket.
    /// </summary>
    /// <param name="bytes">Number of bytes to return.</param>
    public void Return(long bytes)
    {
        if (!IsLimited || bytes <= 0)
        {
            return;
        }

        lock (_lock)
        {
            _tokens = Math.Min(_tokens + bytes, _burstSize);
        }
    }

    /// <summary>
    /// Calculates the time to wait before the specified bytes become available.
    /// </summary>
    /// <param name="bytes">Number of bytes needed.</param>
    /// <returns>Estimated wait time.</returns>
    public TimeSpan EstimateWaitTime(long bytes)
    {
        if (!IsLimited || bytes <= 0)
        {
            return TimeSpan.Zero;
        }

        lock (_lock)
        {
            RefillTokens();

            if (_tokens >= bytes)
            {
                return TimeSpan.Zero;
            }

            var neededTokens = bytes - _tokens;
            return TimeSpan.FromSeconds((double)neededTokens / _bytesPerSecond);
        }
    }

    private void RefillTokens()
    {
        var currentTicks = _stopwatch.ElapsedTicks;
        var elapsedTicks = currentTicks - _lastRefillTicks;

        if (elapsedTicks <= 0)
        {
            return;
        }

        // Calculate tokens to add based on elapsed time
        var elapsedSeconds = (double)elapsedTicks / Stopwatch.Frequency;
        var tokensToAdd = (long)(elapsedSeconds * _bytesPerSecond);

        if (tokensToAdd > 0)
        {
            _tokens = Math.Min(_tokens + tokensToAdd, _burstSize);
            _lastRefillTicks = currentTicks;
        }
    }
}

/// <summary>
/// Combined bandwidth limiter for upload and download limits.
/// </summary>
public class CombinedBandwidthLimiter
{
    private readonly BandwidthLimiter? _uploadLimiter;
    private readonly BandwidthLimiter? _downloadLimiter;

    /// <summary>
    /// Creates a combined bandwidth limiter.
    /// </summary>
    /// <param name="uploadBytesPerSecond">Upload limit (0 = unlimited).</param>
    /// <param name="downloadBytesPerSecond">Download limit (0 = unlimited).</param>
    public CombinedBandwidthLimiter(long uploadBytesPerSecond, long downloadBytesPerSecond)
    {
        if (uploadBytesPerSecond > 0)
        {
            _uploadLimiter = new BandwidthLimiter(uploadBytesPerSecond);
        }

        if (downloadBytesPerSecond > 0)
        {
            _downloadLimiter = new BandwidthLimiter(downloadBytesPerSecond);
        }
    }

    /// <summary>
    /// Gets the upload limiter.
    /// </summary>
    public BandwidthLimiter? UploadLimiter => _uploadLimiter;

    /// <summary>
    /// Gets the download limiter.
    /// </summary>
    public BandwidthLimiter? DownloadLimiter => _downloadLimiter;

    /// <summary>
    /// Gets whether upload limiting is enabled.
    /// </summary>
    public bool IsUploadLimited => _uploadLimiter?.IsLimited ?? false;

    /// <summary>
    /// Gets whether download limiting is enabled.
    /// </summary>
    public bool IsDownloadLimited => _downloadLimiter?.IsLimited ?? false;

    /// <summary>
    /// Acquires bytes for upload.
    /// </summary>
    public ValueTask AcquireUploadAsync(long bytes, CancellationToken cancellationToken = default)
    {
        return _uploadLimiter?.AcquireAsync(bytes, cancellationToken) ?? ValueTask.CompletedTask;
    }

    /// <summary>
    /// Acquires bytes for download.
    /// </summary>
    public ValueTask AcquireDownloadAsync(long bytes, CancellationToken cancellationToken = default)
    {
        return _downloadLimiter?.AcquireAsync(bytes, cancellationToken) ?? ValueTask.CompletedTask;
    }

    /// <summary>
    /// Tries to acquire upload bytes without waiting.
    /// </summary>
    public bool TryAcquireUpload(long bytes)
    {
        return _uploadLimiter?.TryAcquire(bytes) ?? true;
    }

    /// <summary>
    /// Tries to acquire download bytes without waiting.
    /// </summary>
    public bool TryAcquireDownload(long bytes)
    {
        return _downloadLimiter?.TryAcquire(bytes) ?? true;
    }
}
