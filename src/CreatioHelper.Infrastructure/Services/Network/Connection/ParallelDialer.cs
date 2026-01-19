using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Network.Connection;

/// <summary>
/// Parallel connection dialer with priority-based ordering.
/// Based on Syncthing's parallel dialer from lib/connections/service.go
///
/// Key behaviors:
/// - Max 64 parallel dials globally
/// - Max 8 parallel dials per device
/// - Dials addresses in priority buckets (lower priority first)
/// - Returns first successful connection
/// </summary>
public class ParallelDialer : IDisposable
{
    private readonly ILogger<ParallelDialer> _logger;
    private readonly IConnectionPrioritizer _prioritizer;
    private readonly ParallelDialerConfiguration _config;
    private readonly SemaphoreSlim _globalSemaphore;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceSemaphores = new();
    private bool _disposed;

    public ParallelDialer(
        ILogger<ParallelDialer> logger,
        IConnectionPrioritizer prioritizer,
        ParallelDialerConfiguration? config = null)
    {
        _logger = logger;
        _prioritizer = prioritizer;
        _config = config ?? new ParallelDialerConfiguration();
        _globalSemaphore = new SemaphoreSlim(_config.MaxParallelDials);
    }

    /// <summary>
    /// Dial multiple addresses in parallel with priority ordering.
    /// Returns the first successful connection.
    /// </summary>
    public async Task<DialResult?> DialAsync(
        string deviceId,
        IEnumerable<string> addresses,
        X509Certificate2? localCertificate = null,
        CancellationToken cancellationToken = default)
    {
        var addressList = addresses.ToList();
        if (!addressList.Any())
        {
            _logger.LogDebug("No addresses to dial for device {DeviceId}", deviceId);
            return null;
        }

        _logger.LogDebug("Dialing {Count} addresses for device {DeviceId}", addressList.Count, deviceId);

        // Get or create per-device semaphore
        var deviceSemaphore = _deviceSemaphores.GetOrAdd(
            deviceId,
            _ => new SemaphoreSlim(_config.MaxParallelDialsPerDevice));

        // Group addresses into priority buckets
        var priorityBuckets = _prioritizer.GetPriorityBuckets(addressList).ToList();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Process each priority bucket in order
        foreach (var bucket in priorityBuckets)
        {
            if (cts.Token.IsCancellationRequested)
                break;

            var bucketAddresses = bucket.ToList();
            _logger.LogDebug("Processing priority bucket {Priority} with {Count} addresses",
                bucket.Key, bucketAddresses.Count);

            var result = await DialBucketAsync(
                deviceId,
                bucketAddresses,
                deviceSemaphore,
                localCertificate,
                cts.Token);

            if (result != null)
            {
                // Cancel remaining dials
                cts.Cancel();
                return result;
            }
        }

        _logger.LogDebug("Failed to dial any address for device {DeviceId}", deviceId);
        return null;
    }

