using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Network.Nat;

/// <summary>
/// NAT-PMP (NAT Port Mapping Protocol) client implementation.
/// Based on RFC 6886 and Syncthing's NAT-PMP implementation.
///
/// NAT-PMP Protocol Format:
/// Request (12 bytes):
///   [Version:1][Opcode:1][Reserved:2][InternalPort:2][ExternalPort:2][Lifetime:4]
///
/// Response (16 bytes):
///   [Version:1][Opcode:1][Result:2][Epoch:4][InternalPort:2][ExternalPort:2][Lifetime:4]
/// </summary>
public class NatPmpClient : IDisposable
{
    private readonly ILogger<NatPmpClient> _logger;
    private readonly UdpClient _udpClient;
    private IPAddress? _gatewayAddress;
    private uint _lastEpoch;
    private bool _disposed;

    // NAT-PMP Constants (RFC 6886)
    private const byte Version = 0;
    private const byte OpcodeExternalAddress = 0;
    private const byte OpcodeMappingUdp = 1;
    private const byte OpcodeMappingTcp = 2;
    private const int PmpPort = 5351;
    private const int DefaultLifetimeSeconds = 7200; // 2 hours
    private const int MaxRetries = 9;
    private const int InitialTimeoutMs = 250;

    // Response result codes
    private const ushort ResultSuccess = 0;
    private const ushort ResultUnsupportedVersion = 1;
    private const ushort ResultNotAuthorized = 2;
    private const ushort ResultNetworkFailure = 3;
    private const ushort ResultOutOfResources = 4;
    private const ushort ResultUnsupportedOpcode = 5;

    public NatPmpClient(ILogger<NatPmpClient> logger)
    {
        _logger = logger;
        _udpClient = new UdpClient();
        _udpClient.Client.ReceiveTimeout = InitialTimeoutMs;
    }

    /// <summary>
    /// Discover the default gateway for NAT-PMP communication
    /// </summary>
    public async Task<bool> DiscoverGatewayAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _gatewayAddress = await GetDefaultGatewayAsync(cancellationToken);

            if (_gatewayAddress == null)
            {
                _logger.LogDebug("No default gateway found for NAT-PMP");
                return false;
            }

            _logger.LogDebug("Discovered default gateway for NAT-PMP: {Gateway}", _gatewayAddress);

            // Verify gateway supports NAT-PMP by requesting external address
            var externalAddress = await GetExternalAddressAsync(cancellationToken);
            if (externalAddress == null)
            {
                _logger.LogDebug("Gateway does not support NAT-PMP");
                return false;
            }

