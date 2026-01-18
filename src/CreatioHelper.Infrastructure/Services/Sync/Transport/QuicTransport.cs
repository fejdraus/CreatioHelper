using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Transport;

/// <summary>
/// QUIC transport implementation using .NET's native QUIC support.
/// Requires .NET 7+ and a platform with QUIC support (Windows 11+, Linux with libmsquic).
/// </summary>
/// <remarks>
/// QUIC availability is checked at runtime via <see cref="IsAvailable"/>.
/// The class uses runtime guards to ensure QUIC APIs are only called on supported platforms.
/// </remarks>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public class QuicTransport : ITransport
{
    private readonly ILogger<QuicTransport> _logger;
    private readonly TransportOptions _options;
    private readonly X509Certificate2? _certificate;

    // Syncthing QUIC ALPN protocol identifier
    private static readonly SslApplicationProtocol SyncthingAlpn = new("bep/1.0");

    public TransportType Type => TransportType.Quic;
    public int Priority => 10; // QUIC is preferred over TCP
    public IReadOnlyList<string> SupportedSchemes => ["quic", "quic4", "quic6"];

    public bool IsAvailable
    {
        get
        {
            try
            {
                return QuicConnection.IsSupported;
            }
            catch
            {
                return false;
            }
        }
    }

    public QuicTransport(ILogger<QuicTransport> logger, TransportOptions? options = null, X509Certificate2? certificate = null)
    {
        _logger = logger;
        _options = options ?? new TransportOptions();
        _certificate = certificate;
    }

    public async Task<ITransportConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException("QUIC is not supported on this platform");
        }

        _logger.LogDebug("QUIC connecting to {Host}:{Port}", host, port);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_options.ConnectTimeoutMs);

            var clientOptions = new QuicClientConnectionOptions
            {
                RemoteEndPoint = new DnsEndPoint(host, port),
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    ApplicationProtocols = [SyncthingAlpn],
                    ClientCertificates = _certificate != null ? [_certificate] : null,
                    RemoteCertificateValidationCallback = ValidateServerCertificate
                }
            };

            var connection = await QuicConnection.ConnectAsync(clientOptions, cts.Token);

            _logger.LogDebug("QUIC connected to {Host}:{Port}", host, port);

            // Open a bidirectional stream
            var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cts.Token);

            return new QuicTransportConnection(connection, stream);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QUIC connection to {Host}:{Port} failed", host, port);
            throw;
        }
    }

    public async Task<ITransportListener> ListenAsync(int port, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new PlatformNotSupportedException("QUIC is not supported on this platform");
        }

        if (_certificate == null)
        {
            throw new InvalidOperationException("QUIC listener requires a TLS certificate");
        }

        _logger.LogDebug("QUIC starting listener on port {Port}", port);

        var listenerOptions = new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Any, port),
            ApplicationProtocols = [SyncthingAlpn],
            ConnectionOptionsCallback = (connection, hello, token) =>
            {
                return ValueTask.FromResult(new QuicServerConnectionOptions
                {
                    DefaultStreamErrorCode = 0,
                    DefaultCloseErrorCode = 0,
                    ServerAuthenticationOptions = new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        ApplicationProtocols = [SyncthingAlpn],
                        ClientCertificateRequired = true,
                        RemoteCertificateValidationCallback = ValidateClientCertificate
                    }
                });
            }
        };

        var listener = await QuicListener.ListenAsync(listenerOptions, cancellationToken);

        _logger.LogInformation("QUIC listener started on port {Port}", port);

        return new QuicTransportListener(listener, _logger);
    }

    public bool TryParseUri(string uri, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        if (string.IsNullOrEmpty(uri))
            return false;

        // Handle quic://, quic4://, quic6://
        foreach (var scheme in SupportedSchemes)
        {
            if (uri.StartsWith($"{scheme}://", StringComparison.OrdinalIgnoreCase))
            {
                var hostPort = uri.Substring(scheme.Length + 3);
                return TryParseHostPort(hostPort, out host, out port);
            }
        }

        return false;
    }

    private static bool TryParseHostPort(string hostPort, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        // Handle IPv6: [host]:port
        if (hostPort.StartsWith('['))
        {
            var closeBracket = hostPort.IndexOf(']');
            if (closeBracket < 0) return false;

            host = hostPort.Substring(1, closeBracket - 1);
            var remainder = hostPort.Substring(closeBracket + 1);

            if (remainder.StartsWith(':'))
            {
                return int.TryParse(remainder.Substring(1), out port);
            }

            return false;
        }

        // Handle IPv4/hostname: host:port
        var colonIndex = hostPort.LastIndexOf(':');
        if (colonIndex < 0) return false;

        host = hostPort.Substring(0, colonIndex);
        return int.TryParse(hostPort.Substring(colonIndex + 1), out port);
    }

    private bool ValidateServerCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (!_options.ValidateRemoteCertificate)
            return true;

        if (errors == SslPolicyErrors.None)
            return true;

        _logger.LogWarning("QUIC TLS certificate validation failed: {Errors}", errors);
        return false;
    }

    private bool ValidateClientCertificate(object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        // For Syncthing, we accept self-signed certificates and extract device ID from them
        if (certificate == null)
        {
            _logger.LogWarning("QUIC client did not provide a certificate");
            return false;
        }

        return true;
    }
}

/// <summary>
/// QUIC transport connection implementation.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal class QuicTransportConnection : ITransportConnection
{
    private readonly QuicConnection _connection;
    private readonly QuicStream _stream;
    private bool _disposed;

    public Stream Stream => _stream;
    public EndPoint? RemoteEndPoint => _connection.RemoteEndPoint;
    public EndPoint? LocalEndPoint => _connection.LocalEndPoint;
    public TransportType TransportType => TransportType.Quic;
    public bool IsConnected => !_disposed && _connection.RemoteEndPoint != null;

    public QuicTransportConnection(QuicConnection connection, QuicStream stream)
    {
        _connection = connection;
        _stream = stream;
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;

        try
        {
            _stream.CompleteWrites();
            await _stream.FlushAsync(cancellationToken);
        }
        catch
        {
            // Ignore errors during close
        }

        await _connection.CloseAsync(0, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _stream.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

/// <summary>
/// QUIC transport listener implementation.
/// </summary>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal class QuicTransportListener : ITransportListener
{
    private readonly QuicListener _listener;
    private readonly ILogger _logger;
    private bool _stopped;

    public EndPoint LocalEndPoint => _listener.LocalEndPoint;
    public TransportType TransportType => TransportType.Quic;

    public QuicTransportListener(QuicListener listener, ILogger logger)
    {
        _listener = listener;
        _logger = logger;
    }

    public async Task<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var connection = await _listener.AcceptConnectionAsync(cancellationToken);

        _logger.LogDebug("QUIC accepted connection from {RemoteEndPoint}", connection.RemoteEndPoint);

        // Accept the incoming stream
        var stream = await connection.AcceptInboundStreamAsync(cancellationToken);

        return new QuicTransportConnection(connection, stream);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_stopped) return;
        _stopped = true;

        await _listener.DisposeAsync();
        _logger.LogDebug("QUIC listener stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
