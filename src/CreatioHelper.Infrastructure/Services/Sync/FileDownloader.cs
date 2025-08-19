using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Downloads files using BEP Request/Response protocol
/// Based on Syncthing's file download logic
/// </summary>
public class FileDownloader
{
    private readonly ILogger<FileDownloader> _logger;
    private readonly ISyncProtocol _protocol;

    public FileDownloader(ILogger<FileDownloader> logger, ISyncProtocol protocol)
    {
        _logger = logger;
        _protocol = protocol;
    }

    /// <summary>
    /// Downloads a file from remote device using block-based transfer
    /// </summary>
    public async Task<DownloadResult> DownloadFileAsync(
        string deviceId, 
        string folderId, 
        SyncFileInfo remoteFile, 
        string localFilePath,
        CancellationToken cancellationToken = default)
    {
        var result = new DownloadResult { FileName = remoteFile.Name };
        var startTime = DateTime.UtcNow;

        try
        {
            _logger.LogInformation("Starting download of {FileName} ({FileSize} bytes, {BlockCount} blocks) from device {DeviceId}",
                remoteFile.Name, remoteFile.Size, remoteFile.Blocks.Count, deviceId);

            // Create directory if needed
            var directory = Path.GetDirectoryName(localFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Create temporary file for download
            var tempFilePath = localFilePath + ".tmp";
            var downloadedBlocks = 0;
            var totalBytes = 0L;

            using (var fileStream = File.Create(tempFilePath))
            {
                // Download blocks in order
                foreach (var block in remoteFile.Blocks.OrderBy(b => b.Offset))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        _logger.LogTrace("Requesting block {Offset}-{End} of {FileName}",
                            block.Offset, block.Offset + block.Size - 1, remoteFile.Name);

                        var blockData = await _protocol.RequestBlockAsync(
                            deviceId, 
                            folderId, 
                            remoteFile.Name, 
                            block.Offset, 
                            block.Size, 
                            block.Hash);

                        if (blockData == null || blockData.Length == 0)
                        {
                            throw new InvalidOperationException($"Received empty block for {remoteFile.Name} at offset {block.Offset}");
                        }

                        if (blockData.Length != block.Size)
                        {
                            throw new InvalidOperationException($"Block size mismatch for {remoteFile.Name}: expected {block.Size}, got {blockData.Length}");
                        }

                        // Verify block hash (strict validation like original Syncthing)
                        var actualHash = System.Security.Cryptography.SHA256.HashData(blockData);
                        var actualHashHex = Convert.ToHexString(actualHash).ToLower();
                        
                        if (actualHashHex != block.Hash.ToLower())
                        {
                            _logger.LogWarning("❌ FileDownloader: Block hash mismatch for {FileName} at offset {Offset}: expected {ExpectedHash}, got {ActualHash}", 
                                remoteFile.Name, block.Offset, block.Hash, actualHashHex);
                            
                            // In original Syncthing, this would retry with another device
                            // For now, we'll fail this block (can be enhanced with device retry logic later)
                            throw new InvalidOperationException($"Block hash mismatch for {remoteFile.Name} at offset {block.Offset}: expected {block.Hash}, got {actualHashHex}");
                        }
                        else
                        {
                            _logger.LogTrace("✅ FileDownloader: Block hash verified for {FileName} at offset {Offset}", 
                                remoteFile.Name, block.Offset);
                        }

                        // Write block to file at correct position
                        fileStream.Seek(block.Offset, SeekOrigin.Begin);
                        await fileStream.WriteAsync(blockData, cancellationToken);

                        downloadedBlocks++;
                        totalBytes += blockData.Length;

                        _logger.LogTrace("Downloaded block {BlockNumber}/{TotalBlocks} for {FileName} ({BytesTransferred}/{TotalBytes} bytes)",
                            downloadedBlocks, remoteFile.Blocks.Count, remoteFile.Name, totalBytes, remoteFile.Size);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error downloading block {Offset}-{End} of {FileName}",
                            block.Offset, block.Offset + block.Size - 1, remoteFile.Name);
                        result.Errors.Add($"Block {block.Offset}: {ex.Message}");
                        throw;
                    }
                }

                await fileStream.FlushAsync(cancellationToken);
            }

            // Verify complete file
            if (totalBytes != remoteFile.Size)
            {
                throw new InvalidOperationException($"File size mismatch for {remoteFile.Name}: expected {remoteFile.Size}, got {totalBytes}");
            }

            // Verify complete file hash if available (strict validation like original Syncthing)
            if (!string.IsNullOrEmpty(remoteFile.Hash))
            {
                var fileHash = await CalculateFileHashAsync(tempFilePath);
                if (fileHash != remoteFile.Hash)
                {
                    _logger.LogError("❌ FileDownloader: Complete file hash mismatch for {FileName}: expected {ExpectedHash}, got {ActualHash}", 
                        remoteFile.Name, remoteFile.Hash, fileHash);
                    throw new InvalidOperationException($"Complete file hash mismatch for {remoteFile.Name}: expected {remoteFile.Hash}, got {fileHash}");
                }
                else
                {
                    _logger.LogDebug("✅ FileDownloader: Complete file hash verified for {FileName}", remoteFile.Name);
                }
            }

            // Move temp file to final location
            if (File.Exists(localFilePath))
            {
                File.Delete(localFilePath);
            }
            File.Move(tempFilePath, localFilePath);

            // Set file modification time to match remote
            File.SetLastWriteTimeUtc(localFilePath, remoteFile.ModifiedTime);

            result.Success = true;
            result.BytesTransferred = totalBytes;
            result.BlocksTransferred = downloadedBlocks;
            result.Duration = DateTime.UtcNow - startTime;

            _logger.LogInformation("Successfully downloaded {FileName}: {BytesTransferred} bytes in {Duration}ms ({BlocksTransferred} blocks)",
                remoteFile.Name, totalBytes, result.Duration.TotalMilliseconds, downloadedBlocks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download {FileName} from device {DeviceId}: {Error}",
                remoteFile.Name, deviceId, ex.Message);

            result.Success = false;
            result.Error = ex.Message;
            result.Duration = DateTime.UtcNow - startTime;

            // Clean up temp file
            var tempFilePath = localFilePath + ".tmp";
            if (File.Exists(tempFilePath))
            {
                try
                {
                    File.Delete(tempFilePath);
                }
                catch (Exception cleanupEx)
                {
                    _logger.LogWarning(cleanupEx, "Failed to clean up temp file {TempFilePath}", tempFilePath);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Calculates SHA-256 hash of a file
    /// </summary>
    private async Task<string> CalculateFileHashAsync(string filePath)
    {
        using var file = File.OpenRead(filePath);
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = await sha256.ComputeHashAsync(file);
        return Convert.ToHexString(hash).ToLower();
    }
}

/// <summary>
/// Result of file download operation
/// </summary>
public class DownloadResult
{
    public string FileName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public List<string> Errors { get; set; } = new();
    public long BytesTransferred { get; set; }
    public int BlocksTransferred { get; set; }
    public TimeSpan Duration { get; set; }
}