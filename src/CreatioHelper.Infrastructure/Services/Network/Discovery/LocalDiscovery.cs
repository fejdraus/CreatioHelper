using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Network.Discovery;

/// <summary>
/// Local network device discovery using UDP multicast/broadcast.
/// Compatible with Syncthing's local discovery protocol.
/// </summary>
public class LocalDiscovery : IAsyncDisposable
{
    private readonly ILogger<LocalDiscovery> _logger;
    private readonly string _deviceId;
    private readonly int _listenPort;
    private readonly int _announceIntervalSeconds;

    private UdpClient? _udpClient;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _announceTask;
    private Task? _listenTask;

    // Syncthing local discovery magic and protocol
    private static readonly byte[] DiscoveryMagic = { 0x2E, 0xA7, 0xD9, 0x0B };
    private const int DefaultPort = 21027;

    // IPv4 and IPv6 multicast addresses (Syncthing compatible)
    private static readonly IPAddress MulticastAddressV4 = IPAddress.Parse("239.21.0.27");
    private static readonly IPAddress MulticastAddressV6 = IPAddress.Parse("ff12::8384");

    /// <summary>
    /// Event fired when a device is discovered on the local network.
    /// </summary>
    public event Func<DiscoveredDevice, Task>? DeviceDiscovered;

    public LocalDiscovery(
        ILogger<LocalDiscovery> logger,
        string deviceId,
        int listenPort = DefaultPort,
        int announceIntervalSeconds = 30)
    {
        _logger = logger;
        _deviceId = deviceId;
        _listenPort = listenPort;
        _announceIntervalSeconds = announceIntervalSeconds;
    }

    /// <summary>
    /// Gets whether local discovery is running.
    /// </summary>
    public bool IsRunning => _cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested;

    /// <summary>
    /// Starts local discovery.
    /// </summary>
    /// <param name="addresses">List of addresses to announce.</param>
    public async Task StartAsync(IEnumerable<string> addresses, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            _logger.LogWarning("Local discovery is already running");
            return;
        }

