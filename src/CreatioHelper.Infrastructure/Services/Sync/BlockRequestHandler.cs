using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Handles incoming block requests and sends file data
/// Based on Syncthing's block serving logic with support for block-level deduplication
/// </summary>
public class BlockRequestHandler
{
    private readonly ILogger<BlockRequestHandler> _logger;
    private readonly ISyncProtocol _protocol;
    private readonly Dictionary<string, SyncFolder> _folders = new();
    private readonly SyncthingBlockStorage _blockStorage;
    private readonly IBlockInfoRepository _blockRepository;

    public BlockRequestHandler(
        ILogger<BlockRequestHandler> logger, 
        ISyncProtocol protocol,
        SyncthingBlockStorage blockStorage,
        IBlockInfoRepository blockRepository)
    {
        _logger = logger;
        _protocol = protocol;
        _blockStorage = blockStorage;
        _blockRepository = blockRepository;
    }

    /// <summary>
    /// Registers a folder for serving files
    /// </summary>
    public void RegisterFolder(SyncFolder folder)
    {
        _folders[folder.Id] = folder;
        _logger.LogDebug("Registered folder {FolderId} for block serving: {Path}", folder.Id, folder.Path);
    }

    /// <summary>
    /// Unregisters a folder
    /// </summary>
    public void UnregisterFolder(string folderId)
    {
        if (_folders.Remove(folderId))
        {
            _logger.LogDebug("Unregistered folder {FolderId} from block serving", folderId);
        }
    }

