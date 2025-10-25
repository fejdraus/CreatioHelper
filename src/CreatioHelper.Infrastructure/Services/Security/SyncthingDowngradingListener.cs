using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Security;

/// <summary>
/// Syncthing-compatible listener that auto-detects TLS vs plain connections
/// Based on syncthing/lib/tlsutil/tlsutil.go DowngradingListener
/// </summary>
public class SyncthingDowngradingListener : IDisposable
{
    private readonly TcpListener _tcpListener;
    private readonly SslServerAuthenticationOptions _tlsConfig;
    private readonly ILogger<SyncthingDowngradingListener> _logger;
    private bool _disposed;

    public SyncthingDowngradingListener(
        IPEndPoint localEndPoint, 
        SslServerAuthenticationOptions tlsConfig,
        ILogger<SyncthingDowngradingListener> logger)
    {
        _tcpListener = new TcpListener(localEndPoint);
        _tlsConfig = tlsConfig;
        _logger = logger;
    }

    public void Start()
    {
        _tcpListener.Start();
        _logger.LogInformation("Syncthing downgrading listener started on {EndPoint}", _tcpListener.LocalEndpoint);
    }

    public void Stop()
    {
        _tcpListener.Stop();
        _logger.LogInformation("Syncthing downgrading listener stopped");
    }

    /// <summary>
    /// Accept connection and automatically wrap with TLS if detected
    /// Equivalent to Syncthing's DowngradingListener.Accept()
    /// </summary>
    public async Task<Stream> AcceptAsync(CancellationToken cancellationToken = default)
    {
        var tcpClient = await _tcpListener.AcceptTcpClientAsync();
        var stream = tcpClient.GetStream();

        try
        {
            var (unionStream, isTls, error) = await AcceptNoWrapTlsAsync(stream, cancellationToken);

            // Handle identification failure like Syncthing
            if (error == SyncthingTlsError.IdentificationFailed)
            {
                _logger.LogWarning("Failed to identify socket type, passing connection as-is");
                return unionStream;
            }

            if (error != SyncthingTlsError.None)
            {
                throw new InvalidOperationException($"Connection error: {error}");
            }

            if (isTls)
            {
                _logger.LogDebug("TLS connection detected, wrapping with SSL stream");
                var sslStream = new SslStream(unionStream);
                await sslStream.AuthenticateAsServerAsync(_tlsConfig, cancellationToken);
                return sslStream;
            }

            _logger.LogDebug("Plain connection detected");
            return unionStream;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error accepting connection");
            stream.Dispose();
            tcpClient.Close();
            throw;
        }
    }

    /// <summary>
    /// Accept connection without TLS wrapping, just detect protocol type
    /// Equivalent to Syncthing's DowngradingListener.AcceptNoWrapTLS()
    /// </summary>
    private async Task<(SyncthingUnionedStream stream, bool isTls, SyncthingTlsError error)> AcceptNoWrapTlsAsync(
        Stream baseStream, CancellationToken cancellationToken)
    {
        var unionStream = new SyncthingUnionedStream(baseStream);

        try
        {
            // Set 1 second timeout for protocol detection like Syncthing
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(1));

            var buffer = new byte[1];
            var bytesRead = await unionStream.ReadAsync(buffer, 0, 1, cts.Token);

            if (bytesRead == 0)
            {
                _logger.LogDebug("No data read during protocol detection");
                return (unionStream, false, SyncthingTlsError.IdentificationFailed);
            }

            // Store first byte for later replay
            unionStream.StoreFirstByte(buffer[0]);

            // TLS handshake starts with 0x16 (22 decimal) - ContentType.Handshake
            bool isTls = buffer[0] == 0x16;
            
            _logger.LogTrace("Protocol detection: first byte = 0x{FirstByte:X2}, isTLS = {IsTls}", buffer[0], isTls);
            
            return (unionStream, isTls, SyncthingTlsError.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Timeout during protocol detection
            _logger.LogDebug("Timeout during protocol detection, assuming plain connection");
            return (unionStream, false, SyncthingTlsError.IdentificationFailed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during protocol detection");
            return (unionStream, false, SyncthingTlsError.IdentificationFailed);
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _tcpListener?.Stop();
            _disposed = true;
        }
    }
}

/// <summary>
/// Stream that can replay the first byte after protocol detection
/// Equivalent to Syncthing's UnionedConnection
/// </summary>
public class SyncthingUnionedStream : Stream
{
    private readonly Stream _baseStream;
    private byte _firstByte;
    private bool _firstByteConsumed = true; // Start as consumed until we store a byte

    public SyncthingUnionedStream(Stream baseStream)
    {
        _baseStream = baseStream;
    }

    public void StoreFirstByte(byte firstByte)
    {
        _firstByte = firstByte;
        _firstByteConsumed = false;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (!_firstByteConsumed && count > 0)
        {
            // Return the stored first byte
            buffer[offset] = _firstByte;
            _firstByteConsumed = true;
            return 1;
        }

        return await _baseStream.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (!_firstByteConsumed && count > 0)
        {
            // Return the stored first byte
            buffer[offset] = _firstByte;
            _firstByteConsumed = true;
            return 1;
        }

        return _baseStream.Read(buffer, offset, count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await _baseStream.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _baseStream.Write(buffer, offset, count);
    }

    public override bool CanRead => _baseStream.CanRead;
    public override bool CanSeek => _baseStream.CanSeek;
    public override bool CanWrite => _baseStream.CanWrite;
    public override long Length => _baseStream.Length;

    public override long Position
    {
        get => _baseStream.Position;
        set => _baseStream.Position = value;
    }

    public override void Flush() => _baseStream.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _baseStream.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _baseStream.Seek(offset, origin);
    public override void SetLength(long value) => _baseStream.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _baseStream?.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_baseStream != null)
        {
            await _baseStream.DisposeAsync();
        }
        await base.DisposeAsync();
    }
}

/// <summary>
/// TLS error codes matching Syncthing behavior
/// </summary>
public enum SyncthingTlsError
{
    None,
    IdentificationFailed
}