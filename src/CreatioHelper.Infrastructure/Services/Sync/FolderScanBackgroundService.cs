using CreatioHelper.Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Consumes the folder scan queue with a bounded pool of workers, keeping folder scanning
/// off the request pipeline and capped so it cannot starve the thread pool. The concurrency
/// limit leaves cores free for latency-sensitive request handling.
/// </summary>
public sealed class FolderScanBackgroundService : BackgroundService
{
    private readonly FolderScanQueue _queue;
    private readonly IServiceProvider _services;
    private readonly ILogger<FolderScanBackgroundService> _logger;
    private readonly int _maxConcurrency;

    public FolderScanBackgroundService(
        FolderScanQueue queue,
        IServiceProvider services,
        ILogger<FolderScanBackgroundService> logger)
    {
        _queue = queue;
        _services = services;
        _logger = logger;
        _maxConcurrency = Math.Clamp(Environment.ProcessorCount - 2, 1, 4);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Folder scan scheduler started with {Workers} worker(s)", _maxConcurrency);

        var workers = new Task[_maxConcurrency];
        for (var i = 0; i < _maxConcurrency; i++)
        {
            workers[i] = WorkerAsync(stoppingToken);
        }

        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task WorkerAsync(CancellationToken cancellationToken)
    {
        var syncEngine = _services.GetRequiredService<ISyncEngine>();

        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                _queue.Release(request.FolderId);
                try
                {
                    await syncEngine.ScanFolderAsync(request.FolderId, request.Deep).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Scheduled scan of folder {FolderId} failed", request.FolderId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown
        }
    }
}