    /// <summary>
    /// Dial all addresses in a bucket in parallel
    /// </summary>
    private async Task<DialResult?> DialBucketAsync(
        string deviceId,
        List<PrioritizedAddress> addresses,
        SemaphoreSlim deviceSemaphore,
        X509Certificate2? localCertificate,
        CancellationToken cancellationToken)
    {
        var results = new ConcurrentBag<DialResult>();
        var tasks = new List<Task>();
        var firstResultTcs = new TaskCompletionSource<DialResult?>();

        foreach (var addr in addresses)
        {
            var dialTask = Task.Run(async () =>
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                // Acquire both semaphores
                var acquiredGlobal = false;
                var acquiredDevice = false;

                try
                {
                    // Try to acquire global semaphore with timeout
                    acquiredGlobal = await _globalSemaphore.WaitAsync(
                        TimeSpan.FromMilliseconds(_config.SemaphoreTimeoutMs),
                        cancellationToken);

                    if (!acquiredGlobal)
                    {
                        _logger.LogDebug("Global semaphore timeout for {Address}", addr.Address);
                        return;
                    }

                    // Try to acquire device semaphore
                    acquiredDevice = await deviceSemaphore.WaitAsync(
                        TimeSpan.FromMilliseconds(_config.SemaphoreTimeoutMs),
                        cancellationToken);

                    if (!acquiredDevice)
                    {
                        _logger.LogDebug("Device semaphore timeout for {Address}", addr.Address);
                        return;
                    }

                    // Dial the address
                    var result = await DialSingleAsync(addr, localCertificate, cancellationToken);

                    if (result != null)
                    {
                        result.DeviceId = deviceId;
                        results.Add(result);
                        firstResultTcs.TrySetResult(result);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Normal cancellation
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error dialing {Address}", addr.Address);
                }
                finally
                {
                    if (acquiredDevice) deviceSemaphore.Release();
                    if (acquiredGlobal) _globalSemaphore.Release();
                }
            }, cancellationToken);

            tasks.Add(dialTask);
        }

        // Wait for first result or all tasks to complete
        var completionTask = Task.WhenAny(
            firstResultTcs.Task,
            Task.WhenAll(tasks).ContinueWith(_ =>
            {
                firstResultTcs.TrySetResult(null);
                return (DialResult?)null;
            }, TaskContinuationOptions.ExecuteSynchronously)
        );

        await completionTask;

        // Return first successful result
        return results.IsEmpty ? null : results.First();
    }

    /// <summary>
    /// Dial a single address
    /// </summary>
    private async Task<DialResult?> DialSingleAsync(
        PrioritizedAddress addr,
        X509Certificate2? localCertificate,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Dialing {Address} (priority {Priority}, type {Type})",
            addr.Address, addr.Priority, addr.Type);

        try
        {
            switch (addr.Type)
            {
                case ConnectionType.TcpLan:
                case ConnectionType.TcpWan:
                    return await DialTcpAsync(addr, localCertificate, cancellationToken);

                case ConnectionType.QuicLan:
                case ConnectionType.QuicWan:
                    // QUIC not implemented yet, fall back to TCP
                    _logger.LogDebug("QUIC not implemented, attempting TCP for {Address}", addr.Address);
                    var tcpAddr = new PrioritizedAddress
                    {
                        Address = addr.Address.Replace("quic://", "tcp://"),
                        Priority = addr.Priority,
                        Type = addr.IsLan ? ConnectionType.TcpLan : ConnectionType.TcpWan,
                        IsLan = addr.IsLan,
                        IpAddress = addr.IpAddress,
                        Port = addr.Port
                    };
                    return await DialTcpAsync(tcpAddr, localCertificate, cancellationToken);

                case ConnectionType.Relay:
                    // Relay connections handled separately
                    _logger.LogDebug("Relay dialing not implemented for {Address}", addr.Address);
                    return null;

                default:
                    _logger.LogDebug("Unknown connection type for {Address}", addr.Address);
                    return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to dial {Address}", addr.Address);
            return null;
        }
    }

