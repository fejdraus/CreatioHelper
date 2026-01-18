using System.Net;

namespace CreatioHelper.Infrastructure.Services.Sync.Transport;

/// <summary>
/// Transport protocol types supported by the sync engine.
/// </summary>
public enum TransportType
{
    /// <summary>
    /// TCP transport (fallback, always available).
    /// </summary>
    Tcp = 0,

    /// <summary>
    /// QUIC transport (preferred, requires .NET 7+).
    /// </summary>
    Quic = 1,

    /// <summary>
    /// Relay transport (fallback for NAT traversal).
    /// </summary>
    Relay = 2
}

/// <summary>
/// Represents an established transport connection.
/// </summary>
public interface ITransportConnection : IAsyncDisposable
{
    /// <summary>
    /// Gets the underlying stream for reading/writing data.
    /// </summary>
    Stream Stream { get; }

    /// <summary>
    /// Gets the remote endpoint of this connection.
    /// </summary>
    EndPoint? RemoteEndPoint { get; }

    /// <summary>
    /// Gets the local endpoint of this connection.
    /// </summary>
    EndPoint? LocalEndPoint { get; }

    /// <summary>
    /// Gets the transport type of this connection.
    /// </summary>
    TransportType TransportType { get; }

    /// <summary>
    /// Gets whether this connection is still active.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Closes the connection gracefully.
    /// </summary>
    Task CloseAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a transport listener that accepts incoming connections.
/// </summary>
public interface ITransportListener : IAsyncDisposable
{
    /// <summary>
    /// Gets the local endpoint this listener is bound to.
    /// </summary>
    EndPoint LocalEndPoint { get; }

    /// <summary>
    /// Gets the transport type of this listener.
    /// </summary>
    TransportType TransportType { get; }

    /// <summary>
    /// Accepts an incoming connection.
    /// </summary>
    Task<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops accepting new connections.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Interface for transport implementations.
/// </summary>
public interface ITransport
{
    /// <summary>
    /// Gets the transport type.
    /// </summary>
    TransportType Type { get; }

    /// <summary>
    /// Gets the priority of this transport (lower = higher priority).
    /// QUIC=10, TCP=20, Relay=100
    /// </summary>
    int Priority { get; }

    /// <summary>
    /// Gets whether this transport is available on the current platform.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets the URI schemes supported by this transport (e.g., "tcp", "quic", "quic4", "quic6").
    /// </summary>
    IReadOnlyList<string> SupportedSchemes { get; }

    /// <summary>
    /// Connects to a remote endpoint.
    /// </summary>
    /// <param name="host">The remote host.</param>
    /// <param name="port">The remote port.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The established connection.</returns>
    Task<ITransportConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates a listener on the specified port.
    /// </summary>
    /// <param name="port">The port to listen on.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The transport listener.</returns>
    Task<ITransportListener> ListenAsync(int port, CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses a URI and extracts host and port for this transport.
    /// </summary>
    /// <param name="uri">The URI to parse (e.g., "tcp://host:port").</param>
    /// <param name="host">The extracted host.</param>
    /// <param name="port">The extracted port.</param>
    /// <returns>True if the URI is valid for this transport.</returns>
    bool TryParseUri(string uri, out string host, out int port);
}

/// <summary>
/// Configuration options for transport connections.
/// </summary>
public class TransportOptions
{
    /// <summary>
    /// Connection timeout in milliseconds.
    /// </summary>
    public int ConnectTimeoutMs { get; set; } = 10000;

    /// <summary>
    /// Whether to enable TLS (default: true).
    /// </summary>
    public bool EnableTls { get; set; } = true;

    /// <summary>
    /// Path to the TLS certificate file (optional).
    /// </summary>
    public string? CertificatePath { get; set; }

    /// <summary>
    /// Path to the TLS private key file (optional).
    /// </summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>
    /// Whether to validate remote certificates.
    /// </summary>
    public bool ValidateRemoteCertificate { get; set; } = true;

    /// <summary>
    /// Preferred transport types in order of preference.
    /// </summary>
    public List<TransportType> PreferredTransports { get; set; } = [TransportType.Quic, TransportType.Tcp];
}
