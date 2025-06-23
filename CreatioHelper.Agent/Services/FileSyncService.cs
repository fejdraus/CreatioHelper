using System.Diagnostics;
using System.Text.RegularExpressions;
using CreatioHelper.Agent.Abstractions;

namespace CreatioHelper.Agent.Services;

public class FileSyncService : IFileSyncService
{
    private readonly ILogger<FileSyncService> _logger;

    public FileSyncService(ILogger<FileSyncService> logger)
    {
        _logger = logger;
    }

    public async Task<SyncResult> SyncAsync(SyncOptions options, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        long totalBytes = 0;

        try
        {
            if (!await ValidatePathAsync(options.SourcePath))
            {
                return new SyncResult
                {
                    Success = false,
                    Message = $"Source path does not exist: {options.SourcePath}"
                };
            }

            if (!Directory.Exists(options.DestinationPath))
            {
                Directory.CreateDirectory(options.DestinationPath);
            }

            totalBytes = await CopyDirectoryAsync(
                options.SourcePath, 
                options.DestinationPath, 
                options, 
                cancellationToken);

            stopwatch.Stop();

            return new SyncResult
            {
                Success = true,
                Message = $"Successfully synced {totalBytes} bytes",
                BytesTransferred = totalBytes,
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during file sync");
            return new SyncResult
            {
                Success = false,
                Message = ex.Message,
                Duration = stopwatch.Elapsed
            };
        }
    }

    public async Task<SyncResult> SyncAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        var options = new SyncOptions
        {
            SourcePath = sourcePath,
            DestinationPath = destinationPath
        };

        return await SyncAsync(options, cancellationToken);
    }

    public async Task<bool> ValidatePathAsync(string path)
    {
        try
        {
            return await Task.Run(() => Directory.Exists(path) || File.Exists(path));
        }
        catch
        {
            return false;
        }
    }

    private async Task<long> CopyDirectoryAsync(
        string sourcePath, 
        string destinationPath, 
        SyncOptions options, 
        CancellationToken cancellationToken)
    {
        // Упрощенная реализация для примера
        return await Task.FromResult(0L);
    }
}