    /// <summary>
    /// Handles incoming block request with support for both file-based and hash-based requests
    /// </summary>
    public async Task HandleBlockRequestAsync(string deviceId, BepRequest request)
    {
        _logger.LogInformation("🔥 BlockRequestHandler: Received block request {RequestId} from device {DeviceId}: {FileName} offset={Offset} size={Size} hash={Hash}",
            request.Id, deviceId, request.Name, request.Offset, request.Size, 
            request.Hash?.Length > 0 ? Convert.ToHexString(request.Hash)[..8] + "..." : "none");

        var response = new BepResponse
        {
            Id = request.Id,
            Code = BepErrorCode.NoError,
            Data = Array.Empty<byte>()
        };

        try
        {
            // Check if this is a direct block request by hash (Syncthing-style deduplication)
            if (string.IsNullOrEmpty(request.Folder) && string.IsNullOrEmpty(request.Name) && 
                request.Hash != null && request.Hash.Length > 0)
            {
                _logger.LogDebug("Processing direct block hash request {RequestId} for hash {Hash}", 
                    request.Id, Convert.ToHexString(request.Hash)[..8] + "...");
                
                response.Data = await HandleDirectBlockRequestAsync(request.Hash);
                if (response.Data.Length == 0)
                {
                    response.Code = BepErrorCode.NoSuchFile;
                    _logger.LogWarning("Direct block request {RequestId}: Block not found for hash {Hash}",
                        request.Id, Convert.ToHexString(request.Hash)[..8] + "...");
                }
                else
                {
                    _logger.LogInformation("✅ BlockRequestHandler: Served direct block {RequestId}: {BytesSent} bytes for hash {Hash}",
                        request.Id, response.Data.Length, Convert.ToHexString(request.Hash)[..8] + "...");
                }
                
                await _protocol.SendBlockResponseAsync(deviceId, response);
                return;
            }

            // Standard file-based block request validation
            var validationError = ValidateRequest(request);
            if (validationError != null)
            {
                response.Code = validationError.Value.ErrorCode;
                _logger.LogWarning("Invalid block request {RequestId}: {Error}", request.Id, validationError.Value.Message);
                await _protocol.SendBlockResponseAsync(deviceId, response);
                return;
            }

            // Find folder
            if (!_folders.TryGetValue(request.Folder, out var folder))
            {
                response.Code = BepErrorCode.NoSuchFile;
                _logger.LogWarning("Block request {RequestId} for unknown folder {FolderId}", request.Id, request.Folder);
                await _protocol.SendBlockResponseAsync(deviceId, response);
                return;
            }

            // Check if device is authorized for this folder
            if (!folder.Devices.Contains(deviceId))
            {
                response.Code = BepErrorCode.Generic;
                _logger.LogWarning("Unauthorized block request {RequestId} from device {DeviceId} for folder {FolderId}", 
                    request.Id, deviceId, request.Folder);
                await _protocol.SendBlockResponseAsync(deviceId, response);
                return;
            }

            // Build file path
            var filePath = Path.Combine(folder.Path, request.Name.Replace('/', Path.DirectorySeparatorChar));
            filePath = Path.GetFullPath(filePath);

            // Security check - ensure file is within folder
            var folderPath = Path.GetFullPath(folder.Path);
            if (!filePath.StartsWith(folderPath))
            {
                response.Code = BepErrorCode.Generic;
                _logger.LogWarning("Security violation: block request {RequestId} trying to access file outside folder: {FilePath}", 
                    request.Id, filePath);
                await _protocol.SendBlockResponseAsync(deviceId, response);
                return;
            }

            // Try to serve from block storage first if hash is provided
            if (request.Hash != null && request.Hash.Length > 0)
            {
                var cachedBlock = await _blockStorage.GetBlockAsync(request.Hash);
                if (cachedBlock != null)
                {
                    response.Data = cachedBlock;
                    _logger.LogInformation("✅ BlockRequestHandler: Served cached block {RequestId}: {BytesSent} bytes from block storage",
                        request.Id, cachedBlock.Length);
                    await _protocol.SendBlockResponseAsync(deviceId, response);
                    return;
                }
            }

            // Fallback to file-based serving
            if (!File.Exists(filePath))
            {
                response.Code = BepErrorCode.NoSuchFile;
                _logger.LogDebug("Block request {RequestId} for non-existent file: {FilePath}", request.Id, filePath);
                await _protocol.SendBlockResponseAsync(deviceId, response);
                return;
            }

            // Read requested block from file
            var blockData = await ReadFileBlockAsync(filePath, request.Offset, request.Size);
            
            // Verify hash if provided (strict validation like original Syncthing)
            if (request.Hash != null && request.Hash.Length > 0)
            {
                var actualHash = System.Security.Cryptography.SHA256.HashData(blockData);
                if (!actualHash.SequenceEqual(request.Hash))
                {
                    var actualHashHex = Convert.ToHexString(actualHash).ToLower();
                    var expectedHashHex = Convert.ToHexString(request.Hash).ToLower();
                    
                    _logger.LogError("❌ BlockRequestHandler: Block hash mismatch for request {RequestId}: {FileName} offset={Offset} - expected {ExpectedHash}, got {ActualHash}", 
                        request.Id, request.Name, request.Offset, expectedHashHex, actualHashHex);
                    
                    response.Code = BepErrorCode.Generic;
                    await _protocol.SendBlockResponseAsync(deviceId, response);
                    return;
                }
                else
                {
                    _logger.LogDebug("✅ BlockRequestHandler: Block hash matches for request {RequestId}", request.Id);
                    
                    // Store verified block in block storage for future deduplication
                    _ = Task.Run(async () => {
                        try
                        {
                            await _blockStorage.StoreBlockAsync(blockData, request.Hash);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to store block in block storage for hash {Hash}", 
                                Convert.ToHexString(request.Hash)[..8] + "...");
                        }
                    });
                }
            }

            response.Data = blockData;
            
            _logger.LogInformation("✅ BlockRequestHandler: Serving file block {RequestId}: {BytesSent} bytes for {FileName} offset={Offset}",
                request.Id, blockData.Length, request.Name, request.Offset);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ BlockRequestHandler: Error handling block request {RequestId} from device {DeviceId}: {Error}",
                request.Id, deviceId, ex.Message);
            
