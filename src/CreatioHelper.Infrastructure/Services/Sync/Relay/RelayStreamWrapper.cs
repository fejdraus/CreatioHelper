using System.Net;
using System.Net.Sockets;

namespace CreatioHelper.Infrastructure.Services.Sync.Relay;

/// <summary>
/// Wrapper to make relay streams compatible with BepConnection which expects TcpClient
/// This allows relay streams to be used seamlessly with the existing BEP protocol implementation
/// </summary>
public class RelayStreamWrapper : TcpClient
{
    private readonly Stream _relayStream;
    private readonly RelayNetworkStream _networkStream;
    private bool _disposed;

    public RelayStreamWrapper(Stream relayStream) : base()
    {
        _relayStream = relayStream ?? throw new ArgumentNullException(nameof(relayStream));
        _networkStream = new RelayNetworkStream(_relayStream);
    }

    public new Stream GetStream()
    {
        return _networkStream;
    }

    public new bool Connected => !_disposed && _relayStream.CanRead && _relayStream.CanWrite;

    protected override void Dispose(bool disposing)
    {
        if (!_disposed && disposing)
        {
            _disposed = true;
            _networkStream?.Dispose();
            _relayStream?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// NetworkStream implementation that wraps a relay stream
/// </summary>
internal class RelayNetworkStream : Stream
{
    private readonly Stream _underlyingStream;

    public RelayNetworkStream(Stream underlyingStream)
    {
        _underlyingStream = underlyingStream ?? throw new ArgumentNullException(nameof(underlyingStream));
    }

    public override bool CanRead => _underlyingStream.CanRead;
    public override bool CanWrite => _underlyingStream.CanWrite;
    public override bool CanSeek => _underlyingStream.CanSeek;
    public override long Length => _underlyingStream.Length;

    public override long Position
    {
        get => _underlyingStream.Position;
        set => _underlyingStream.Position = value;
    }

    public override void Flush() => _underlyingStream.Flush();
    
    public override Task FlushAsync(CancellationToken cancellationToken) => 
        _underlyingStream.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) =>
        _underlyingStream.Read(buffer, offset, count);

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        await _underlyingStream.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _underlyingStream.ReadAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) =>
        _underlyingStream.Write(buffer, offset, count);

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        await _underlyingStream.WriteAsync(buffer, offset, count, cancellationToken);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _underlyingStream.WriteAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) =>
        _underlyingStream.Seek(offset, origin);

    public override void SetLength(long value) =>
        _underlyingStream.SetLength(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _underlyingStream?.Dispose();
        }
        base.Dispose(disposing);
    }
}