        _cancellationTokenSource = new CancellationTokenSource();
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _cancellationTokenSource.Token);

        try
        {
            // Create UDP client for listening
            _udpClient = new UdpClient(_listenPort);
            _udpClient.EnableBroadcast = true;

            // Join multicast group
            try
            {
                _udpClient.JoinMulticastGroup(MulticastAddressV4);
                _logger.LogDebug("Joined IPv4 multicast group: {Address}", MulticastAddressV4);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to join IPv4 multicast group");
            }

            var addressList = addresses.ToList();

            // Start announce task
            _announceTask = AnnounceLoopAsync(addressList, linkedCts.Token);

            // Start listen task
            _listenTask = ListenLoopAsync(linkedCts.Token);

            _logger.LogInformation("Local discovery started on port {Port}", _listenPort);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start local discovery");
            await StopAsync();
            throw;
        }
    }

    /// <summary>
    /// Stops local discovery.
    /// </summary>
    public async Task StopAsync()
    {
        if (_cancellationTokenSource != null)
        {
            _cancellationTokenSource.Cancel();
        }

        // Wait for tasks to complete
        if (_announceTask != null)
        {
            try { await _announceTask; } catch { }
        }
        if (_listenTask != null)
        {
            try { await _listenTask; } catch { }
        }

        _udpClient?.Dispose();
        _udpClient = null;
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = null;

        _logger.LogInformation("Local discovery stopped");
    }

    /// <summary>
    /// Sends a discovery announcement.
    /// </summary>
    public async Task AnnounceAsync(IEnumerable<string> addresses, CancellationToken cancellationToken = default)
    {
        if (_udpClient == null)
        {
            throw new InvalidOperationException("Local discovery is not running");
        }

        var announcement = CreateAnnouncement(addresses.ToList());
        var data = SerializeAnnouncement(announcement);

        // Send to broadcast
        var broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, _listenPort);
        await _udpClient.SendAsync(data, data.Length, broadcastEndpoint);

        // Send to multicast
        var multicastEndpoint = new IPEndPoint(MulticastAddressV4, _listenPort);
        await _udpClient.SendAsync(data, data.Length, multicastEndpoint);

        _logger.LogDebug("Sent local discovery announcement with {Count} addresses", addresses.Count());
    }

    private async Task AnnounceLoopAsync(List<string> addresses, CancellationToken cancellationToken)
    {
        // Initial announcement
        try
        {
            await AnnounceAsync(addresses, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send initial discovery announcement");
        }

        // Periodic announcements
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_announceIntervalSeconds), cancellationToken);
                await AnnounceAsync(addresses, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to send discovery announcement");
            }
        }
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(cancellationToken);
                await ProcessAnnouncementAsync(result.Buffer, result.RemoteEndPoint, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.Interrupted)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error receiving discovery announcement");
            }
        }
    }

    private async Task ProcessAnnouncementAsync(byte[] data, IPEndPoint remoteEndpoint, CancellationToken cancellationToken)
    {
        if (data.Length < 8)
        {
            return; // Too short
        }

        // Check magic
        for (int i = 0; i < 4; i++)
        {
            if (data[i] != DiscoveryMagic[i])
            {
                return; // Invalid magic
            }
        }

        try
        {
            var announcement = DeserializeAnnouncement(data);
            if (announcement == null || announcement.DeviceId == _deviceId)
            {
                return; // Invalid or our own announcement
            }

            var discoveredDevice = new DiscoveredDevice
            {
                DeviceId = announcement.DeviceId,
                Addresses = announcement.Addresses.ToList(),
                InstanceId = announcement.InstanceId,
                DiscoveredAt = DateTime.UtcNow,
                SourceEndpoint = remoteEndpoint,
                DiscoveryMethod = DiscoveryMethod.Local
            };

            _logger.LogDebug("Discovered device: {DeviceId} at {Endpoint} with {Count} addresses",
                discoveredDevice.DeviceId, remoteEndpoint, discoveredDevice.Addresses.Count);

            if (DeviceDiscovered != null)
            {
                await DeviceDiscovered.Invoke(discoveredDevice);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to process discovery announcement");
        }
    }

    private LocalAnnouncement CreateAnnouncement(List<string> addresses)
    {
        return new LocalAnnouncement
        {
            DeviceId = _deviceId,
            Addresses = addresses,
            InstanceId = Environment.ProcessId
        };
    }

    private byte[] SerializeAnnouncement(LocalAnnouncement announcement)
    {
        var json = JsonSerializer.Serialize(announcement);
        var jsonBytes = Encoding.UTF8.GetBytes(json);

        var result = new byte[4 + 4 + jsonBytes.Length];
        DiscoveryMagic.CopyTo(result, 0);
        BitConverter.GetBytes(jsonBytes.Length).CopyTo(result, 4);
        jsonBytes.CopyTo(result, 8);

        return result;
    }

    private LocalAnnouncement? DeserializeAnnouncement(byte[] data)
    {
        if (data.Length < 8) return null;

        var length = BitConverter.ToInt32(data, 4);
        if (data.Length < 8 + length) return null;

        var json = Encoding.UTF8.GetString(data, 8, length);
        return JsonSerializer.Deserialize<LocalAnnouncement>(json);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private class LocalAnnouncement
    {
        public string DeviceId { get; set; } = string.Empty;
        public List<string> Addresses { get; set; } = new();
        public int InstanceId { get; set; }
    }
}

/// <summary>
/// Discovery method used to find a device.
/// </summary>
public enum DiscoveryMethod
{
    Local,
    Global,
    Static,
    Relay
}

/// <summary>
/// Information about a discovered device.
/// </summary>
public class DiscoveredDevice
{
    public string DeviceId { get; set; } = string.Empty;
    public List<string> Addresses { get; set; } = new();
    public int InstanceId { get; set; }
    public DateTime DiscoveredAt { get; set; }
    public IPEndPoint? SourceEndpoint { get; set; }
    public DiscoveryMethod DiscoveryMethod { get; set; }
    public TimeSpan? Latency { get; set; }
}
