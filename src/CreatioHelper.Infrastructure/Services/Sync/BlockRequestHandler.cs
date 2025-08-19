using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Handles incoming block requests and sends file data
/// Based on Syncthing's block serving logic
/// </summary>
public class BlockRequestHandler
{
    private readonly ILogger<BlockRequestHandler> _logger;
    private readonly ISyncProtocol _protocol;
    private readonly Dictionary<string, SyncFolder> _folders = new();

    public BlockRequestHandler(ILogger<BlockRequestHandler> logger, ISyncProtocol protocol)
    {
        _logger = logger;
        _protocol = protocol;
    }

    /// <summary>
    /// Registers a folder for serving files
    /// </summary>
    public void RegisterFolder(SyncFolder folder)
    {
        _folders[folder.FolderId] = folder;
        _logger.LogDebug("Registered folder {FolderId} for block serving: {Path}", folder.FolderId, folder.Path);
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
    /// Handles incoming block request
    /// </summary>
    public async Task HandleBlockRequestAsync(string deviceId, BepRequest request)
    {
        _logger.LogInformation("🔥 BlockRequestHandler: Received block request {RequestId} from device {DeviceId}: {FileName} offset={Offset} size={Size}",
            request.Id, deviceId, request.Name, request.Offset, request.Size);

        var response = new BepResponse
        {
            Id = request.Id,
            Code = BepErrorCode.NoError,
            Data = Array.Empty<byte>()
        };

        try
        {
            _logger.LogTrace("Handling block request {RequestId} from device {DeviceId}: {FileName} offset={Offset} size={Size}",
                request.Id, deviceId, request.Name, request.Offset, request.Size);

            // Validate request
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
            if (!folder.Devices.Any(d => d.DeviceId == deviceId))
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

            // Check if file exists
            if (!File.Exists(filePath))
            {
                response.Code = BepErrorCode.NoSuchFile;
                _logger.LogDebug("Block request {RequestId} for non-existent file: {FilePath}", request.Id, filePath);
                await _protocol.SendBlockResponseAsync(deviceId, response);
                return;
            }

            // Read requested block
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
                }
            }

            response.Data = blockData;
            
            _logger.LogInformation("✅ BlockRequestHandler: Serving block {RequestId}: {BytesSent} bytes for {FileName} offset={Offset}",
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
    /// Validates block request parameters
    /// </summary>
    private (BepErrorCode ErrorCode, string Message)? ValidateRequest(BepRequest request)
    {
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