            response.Code = BepErrorCode.Generic;
            response.Data = Array.Empty<byte>();
        }

        _logger.LogInformation("📤 BlockRequestHandler: Sending response for request {RequestId} with code {Code} and {DataSize} bytes",
            request.Id, response.Code, response.Data.Length);
        await _protocol.SendBlockResponseAsync(deviceId, response);
    }

    /// <summary>
    /// Handles direct block request by hash (Syncthing-compatible deduplication)
    /// </summary>
    private async Task<byte[]> HandleDirectBlockRequestAsync(byte[] blockHash)
    {
        try
        {
            // First try block storage
            var blockData = await _blockStorage.GetBlockAsync(blockHash);
            if (blockData != null)
            {
                _logger.LogDebug("Served direct block from block storage for hash {Hash}", 
                    Convert.ToHexString(blockHash)[..8] + "...");
                return blockData;
            }

            // If not in block storage, try to find in repository and locate source file
            var blockMetadata = await _blockRepository.GetAsync(blockHash);
            if (blockMetadata != null)
            {
                // Try to find the source file and extract the block
                if (_folders.TryGetValue(blockMetadata.FolderId, out var folder))
                {
                    var filePath = Path.Combine(folder.Path, blockMetadata.FileName);
                    if (File.Exists(filePath))
                    {
                        // Calculate block offset based on block index and typical block size
                        var calculator = new SyncthingBlockCalculator(_logger as ILogger<SyncthingBlockCalculator> ?? throw new InvalidOperationException("Invalid logger type"));
                        var blockSize = calculator.CalculateBlockSize(new FileInfo(filePath).Length);
                        var blockOffset = blockMetadata.BlockIndex * (long)blockSize;
                        
                        var extractedBlock = await ReadFileBlockAsync(filePath, blockOffset, blockMetadata.Size);
                        
                        // Verify the extracted block matches the requested hash
                        var extractedHash = System.Security.Cryptography.SHA256.HashData(extractedBlock);
                        if (extractedHash.SequenceEqual(blockHash))
                        {
                            // Store in block storage for future requests
                            _ = Task.Run(async () => {
                                try
                                {
                                    await _blockStorage.StoreBlockAsync(extractedBlock, blockHash);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "Failed to store extracted block in block storage");
                                }
                            });
                            
                            _logger.LogDebug("Served direct block extracted from file {FileName} for hash {Hash}", 
                                blockMetadata.FileName, Convert.ToHexString(blockHash)[..8] + "...");
                            return extractedBlock;
                        }
                        else
                        {
                            _logger.LogWarning("Block hash mismatch when extracting from file {FileName}", blockMetadata.FileName);
                        }
                    }
                }
            }

            _logger.LogDebug("Direct block not found for hash {Hash}", Convert.ToHexString(blockHash)[..8] + "...");
            return Array.Empty<byte>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling direct block request for hash {Hash}", 
                Convert.ToHexString(blockHash)[..8] + "...");
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Validates block request parameters
    /// </summary>
    private (BepErrorCode ErrorCode, string Message)? ValidateRequest(BepRequest request)
    {
        // Allow direct block requests without folder/name
        if (string.IsNullOrEmpty(request.Folder) && string.IsNullOrEmpty(request.Name) && 
            request.Hash != null && request.Hash.Length > 0)
        {
            return null; // Direct block request is valid
        }

        if (string.IsNullOrEmpty(request.Folder))
            return (BepErrorCode.Generic, "Missing folder ID");

        if (string.IsNullOrEmpty(request.Name))
            return (BepErrorCode.Generic, "Missing file name");

        if (request.Offset < 0)
            return (BepErrorCode.Generic, "Invalid offset");

        if (request.Size <= 0)
            return (BepErrorCode.Generic, "Invalid size");

        if (request.Size > 16 * 1024 * 1024) // 16MB max block size
            return (BepErrorCode.Generic, "Block too large");

        // Check for directory traversal attacks
        if (request.Name.Contains("..") || request.Name.Contains('\\'))
            return (BepErrorCode.Generic, "Invalid file name");

        return null;
    }

    /// <summary>
    /// Reads a block of data from file
    /// </summary>
    private async Task<byte[]> ReadFileBlockAsync(string filePath, long offset, int size)
    {
        using var file = File.OpenRead(filePath);
        
        // Check bounds
        if (offset >= file.Length)
            return Array.Empty<byte>();

        // Adjust size if it extends beyond file
        var actualSize = (int)Math.Min(size, file.Length - offset);
        
        var buffer = new byte[actualSize];
        file.Seek(offset, SeekOrigin.Begin);
        var bytesRead = await file.ReadAsync(buffer.AsMemory(0, actualSize));
        
        if (bytesRead != actualSize)
        {
            // Return only the bytes actually read
            var result = new byte[bytesRead];
            Array.Copy(buffer, result, bytesRead);
            return result;
        }
        
        return buffer;
    }
}

/// <summary>
/// Error information for request validation
/// </summary>
public record RequestValidationError(BepErrorCode ErrorCode, string Message);