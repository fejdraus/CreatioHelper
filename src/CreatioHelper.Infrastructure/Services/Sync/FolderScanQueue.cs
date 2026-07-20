using System.Collections.Concurrent;
using System.Threading.Channels;

namespace CreatioHelper.Infrastructure.Services.Sync;

public sealed record ScanRequest(string FolderId, bool Deep);

/// <summary>
/// Bounded producer/consumer queue for folder scans. Coalesces duplicate requests
/// so a folder that is already pending is not scanned twice concurrently. Backpressure
/// and the actual concurrency limit are enforced by the consuming background service.
/// </summary>
public sealed class FolderScanQueue
{
    private readonly Channel<ScanRequest> _channel = Channel.CreateUnbounded<ScanRequest>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

    private readonly ConcurrentDictionary<string, byte> _pending = new();

    public ChannelReader<ScanRequest> Reader => _channel.Reader;

    public void Enqueue(string folderId, bool deep = false)
    {
        if (string.IsNullOrEmpty(folderId))
        {
            return;
        }

        if (!_pending.TryAdd(folderId, 0))
        {
            return;
        }

        if (!_channel.Writer.TryWrite(new ScanRequest(folderId, deep)))
        {
            _pending.TryRemove(folderId, out _);
        }
    }

    /// <summary>
    /// Called by a worker right after dequeuing so a change arriving during the scan can re-enqueue the folder.
    /// </summary>
    public void Release(string folderId)
    {
        _pending.TryRemove(folderId, out _);
    }
}
