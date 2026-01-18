using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Transport;

/// <summary>
/// TCP transport implementation.
/// </summary>
public class TcpTransport : ITransport
{
    private readonly ILogger<TcpTransport> _logger;
    private readonly TransportOptions _options;
    private readonly X509Certificate2? _certificate;

    public TransportType Type => TransportType.Tcp;
    public int Priority => 20;
    public bool IsAvailable => true; // TCP is always available
    public IReadOnlyList<string> SupportedSchemes => ["tcp", "tcp4", "tcp6"];

    public TcpTransport(ILogger<TcpTransport> logger, TransportOptions? options = null, X509Certificate2? certificate = null)
    {
        _logger = logger;
        _options = options ?? new TransportOptions();
        _certificate = certificate;
    }

    public async Task<ITransportConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("TCP connecting to {Host}:{Port}", host, port);

        var client = new TcpClient();

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(_options.ConnectTimeoutMs);

            await client.ConnectAsync(host, port, cts.Token);

            Stream stream = client.GetStream();

            // Wrap with TLS if enabled
            if (_options.EnableTls)
            {
                var sslStream = new SslStream(stream, false, ValidateServerCertificate);

                var sslOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    ClientCertificates = _certificate != null ? [_certificate] : null,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
                };

                await sslStream.AuthenticateAsClientAsync(sslOptions, cts.Token);
                stream = sslStream;
            }

            _logger.LogDebug("TCP connected to {Host}:{Port}", host, port);

            return new TcpTransportConnection(client, stream);
        }
        catch (Exception ex)
        {
            client.Dispose();
            _logger.LogWarning(ex, "TCP connection to {Host}:{Port} failed", host, port);
            throw;
        }
    }

    public async Task<ITransportListener> ListenAsync(int port, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("TCP starting listener on port {Port}", port);

        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        _logger.LogInformation("TCP listener started on port {Port}", port);

        return new TcpTransportListener(listener, _options, _certificate, _logger);
    }

    public bool TryParseUri(string uri, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        if (string.IsNullOrEmpty(uri))
            return false;

        // Handle tcp://, tcp4://, tcp6://
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

        _logger.LogWarning("TLS certificate validation failed: {Errors}", errors);
        return false;
    }
}

/// <summary>
/// TCP transport connection implementation.
/// </summary>
internal class TcpTransportConnection : ITransportConnection
{
    private readonly TcpClient _client;
    private readonly Stream _stream;
    private bool _disposed;

    public Stream Stream => _stream;
    public EndPoint? RemoteEndPoint => _client.Client?.RemoteEndPoint;
    public EndPoint? LocalEndPoint => _client.Client?.LocalEndPoint;
    public TransportType TransportType => TransportType.Tcp;
    public bool IsConnected => !_disposed && _client.Connected;

    public TcpTransportConnection(TcpClient client, Stream stream)
    {
        _client = client;
        _stream = stream;
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;

        try
        {
            await _stream.FlushAsync(cancellationToken);
        }
        catch
        {
            // Ignore flush errors during close
        }

        _client.Close();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await CloseAsync();
        await _stream.DisposeAsync();
        _client.Dispose();
    }
}

/// <summary>
/// TCP transport listener implementation.
/// </summary>
internal class TcpTransportListener : ITransportListener
{
    private readonly TcpListener _listener;
    private readonly TransportOptions _options;
    private readonly X509Certificate2? _certificate;
    private readonly ILogger _logger;
    private bool _stopped;

    public EndPoint LocalEndPoint => _listener.LocalEndpoint;
    public TransportType TransportType => TransportType.Tcp;

    public TcpTransportListener(TcpListener listener, TransportOptions options, X509Certificate2? certificate, ILogger logger)
    {
        _listener = listener;
        _options = options;
        _certificate = certificate;
        _logger = logger;
    }

    public async Task<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var client = await _listener.AcceptTcpClientAsync(cancellationToken);

        _logger.LogDebug("TCP accepted connection from {RemoteEndPoint}", client.Client.RemoteEndPoint);

        Stream stream = client.GetStream();

        // Wrap with TLS if enabled
        if (_options.EnableTls && _certificate != null)
        {
            var sslStream = new SslStream(stream, false);

            var sslOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13
            };

            await sslStream.AuthenticateAsServerAsync(sslOptions, cancellationToken);
            stream = sslStream;
        }

        return new TcpTransportConnection(client, stream);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_stopped) return Task.CompletedTask;
        _stopped = true;

        _listener.Stop();
        _logger.LogDebug("TCP listener stopped");

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