            _logger.LogInformation("NAT-PMP gateway discovered at {Gateway}, external IP: {ExternalIP}",
                _gatewayAddress, externalAddress);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to discover NAT-PMP gateway");
            return false;
        }
    }

    /// <summary>
    /// Get the external (public) IP address from the NAT-PMP gateway
    /// </summary>
    public async Task<IPAddress?> GetExternalAddressAsync(CancellationToken cancellationToken = default)
    {
        if (_gatewayAddress == null)
        {
            if (!await DiscoverGatewayAsync(cancellationToken))
                return null;
        }

        try
        {
            // Request external address (opcode 0)
            var request = new byte[2];
            request[0] = Version;
            request[1] = OpcodeExternalAddress;

            var response = await SendWithRetryAsync(request, 12, cancellationToken);
            if (response == null)
            {
                _logger.LogDebug("No response for external address request");
                return null;
            }

            // Parse response
            // [Version:1][Opcode:1][Result:2][Epoch:4][ExternalIP:4]
            var resultCode = (ushort)((response[2] << 8) | response[3]);
            if (resultCode != ResultSuccess)
            {
                _logger.LogWarning("NAT-PMP external address request failed: {ResultCode}", GetResultDescription(resultCode));
                return null;
            }

            // Update epoch
            _lastEpoch = (uint)((response[4] << 24) | (response[5] << 16) | (response[6] << 8) | response[7]);

            // Extract external IP
            var externalIp = new IPAddress(new[] { response[8], response[9], response[10], response[11] });
            return externalIp;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get external address via NAT-PMP");
            return null;
        }
    }

    /// <summary>
    /// Create a port mapping
    /// </summary>
    /// <param name="protocol">Protocol ("TCP" or "UDP")</param>
    /// <param name="internalPort">Internal port to map</param>
    /// <param name="externalPort">Requested external port (0 for any)</param>
    /// <param name="lifetimeSeconds">Mapping lifetime in seconds (0 to delete)</param>
    /// <returns>Mapping result or null if failed</returns>
    public async Task<NatPmpMapping?> CreateMappingAsync(
        string protocol,
        int internalPort,
        int externalPort = 0,
        int lifetimeSeconds = DefaultLifetimeSeconds,
        CancellationToken cancellationToken = default)
    {
        if (_gatewayAddress == null)
        {
            if (!await DiscoverGatewayAsync(cancellationToken))
                return null;
        }

        try
        {
            // Create mapping request
            var opcode = protocol.ToUpperInvariant() == "UDP" ? OpcodeMappingUdp : OpcodeMappingTcp;

            var request = new byte[12];
            request[0] = Version;
            request[1] = opcode;
            // Reserved bytes 2-3 are zero
            request[4] = (byte)(internalPort >> 8);
            request[5] = (byte)(internalPort & 0xFF);
            request[6] = (byte)(externalPort >> 8);
            request[7] = (byte)(externalPort & 0xFF);
            request[8] = (byte)(lifetimeSeconds >> 24);
            request[9] = (byte)(lifetimeSeconds >> 16);
            request[10] = (byte)(lifetimeSeconds >> 8);
            request[11] = (byte)(lifetimeSeconds & 0xFF);

            var response = await SendWithRetryAsync(request, 16, cancellationToken);
            if (response == null)
            {
                _logger.LogDebug("No response for port mapping request");
                return null;
            }

            // Parse response
            // [Version:1][Opcode:1][Result:2][Epoch:4][InternalPort:2][ExternalPort:2][Lifetime:4]
            var resultCode = (ushort)((response[2] << 8) | response[3]);
            if (resultCode != ResultSuccess)
            {
                _logger.LogWarning("NAT-PMP mapping request failed: {ResultCode}", GetResultDescription(resultCode));
                return null;
            }

            // Update epoch
            _lastEpoch = (uint)((response[4] << 24) | (response[5] << 16) | (response[6] << 8) | response[7]);

            // Extract mapping info
            var mappedInternalPort = (ushort)((response[8] << 8) | response[9]);
            var mappedExternalPort = (ushort)((response[10] << 8) | response[11]);
            var mappedLifetime = (uint)((response[12] << 24) | (response[13] << 16) | (response[14] << 8) | response[15]);

            var mapping = new NatPmpMapping
            {
                Protocol = protocol,
                InternalPort = mappedInternalPort,
                ExternalPort = mappedExternalPort,
                LifetimeSeconds = mappedLifetime,
                GatewayAddress = _gatewayAddress,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddSeconds(mappedLifetime)
            };

            _logger.LogInformation("Created NAT-PMP mapping: {Protocol} {InternalPort}→{ExternalPort}, lifetime {Lifetime}s",
                protocol, mappedInternalPort, mappedExternalPort, mappedLifetime);

            return mapping;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create NAT-PMP mapping");
            return null;
        }
    }

    /// <summary>
    /// Delete a port mapping (lifetime = 0)
    /// </summary>
    public async Task<bool> DeleteMappingAsync(string protocol, int internalPort, CancellationToken cancellationToken = default)
    {
        var result = await CreateMappingAsync(protocol, internalPort, 0, 0, cancellationToken);
        return result != null && result.LifetimeSeconds == 0;
    }

    /// <summary>
    /// Renew an existing mapping
    /// </summary>
    public async Task<NatPmpMapping?> RenewMappingAsync(NatPmpMapping existingMapping, CancellationToken cancellationToken = default)
    {
        return await CreateMappingAsync(
            existingMapping.Protocol,
            existingMapping.InternalPort,
            existingMapping.ExternalPort,
            DefaultLifetimeSeconds,
            cancellationToken);
    }

    private async Task<byte[]?> SendWithRetryAsync(byte[] request, int expectedResponseLength, CancellationToken cancellationToken)
    {
        if (_gatewayAddress == null)
            return null;

        var endpoint = new IPEndPoint(_gatewayAddress, PmpPort);
        var timeout = InitialTimeoutMs;

        for (var retry = 0; retry < MaxRetries; retry++)
        {
            try
            {
                _udpClient.Client.ReceiveTimeout = timeout;

                await _udpClient.SendAsync(request, endpoint, cancellationToken);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(timeout);

                var result = await _udpClient.ReceiveAsync(cts.Token);

                if (result.Buffer.Length >= expectedResponseLength)
                {
                    return result.Buffer;
                }

                _logger.LogDebug("NAT-PMP response too short: {Length} bytes, expected {Expected}",
                    result.Buffer.Length, expectedResponseLength);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout - double it and retry
                _logger.LogDebug("NAT-PMP request timeout, retry {Retry}/{MaxRetries}", retry + 1, MaxRetries);
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "NAT-PMP socket error");
                break;
            }

            // RFC 6886: Double timeout for each retry, up to 64 seconds
            timeout = Math.Min(timeout * 2, 64000);
        }

        return null;
    }

    private static async Task<IPAddress?> GetDefaultGatewayAsync(CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            try
            {
                var networkInterfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                                ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

                foreach (var networkInterface in networkInterfaces)
                {
                    var properties = networkInterface.GetIPProperties();
                    var gateway = properties.GatewayAddresses
                        .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork);

                    if (gateway != null)
                    {
                        return gateway.Address;
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }, cancellationToken);
    }

    private static string GetResultDescription(ushort resultCode)
    {
        return resultCode switch
        {
            ResultSuccess => "Success",
            ResultUnsupportedVersion => "Unsupported Version",
            ResultNotAuthorized => "Not Authorized",
            ResultNetworkFailure => "Network Failure",
            ResultOutOfResources => "Out of Resources",
            ResultUnsupportedOpcode => "Unsupported Opcode",
            _ => $"Unknown Result ({resultCode})"
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _udpClient.Dispose();
            _disposed = true;
        }
    }
}

/// <summary>
/// NAT-PMP port mapping information
/// </summary>
public class NatPmpMapping
{
    public string Protocol { get; set; } = string.Empty;
    public int InternalPort { get; set; }
    public int ExternalPort { get; set; }
    public uint LifetimeSeconds { get; set; }
    public IPAddress? GatewayAddress { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
    public TimeSpan TimeToExpire => ExpiresAt - DateTime.UtcNow;

    /// <summary>
    /// Should renew when less than half lifetime remaining
    /// </summary>
    public bool ShouldRenew => TimeToExpire.TotalSeconds < LifetimeSeconds / 2;
}