    /// <summary>
    /// Establish TCP connection with TLS
    /// </summary>
    private async Task<DialResult?> DialTcpAsync(
        PrioritizedAddress addr,
        X509Certificate2? localCertificate,
        CancellationToken cancellationToken)
    {
        if (addr.IpAddress == null || addr.Port == 0)
        {
            _logger.LogDebug("Invalid address or port for {Address}", addr.Address);
            return null;
        }

        var tcpClient = new TcpClient();

        try
        {
            // Set connection timeout
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectCts.CancelAfter(TimeSpan.FromSeconds(_config.ConnectionTimeoutSeconds));

            await tcpClient.ConnectAsync(addr.IpAddress, addr.Port, connectCts.Token);

            _logger.LogDebug("TCP connected to {Address}", addr.Address);

            // Perform TLS handshake
            var sslStream = new SslStream(
                tcpClient.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: (_, cert, _, _) =>
                {
                    // Accept all certificates for now (we validate device ID separately)
                    return true;
                });

            var sslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = addr.IpAddress.ToString(),
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            };

            if (localCertificate != null)
            {
                sslOptions.ClientCertificates = new X509CertificateCollection { localCertificate };
            }

            using var tlsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            tlsCts.CancelAfter(TimeSpan.FromSeconds(_config.TlsHandshakeTimeoutSeconds));

            await sslStream.AuthenticateAsClientAsync(sslOptions, tlsCts.Token);

            _logger.LogDebug("TLS handshake completed for {Address}", addr.Address);

            // Get remote certificate
            var remoteCert = sslStream.RemoteCertificate as X509Certificate2;

            return new DialResult
            {
                Address = addr.Address,
                Priority = addr.Priority,
                ConnectionType = addr.Type,
                IsLan = addr.IsLan,
                TcpClient = tcpClient,
                SslStream = sslStream,
                RemoteCertificate = remoteCert,
                LocalEndpoint = tcpClient.Client.LocalEndPoint as IPEndPoint,
                RemoteEndpoint = tcpClient.Client.RemoteEndPoint as IPEndPoint,
                ConnectedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            tcpClient.Dispose();
            _logger.LogDebug(ex, "TCP dial failed for {Address}", addr.Address);
            return null;
        }
    }

    /// <summary>
    /// Clean up per-device semaphores for disconnected devices
    /// </summary>
    public void CleanupDevice(string deviceId)
    {
        if (_deviceSemaphores.TryRemove(deviceId, out var semaphore))
        {
            semaphore.Dispose();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _globalSemaphore.Dispose();

            foreach (var semaphore in _deviceSemaphores.Values)
            {
                semaphore.Dispose();
            }
            _deviceSemaphores.Clear();

            _disposed = true;
        }
    }
}

/// <summary>
/// Configuration for parallel dialer
/// </summary>
public class ParallelDialerConfiguration
{
    public int MaxParallelDials { get; set; } = 64;
    public int MaxParallelDialsPerDevice { get; set; } = 8;
    public int ConnectionTimeoutSeconds { get; set; } = 10;
    public int TlsHandshakeTimeoutSeconds { get; set; } = 10;
    public int SemaphoreTimeoutMs { get; set; } = 5000;
}

/// <summary>
/// Result of a dial attempt
/// </summary>
public class DialResult : IDisposable
{
    /// <summary>
    /// Device ID (set after successful dial)
    /// </summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>
    /// Address that was dialed
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Connection priority
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// Type of connection
    /// </summary>
    public ConnectionType ConnectionType { get; set; }

    /// <summary>
    /// Is this a LAN connection
    /// </summary>
    public bool IsLan { get; set; }

    /// <summary>
    /// TCP client (for TCP connections)
    /// </summary>
    public TcpClient? TcpClient { get; set; }

    /// <summary>
    /// SSL stream (for TLS connections)
    /// </summary>
    public SslStream? SslStream { get; set; }

    /// <summary>
    /// Remote certificate
    /// </summary>
    public X509Certificate2? RemoteCertificate { get; set; }

    /// <summary>
    /// Local endpoint
    /// </summary>
    public IPEndPoint? LocalEndpoint { get; set; }

    /// <summary>
    /// Remote endpoint
    /// </summary>
    public IPEndPoint? RemoteEndpoint { get; set; }

    /// <summary>
    /// When the connection was established
    /// </summary>
    public DateTime ConnectedAt { get; set; }

    /// <summary>
    /// Get the network stream for reading/writing
    /// </summary>
    public Stream? GetStream() => (Stream?)SslStream ?? TcpClient?.GetStream();

    public void Dispose()
    {
        SslStream?.Dispose();
        TcpClient?.Dispose();
    }
}
