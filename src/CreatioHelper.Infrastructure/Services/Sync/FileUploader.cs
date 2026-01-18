using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Uploads files using BEP protocol
/// Based on Syncthing's file upload logic (sending local files to remote devices)
/// Supports both full upload and delta upload (only changed blocks)
/// </summary>
public class FileUploader
{
    private readonly ILogger<FileUploader> _logger;
    private readonly ISyncProtocol _protocol;
    private readonly DeltaSyncEngine? _deltaSyncEngine;
    private readonly IStatisticsCollector? _statisticsCollector;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> _uploadSemaphores = new();

    public FileUploader(ILogger<FileUploader> logger, ISyncProtocol protocol)
    {
        _logger = logger;
        _protocol = protocol;
        _deltaSyncEngine = null;
        _statisticsCollector = null;
    }

    public FileUploader(ILogger<FileUploader> logger, ISyncProtocol protocol, DeltaSyncEngine deltaSyncEngine)
    {
        _logger = logger;
        _protocol = protocol;
        _deltaSyncEngine = deltaSyncEngine;
        _statisticsCollector = null;
    }

    public FileUploader(ILogger<FileUploader> logger, ISyncProtocol protocol, IStatisticsCollector statisticsCollector)
    {
        _logger = logger;
        _protocol = protocol;
        _deltaSyncEngine = null;
        _statisticsCollector = statisticsCollector;
    }

    public FileUploader(ILogger<FileUploader> logger, ISyncProtocol protocol, DeltaSyncEngine? deltaSyncEngine, IStatisticsCollector? statisticsCollector)
    {
        _logger = logger;
        _protocol = protocol;
        _deltaSyncEngine = deltaSyncEngine;
        _statisticsCollector = statisticsCollector;
    }

    /// <summary>
    /// Uploads a file to remote device by sending IndexUpdate with file info.
    /// Remote device will then request blocks using BlockRequest.
    /// </summary>
    public async Task<UploadResult> UploadFileAsync(
        string deviceId,
        string folderId,
        string localFilePath,
        SyncFileInfo fileInfo,
        CancellationToken cancellationToken = default)
    {
        var result = new UploadResult { FileName = fileInfo.Name };
        var startTime = DateTime.UtcNow;

        // Use semaphore to prevent concurrent uploads of the same file
        var semaphoreKey = localFilePath.ToLowerInvariant();
        var semaphore = _uploadSemaphores.GetOrAdd(semaphoreKey, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync(cancellationToken);

        try
        {
            _logger.LogInformation("Starting upload of {FileName} ({FileSize} bytes, {BlockCount} blocks) to device {DeviceId}",
                fileInfo.Name, fileInfo.Size, fileInfo.Blocks.Count, deviceId);

            // Verify file exists
            if (!File.Exists(localFilePath))
            {
                throw new FileNotFoundException($"Local file not found: {localFilePath}");
            }

            // Calculate blocks if not already done
            if (fileInfo.Blocks.Count == 0 && fileInfo.Size > 0)
            {
                var blocks = await CalculateFileBlocksAsync(localFilePath, cancellationToken);
                fileInfo.SetBlocks(blocks);

                // Calculate file hash from blocks
                var fileHash = CalculateFileHash(blocks);
                fileInfo.UpdateHash(fileHash);
            }

            // Verify local file matches expected blocks (integrity check)
            var verifyResult = await VerifyLocalFileAsync(localFilePath, fileInfo, cancellationToken);
            if (!verifyResult.IsValid)
            {
                throw new InvalidOperationException($"Local file verification failed: {verifyResult.Error}");
            }

            // Send IndexUpdate to notify remote device about this file
            // The remote device will then request blocks as needed
            await _protocol.SendIndexUpdateAsync(deviceId, folderId, new List<SyncFileInfo> { fileInfo });

            _logger.LogInformation("Sent IndexUpdate for {FileName} to device {DeviceId} - remote will request blocks",
                fileInfo.Name, deviceId);

            result.Success = true;
            result.BytesTransferred = fileInfo.Size;
            result.BlocksTransferred = fileInfo.Blocks.Count;
            result.Duration = DateTime.UtcNow - startTime;

            _logger.LogInformation("Upload initiated for {FileName}: notified remote of {BytesTransferred} bytes ({BlocksTransferred} blocks) in {Duration}ms",
                fileInfo.Name, result.BytesTransferred, result.BlocksTransferred, result.Duration.TotalMilliseconds);

            // Record statistics for successful upload
            await RecordUploadStatisticsAsync(deviceId, folderId, fileInfo.Name, result.BytesTransferred,
                fileInfo.Size, result.BlocksTransferred, 0, false, result.Duration, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload {FileName} to device {DeviceId}: {Error}",
                fileInfo.Name, deviceId, ex.Message);

            result.Success = false;
            result.Error = ex.Message;
            result.Duration = DateTime.UtcNow - startTime;

            // Record statistics for failed upload
            await RecordUploadStatisticsAsync(deviceId, folderId, fileInfo.Name, 0,
                fileInfo.Size, 0, 0, false, result.Duration, false, ex.Message);
        }
        finally
        {
            semaphore.Release();
        }

        return result;
    }

    /// <summary>
    /// Performs delta upload - only sends changed blocks compared to remote file
    /// This is more efficient for large files with small changes
    /// </summary>
    /// <param name="deviceId">Target device ID</param>
    /// <param name="folderId">Folder ID</param>
    /// <param name="localFilePath">Path to local file</param>
    /// <param name="localFileInfo">Local file info with blocks</param>
    /// <param name="remoteFileInfo">Remote file info with blocks (what remote has)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Upload result with delta statistics</returns>
    public async Task<DeltaUploadResult> DeltaUploadAsync(
        string deviceId,
        string folderId,
        string localFilePath,
        SyncFileInfo localFileInfo,
        SyncFileInfo? remoteFileInfo,
        CancellationToken cancellationToken = default)
    {
        var result = new DeltaUploadResult { FileName = localFileInfo.Name };
        var startTime = DateTime.UtcNow;

        var semaphoreKey = localFilePath.ToLowerInvariant();
        var semaphore = _uploadSemaphores.GetOrAdd(semaphoreKey, _ => new SemaphoreSlim(1, 1));

        await semaphore.WaitAsync(cancellationToken);

        try
        {
            _logger.LogInformation("Starting delta upload of {FileName} to device {DeviceId}",
                localFileInfo.Name, deviceId);

            // Verify file exists
            if (!File.Exists(localFilePath))
            {
                throw new FileNotFoundException($"Local file not found: {localFilePath}");
            }

            // Calculate blocks if not already done
            if (localFileInfo.Blocks.Count == 0 && localFileInfo.Size > 0)
            {
                var blocks = await CalculateFileBlocksAsync(localFilePath, cancellationToken);
                localFileInfo.SetBlocks(blocks);

                var fileHash = CalculateFileHash(blocks);
                localFileInfo.UpdateHash(fileHash);
            }

            // If no remote file info or remote has no blocks, do full upload
            if (remoteFileInfo == null || remoteFileInfo.Blocks.Count == 0)
            {
                _logger.LogInformation("No remote file info available, performing full upload for {FileName}", localFileInfo.Name);

                var fullResult = await UploadFileAsync(deviceId, folderId, localFilePath, localFileInfo, cancellationToken);
                return new DeltaUploadResult
                {
                    FileName = fullResult.FileName,
                    Success = fullResult.Success,
                    Error = fullResult.Error,
                    TotalBytes = fullResult.BytesTransferred,
                    ChangedBytes = fullResult.BytesTransferred,
                    TotalBlocks = fullResult.BlocksTransferred,
                    ChangedBlocks = fullResult.BlocksTransferred,
                    Duration = fullResult.Duration,
                    IsDeltaUpload = false
                };
            }

            // Compare blocks to find changes
            var deltaComparison = CompareBlocksForUpload(localFileInfo, remoteFileInfo);

            _logger.LogInformation("Delta analysis for {FileName}: {ChangedBlocks}/{TotalBlocks} blocks changed ({ChangedBytes}/{TotalBytes} bytes, {Percentage:F1}% transfer)",
                localFileInfo.Name,
                deltaComparison.ChangedBlocks.Count,
                localFileInfo.Blocks.Count,
                deltaComparison.ChangedBytes,
                localFileInfo.Size,
                localFileInfo.Size > 0 ? (deltaComparison.ChangedBytes * 100.0 / localFileInfo.Size) : 0);

            // If files are identical, no upload needed
            if (deltaComparison.ChangedBlocks.Count == 0)
            {
                _logger.LogInformation("Files are identical, no upload needed for {FileName}", localFileInfo.Name);

                result.Success = true;
                result.TotalBytes = localFileInfo.Size;
                result.ChangedBytes = 0;
                result.TotalBlocks = localFileInfo.Blocks.Count;
                result.ChangedBlocks = 0;
                result.UnchangedBlocks = localFileInfo.Blocks.Count;
                result.Duration = DateTime.UtcNow - startTime;
                result.IsDeltaUpload = true;

                // Record statistics for identical file (no actual transfer)
                await RecordUploadStatisticsAsync(deviceId, folderId, localFileInfo.Name, 0,
                    localFileInfo.Size, 0, localFileInfo.Blocks.Count, true, result.Duration, true, null);

                return result;
            }

            // Create file info with only changed blocks for IndexUpdate
            // Remote will only request these blocks
            var deltaFileInfo = CreateDeltaFileInfo(localFileInfo, deltaComparison.ChangedBlocks);

            // Verify changed blocks match local file
            var verifyResult = await VerifyLocalFileBlocksAsync(localFilePath, deltaComparison.ChangedBlocks, cancellationToken);
            if (!verifyResult.IsValid)
            {
                throw new InvalidOperationException($"Local file verification failed: {verifyResult.Error}");
            }

            // Send IndexUpdate with full file info (remote needs all block hashes to know what to request)
            await _protocol.SendIndexUpdateAsync(deviceId, folderId, new List<SyncFileInfo> { localFileInfo });

            _logger.LogInformation("Delta upload initiated for {FileName}: {ChangedBlocks} changed blocks ({ChangedBytes} bytes) will be requested by remote",
                localFileInfo.Name, deltaComparison.ChangedBlocks.Count, deltaComparison.ChangedBytes);

            result.Success = true;
            result.TotalBytes = localFileInfo.Size;
            result.ChangedBytes = deltaComparison.ChangedBytes;
            result.TotalBlocks = localFileInfo.Blocks.Count;
            result.ChangedBlocks = deltaComparison.ChangedBlocks.Count;
            result.UnchangedBlocks = deltaComparison.UnchangedBlocks.Count;
            result.Duration = DateTime.UtcNow - startTime;
            result.IsDeltaUpload = true;

            // Record statistics for successful delta upload
            await RecordUploadStatisticsAsync(deviceId, folderId, localFileInfo.Name, deltaComparison.ChangedBytes,
                localFileInfo.Size, deltaComparison.ChangedBlocks.Count, deltaComparison.UnchangedBlocks.Count,
                true, result.Duration, true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed delta upload of {FileName} to device {DeviceId}: {Error}",
                localFileInfo.Name, deviceId, ex.Message);

            result.Success = false;
            result.Error = ex.Message;
            result.Duration = DateTime.UtcNow - startTime;

            // Record statistics for failed delta upload
            await RecordUploadStatisticsAsync(deviceId, folderId, localFileInfo.Name, 0,
                localFileInfo.Size, 0, 0, true, result.Duration, false, ex.Message);
        }
        finally
        {
            semaphore.Release();
        }

        return result;
    }

    /// <summary>
    /// Compares local and remote blocks to determine what needs to be uploaded
    /// </summary>
    public DeltaBlockComparison CompareBlocksForUpload(SyncFileInfo localFile, SyncFileInfo remoteFile)
    {
        var result = new DeltaBlockComparison();

        // If file hashes match, files are identical
        if (!string.IsNullOrEmpty(localFile.Hash) && !string.IsNullOrEmpty(remoteFile.Hash) &&
            string.Equals(localFile.Hash, remoteFile.Hash, StringComparison.OrdinalIgnoreCase))
        {
            result.UnchangedBlocks = localFile.Blocks.ToList();
            return result;
        }

        // Create lookup of remote blocks by hash for fast comparison
        var remoteBlocksByHash = new HashSet<string>(
            remoteFile.Blocks.Select(b => b.Hash.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        foreach (var localBlock in localFile.Blocks)
        {
            if (remoteBlocksByHash.Contains(localBlock.Hash.ToLowerInvariant()))
            {
                // Remote already has this block
                result.UnchangedBlocks.Add(localBlock);
            }
            else
            {
                // Remote needs this block
                result.ChangedBlocks.Add(localBlock);
                result.ChangedBytes += localBlock.Size;
            }
        }

        return result;
    }

    /// <summary>
    /// Creates a file info with only changed blocks for delta upload
    /// </summary>
    private SyncFileInfo CreateDeltaFileInfo(SyncFileInfo originalFileInfo, List<BlockInfo> changedBlocks)
    {
        var deltaFileInfo = new SyncFileInfo(
            originalFileInfo.FolderId,
            originalFileInfo.Name,
            originalFileInfo.RelativePath,
            originalFileInfo.Size,
            originalFileInfo.ModifiedTime);

        deltaFileInfo.SetBlocks(changedBlocks);
        deltaFileInfo.UpdateHash(originalFileInfo.Hash);

        return deltaFileInfo;
    }

    /// <summary>
    /// Verifies specific blocks in local file match their expected hashes
    /// </summary>
    private async Task<FileVerificationResult> VerifyLocalFileBlocksAsync(
        string filePath, List<BlockInfo> blocksToVerify, CancellationToken cancellationToken)
    {
        try
        {
            using var file = File.OpenRead(filePath);
            var buffer = new byte[16 * 1024 * 1024]; // Max block size 16MB

            foreach (var block in blocksToVerify)
            {
                cancellationToken.ThrowIfCancellationRequested();

                file.Seek(block.Offset, SeekOrigin.Begin);
                var bytesRead = await file.ReadAsync(buffer.AsMemory(0, block.Size), cancellationToken);

                if (bytesRead != block.Size)
                {
                    return new FileVerificationResult
                    {
                        IsValid = false,
                        Error = $"Block read error at offset {block.Offset}: expected {block.Size}, read {bytesRead}"
                    };
                }

                var actualHash = SHA256.HashData(buffer.AsSpan(0, bytesRead));
                var actualHashHex = Convert.ToHexString(actualHash).ToLower();

                if (!string.Equals(actualHashHex, block.Hash, StringComparison.OrdinalIgnoreCase))
                {
                    return new FileVerificationResult
                    {
                        IsValid = false,
                        Error = $"Block hash mismatch at offset {block.Offset}: expected {block.Hash}, actual {actualHashHex}"
                    };
                }
            }

            return new FileVerificationResult { IsValid = true };
        }
        catch (Exception ex)
        {
            return new FileVerificationResult
            {
                IsValid = false,
                Error = $"Verification error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Calculates block hashes for a file (Syncthing-compatible)
    /// </summary>
    public async Task<List<BlockInfo>> CalculateFileBlocksAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var blocks = new List<BlockInfo>();
        var fileInfo = new FileInfo(filePath);
        var blockSize = CalculateBlockSize(fileInfo.Length);

        using var file = File.OpenRead(filePath);
        var buffer = new byte[blockSize];
        long offset = 0;

        while (offset < fileInfo.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var bytesToRead = (int)Math.Min(blockSize, fileInfo.Length - offset);
            var bytesRead = await file.ReadAsync(buffer.AsMemory(0, bytesToRead), cancellationToken);

            if (bytesRead > 0)
            {
                var blockData = buffer.AsSpan(0, bytesRead).ToArray();
                var hash = SHA256.HashData(blockData);
                var hashHex = Convert.ToHexString(hash).ToLower();

                // Calculate weak hash (rolling hash for delta sync optimization)
                var weakHash = CalculateWeakHash(blockData);

                blocks.Add(new BlockInfo(offset, bytesRead, hashHex, weakHash));
                offset += bytesRead;
            }
        }

        _logger.LogDebug("Calculated {BlockCount} blocks for {FileName} with block size {BlockSize}",
            blocks.Count, Path.GetFileName(filePath), blockSize);

        return blocks;
    }

    /// <summary>
    /// Verifies that local file matches expected block information
    /// </summary>
    private async Task<FileVerificationResult> VerifyLocalFileAsync(
        string filePath, SyncFileInfo fileInfo, CancellationToken cancellationToken)
    {
        try
        {
            var localFileInfo = new FileInfo(filePath);

            // Check size
            if (localFileInfo.Length != fileInfo.Size)
            {
                return new FileVerificationResult
                {
                    IsValid = false,
                    Error = $"Size mismatch: expected {fileInfo.Size}, actual {localFileInfo.Length}"
                };
            }

            // Verify blocks if available
            if (fileInfo.Blocks.Count > 0)
            {
                using var file = File.OpenRead(filePath);
                var buffer = new byte[16 * 1024 * 1024]; // Max block size 16MB

                foreach (var block in fileInfo.Blocks)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    file.Seek(block.Offset, SeekOrigin.Begin);
                    var bytesRead = await file.ReadAsync(buffer.AsMemory(0, block.Size), cancellationToken);

                    if (bytesRead != block.Size)
                    {
                        return new FileVerificationResult
                        {
                            IsValid = false,
                            Error = $"Block read error at offset {block.Offset}: expected {block.Size}, read {bytesRead}"
                        };
                    }

                    var actualHash = SHA256.HashData(buffer.AsSpan(0, bytesRead));
                    var actualHashHex = Convert.ToHexString(actualHash).ToLower();

                    if (actualHashHex != block.Hash.ToLower())
                    {
                        return new FileVerificationResult
                        {
                            IsValid = false,
                            Error = $"Block hash mismatch at offset {block.Offset}: expected {block.Hash}, actual {actualHashHex}"
                        };
                    }
                }
            }

            return new FileVerificationResult { IsValid = true };
        }
        catch (Exception ex)
        {
            return new FileVerificationResult
            {
                IsValid = false,
                Error = $"Verification error: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Calculates optimal block size based on file size (Syncthing algorithm)
    /// </summary>
    private static int CalculateBlockSize(long fileSize)
    {
        const int desiredPerFileBlocks = 2000;
        int[] blockSizes = { 128 * 1024, 256 * 1024, 512 * 1024, 1024 * 1024,
                            2048 * 1024, 4096 * 1024, 8192 * 1024, 16384 * 1024 };

        foreach (var size in blockSizes)
        {
            if (fileSize < desiredPerFileBlocks * (long)size)
                return size;
        }

        return blockSizes[^1]; // Max block size
    }

    /// <summary>
    /// Calculates weak hash for block (Syncthing rolling checksum)
    /// Used for delta sync optimization
    /// </summary>
    private static uint CalculateWeakHash(byte[] data)
    {
        // Adler-32 style rolling hash
        const uint mod = 65521;
        uint a = 1, b = 0;

        foreach (var byte_ in data)
        {
            a = (a + byte_) % mod;
            b = (b + a) % mod;
        }

        return (b << 16) | a;
    }

    /// <summary>
    /// Calculates file hash from block hashes (Syncthing algorithm)
    /// </summary>
    private static string CalculateFileHash(List<BlockInfo> blocks)
    {
        if (blocks.Count == 0)
            return string.Empty;

        // Concatenate all block hashes and hash the result
        using var hasher = SHA256.Create();
        foreach (var block in blocks)
        {
            var blockHash = Convert.FromHexString(block.Hash);
            hasher.TransformBlock(blockHash, 0, blockHash.Length, null, 0);
        }
        hasher.TransformFinalBlock(Array.Empty<byte>(), 0, 0);

        return Convert.ToHexString(hasher.Hash!).ToLower();
    }

    /// <summary>
    /// Records upload statistics if collector is available
    /// </summary>
    private async Task RecordUploadStatisticsAsync(
        string deviceId,
        string folderId,
        string fileName,
        long bytesUploaded,
        long totalFileSize,
        int blocksTransferred,
        int blocksSkipped,
        bool isDeltaUpload,
        TimeSpan duration,
        bool success,
        string? error)
    {
        if (_statisticsCollector == null)
            return;

        try
        {
            await _statisticsCollector.RecordUploadAsync(new UploadRecordInfo
            {
                DeviceId = deviceId,
                FolderId = folderId,
                FileName = fileName,
                BytesUploaded = bytesUploaded,
                TotalFileSize = totalFileSize,
                BlocksTransferred = blocksTransferred,
                BlocksSkipped = blocksSkipped,
                IsDeltaUpload = isDeltaUpload,
                Duration = duration,
                Success = success,
                Error = error
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record upload statistics for {FileName}", fileName);
        }
    }
}

/// <summary>
/// Result of file upload operation
/// </summary>
public class UploadResult
{
    public string FileName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public List<string> Errors { get; set; } = new();
    public long BytesTransferred { get; set; }
    public int BlocksTransferred { get; set; }
    public TimeSpan Duration { get; set; }
}

/// <summary>
/// Result of file verification
/// </summary>
public class FileVerificationResult
{
    public bool IsValid { get; set; }
    public string Error { get; set; } = string.Empty;
}

/// <summary>
/// Result of delta upload operation
/// </summary>
public class DeltaUploadResult
{
    public string FileName { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long ChangedBytes { get; set; }
    public int TotalBlocks { get; set; }
    public int ChangedBlocks { get; set; }
    public int UnchangedBlocks { get; set; }
    public TimeSpan Duration { get; set; }
    public bool IsDeltaUpload { get; set; }

    /// <summary>
    /// Percentage of data that needs to be transferred (0-100)
    /// </summary>
    public double TransferPercentage => TotalBytes > 0 ? (ChangedBytes * 100.0 / TotalBytes) : 0;

    /// <summary>
    /// Bytes saved by delta upload compared to full upload
    /// </summary>
    public long BytesSaved => TotalBytes - ChangedBytes;
}

/// <summary>
/// Result of comparing blocks for delta upload
/// </summary>
public class DeltaBlockComparison
{
    /// <summary>
    /// Blocks that exist locally but not on remote - need to be transferred
    /// </summary>
    public List<BlockInfo> ChangedBlocks { get; set; } = new();

    /// <summary>
    /// Blocks that already exist on remote - no transfer needed
    /// </summary>
    public List<BlockInfo> UnchangedBlocks { get; set; } = new();

    /// <summary>
    /// Total bytes that need to be transferred
    /// </summary>
    public long ChangedBytes { get; set; }
}
