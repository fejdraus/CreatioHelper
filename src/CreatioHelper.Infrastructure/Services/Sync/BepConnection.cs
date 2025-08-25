using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using K4os.Compression.LZ4;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// BEP Connection handling exact Syncthing wire format with compression and encryption support
/// Wire format: [2 bytes: header length][Header: protobuf][4 bytes: message length][Message: protobuf]
/// </summary>
public class BepConnection : IDisposable
{
    private string _deviceId;
    private readonly TcpClient _tcpClient;
    private readonly Stream _stream;
    private readonly ILogger _logger;
    private readonly CompressionEngine _compressionEngine;
    private readonly EncryptionEngine _encryptionEngine;
    private readonly KeyManager _keyManager;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<BepResponse>> _pendingRequests = new();
    private volatile bool _isConnected = true;
    private volatile bool _magicSent = false;
    private readonly bool _isOutgoing; // true if we initiated the connection (client), false if incoming (server)

    // BEP Constants
    private const uint BepMagic = 0x2EA7D90B;
    private const int CompressionThreshold = 128;
    private const int MaxMessageSize = 1024 * 1024 * 16; // 16MB

    public string DeviceId => _deviceId;
    public bool IsConnected => _isConnected && _tcpClient.Connected;

    // Events for message processing
    public event EventHandler<(BepMessageType Type, object Message)>? MessageReceived;
    public event EventHandler<(string OldDeviceId, string NewDeviceId)>? DeviceIdUpdated;

    public BepConnection(string deviceId, TcpClient tcpClient, Stream stream, ILogger logger, CompressionEngine compressionEngine, EncryptionEngine encryptionEngine, KeyManager keyManager, bool isOutgoing = false)
    {
        _deviceId = deviceId;
        _tcpClient = tcpClient;
        _stream = stream;
        _logger = logger;
        _compressionEngine = compressionEngine;
        _encryptionEngine = encryptionEngine;
        _keyManager = keyManager;
        _isOutgoing = isOutgoing;
    }

    public Task StartAsync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ReceiveLoopAsync(_cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in BEP receive loop for device {DeviceId}", _deviceId);
                await DisconnectAsync();
            }
        });
        
        return Task.CompletedTask;
    }

    public async Task SendHelloAsync(string deviceId, string deviceName, string clientName, string clientVersion)
    {
        var hello = new BepHello
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            ClientName = clientName,
            ClientVersion = clientVersion
        };

        await SendMessageAsync(BepMessageType.Hello, hello);
    }

    public async Task SendMessageAsync<T>(BepMessageType messageType, T message)
    {
        if (!IsConnected) 
        {
            _logger.LogWarning("🚫 BepConnection: Cannot send {MessageType} to device {DeviceId} - not connected", messageType, _deviceId);
            return;
        }

        // Add extra logging for Request messages
        if (messageType == BepMessageType.Request && message is BepRequest request)
        {
            _logger.LogInformation("📨 BepConnection: Preparing to send block request {RequestId} for {FileName} to device {DeviceId}",
                request.Id, request.Name, _deviceId);
        }

        await _sendSemaphore.WaitAsync();
        try
        {
            // Send BEP magic number only for Hello message (first message from client)
            if (messageType == BepMessageType.Hello && !_magicSent)
            {
                var magicBytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(magicBytes, BepMagic);
                await _stream.WriteAsync(magicBytes);
                _magicSent = true;
                _logger.LogDebug("Sent BEP magic number 0x{Magic:X8} to device {DeviceId}", BepMagic, _deviceId);
            }
            // Serialize message
            var json = JsonSerializer.Serialize(message);
            var messageData = Encoding.UTF8.GetBytes(json);
            
            if (messageType == BepMessageType.Request && message is BepRequest req)
            {
                _logger.LogInformation("🔧 BepConnection: Serialized block request {RequestId} - JSON size: {JsonSize}", 
                    req.Id, messageData.Length);
            }
            
            // Check if compression should be used
            var useCompression = messageData.Length >= CompressionThreshold;
            var compression = useCompression ? BepCompression.Always : BepCompression.None;
            
            // Compress if needed
            if (useCompression)
            {
                var compressed = LZ4Pickler.Pickle(messageData);
                var compressionRatio = (double)compressed.Length / messageData.Length;
                
                // Only use compression if it saves at least 3.125% (Syncthing threshold)
                if (compressionRatio < 0.96875)
                {
                    messageData = compressed;
                }
                else
                {
                    compression = BepCompression.None;
                }
            }

            // Create header
            var header = new BepHeader
            {
                Type = messageType,
                Compression = compression
            };

            var headerJson = JsonSerializer.Serialize(header);
            var headerData = Encoding.UTF8.GetBytes(headerJson);

            if (messageType == BepMessageType.Request && message is BepRequest req2)
            {
                _logger.LogInformation("📋 BepConnection: Header for block request {RequestId} - size: {HeaderSize}, compression: {Compression}",
                    req2.Id, headerData.Length, compression);
            }

            // Write header length (2 bytes, big endian)
            var headerLengthBytes = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(headerLengthBytes, (ushort)headerData.Length);
            await _stream.WriteAsync(headerLengthBytes);

            // Write header
            await _stream.WriteAsync(headerData);

            // Write message length (4 bytes, big endian)
            var messageLengthBytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(messageLengthBytes, (uint)messageData.Length);
            await _stream.WriteAsync(messageLengthBytes);

            // Write message
            await _stream.WriteAsync(messageData);
            await _stream.FlushAsync();

            if (messageType == BepMessageType.Request && message is BepRequest req3)
            {
                _logger.LogInformation("✈️ BepConnection: Successfully sent block request {RequestId} to device {DeviceId} - total bytes written",
                    req3.Id, _deviceId);
            }
            else
            {
                _logger.LogDebug("Sent BEP {MessageType} message to device {DeviceId}, size: {Size}, compressed: {Compressed}",
                    messageType, _deviceId, messageData.Length, compression != BepCompression.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending BEP message to device {DeviceId}", _deviceId);
            await DisconnectAsync();
        }
        finally
        {
            _sendSemaphore.Release();
        }
    }

    public async Task<BepResponse> RequestBlockAsync(BepRequest request)
    {
        _logger.LogInformation("🚀 BepConnection: Requesting block {RequestId} for {FileName} from device {DeviceId} - offset={Offset} size={Size}",
            request.Id, request.Name, _deviceId, request.Offset, request.Size);

        var tcs = new TaskCompletionSource<BepResponse>();
        _pendingRequests[request.Id] = tcs;

        try
        {
            _logger.LogInformation("📤 BepConnection: Sending block request {RequestId} to device {DeviceId}",
                request.Id, _deviceId);
            
            await SendMessageAsync(BepMessageType.Request, request);
            
            _logger.LogInformation("⏳ BepConnection: Waiting for block response {RequestId} from device {DeviceId}",
                request.Id, _deviceId);
            
            // Wait for response with timeout
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var response = await tcs.Task.WaitAsync(cts.Token);
            
            _logger.LogInformation("✅ BepConnection: Received block response {RequestId} from device {DeviceId} - data size: {DataSize}",
                request.Id, _deviceId, response.Data?.Length ?? 0);
            
            return response;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("⏰ BepConnection: Block request {RequestId} to device {DeviceId} timed out",
                request.Id, _deviceId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ BepConnection: Error requesting block {RequestId} from device {DeviceId}",
                request.Id, _deviceId);
            throw;
        }
        finally
        {
            _pendingRequests.TryRemove(request.Id, out _);
        }
    }

    /// <summary>
    /// Send a block response to a request with compression and encryption support
    /// </summary>
    public async Task SendBlockResponseAsync(BepResponse response)
    {
        var originalSize = response.Data?.Length ?? 0;
        
        _logger.LogInformation("📤 BepConnection: Sending block response {ResponseId} to device {DeviceId} - original data size: {DataSize}",
            response.Id, _deviceId, originalSize);
        
        // Apply processing pipeline: Compression → Encryption
        if (response.Data != null && response.Data.Length > 0)
        {
            // Step 1: Compression
            var (compressedData, actualCompressionType) = _compressionEngine.CompressBlock(response.Data, CompressionType.LZ4);
            
            if (actualCompressionType != CompressionType.None)
            {
                response.Data = compressedData;
                response.CompressionType = actualCompressionType;
                
                var compressionRatio = (float)compressedData.Length / originalSize;
                _logger.LogInformation("🗜️ BepConnection: Block compressed: {OriginalSize} → {CompressedSize} ({Ratio:P1}) using {CompressionType}",
                    originalSize, compressedData.Length, compressionRatio, actualCompressionType);
            }
            else
            {
                response.CompressionType = CompressionType.None;
                _logger.LogDebug("📦 BepConnection: Block not compressed - using original data");
            }
            
            // Step 2: Encryption
            try
            {
                // Check if encryption should be applied BEFORE attempting encryption
                if (_encryptionEngine.ShouldEncrypt(response.Data))
                {
                    // TODO: Get actual folder ID and password from context
                    // For now, use hardcoded values for testing
                    var encryptionKey = await _keyManager.GetOrCreateFolderKeyAsync("default", "test-folder-password-123");
                    var (encryptedData, isEncrypted) = _encryptionEngine.EncryptBlock(response.Data, encryptionKey);
                    
                    if (isEncrypted)
                    {
                        var preEncryptionSize = response.Data.Length;
                        response.Data = encryptedData;
                        response.EncryptionType = EncryptionType.AES256GCM;
                        
                        _logger.LogInformation("🔐 BepConnection: Block encrypted: {PreEncryptionSize} → {EncryptedSize} using AES-256-GCM",
                            preEncryptionSize, encryptedData.Length);
                    }
                    else
                    {
                        response.EncryptionType = EncryptionType.None;
                        _logger.LogDebug("🔓 BepConnection: Block encryption failed - using unencrypted data");
                    }
                }
                else
                {
                    response.EncryptionType = EncryptionType.None;
                    _logger.LogDebug("🔓 BepConnection: Block not encrypted - encryption disabled");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ BepConnection: Encryption failed for device {DeviceId}, sending unencrypted", _deviceId);
                response.EncryptionType = EncryptionType.None;
            }
        }
        
        await SendMessageAsync(BepMessageType.Response, response);
        
        _logger.LogInformation("✅ BepConnection: Block response {ResponseId} sent to device {DeviceId} (compressed: {Compressed}, encrypted: {Encrypted})",
            response.Id, _deviceId, response.CompressionType != CompressionType.None, response.EncryptionType != EncryptionType.None);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        // Read magic number only for incoming connections (we are server)
        if (!_isOutgoing)
        {
            var magicBytes = new byte[4];
            await ReadExactAsync(magicBytes, cancellationToken);
            var magic = BinaryPrimitives.ReadUInt32BigEndian(magicBytes);
            
            if (magic != BepMagic)
            {
                _logger.LogError("Invalid BEP magic number from device {DeviceId}: 0x{Magic:X8}", _deviceId, magic);
                return;
            }
            _logger.LogDebug("Received valid BEP magic number 0x{Magic:X8} from device {DeviceId}", magic, _deviceId);
        }

        while (!cancellationToken.IsCancellationRequested && IsConnected)
        {
            try
            {
                // Read header length (2 bytes, big endian)
                var headerLengthBytes = new byte[2];
                await ReadExactAsync(headerLengthBytes, cancellationToken);
                var headerLength = BinaryPrimitives.ReadUInt16BigEndian(headerLengthBytes);

                if (headerLength > 1024) // Reasonable header size limit
                {
                    _logger.LogError("Header too large from device {DeviceId}: {Length} bytes", _deviceId, headerLength);
                    return;
                }

                // Read header
                var headerData = new byte[headerLength];
                await ReadExactAsync(headerData, cancellationToken);

                var headerJson = Encoding.UTF8.GetString(headerData);
                var header = JsonSerializer.Deserialize<BepHeader>(headerJson);
                if (header == null)
                {
                    _logger.LogError("Failed to deserialize header from device {DeviceId}", _deviceId);
                    return;
                }

                // Read message length (4 bytes, big endian)
                var messageLengthBytes = new byte[4];
                await ReadExactAsync(messageLengthBytes, cancellationToken);
                var messageLength = BinaryPrimitives.ReadUInt32BigEndian(messageLengthBytes);

                if (messageLength > MaxMessageSize)
                {
                    _logger.LogError("Message too large from device {DeviceId}: {Length} bytes", _deviceId, messageLength);
                    return;
                }

                // Read message data
                var messageData = new byte[messageLength];
                await ReadExactAsync(messageData, cancellationToken);

                // Decompress if needed
                if (header.Compression == BepCompression.Always)
                {
                    try
                    {
                        messageData = LZ4Pickler.Unpickle(messageData);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to decompress message from device {DeviceId}", _deviceId);
                        return;
                    }
                }

                await ProcessMessageAsync(header.Type, messageData);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error receiving BEP message from device {DeviceId}", _deviceId);
                break;
            }
        }
    }

    private async Task ReadExactAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException($"Connection closed by device {_deviceId}");
            }
            totalRead += read;
        }
    }

    private async Task ProcessMessageAsync(BepMessageType messageType, byte[] data)
    {
        try
        {
            var json = Encoding.UTF8.GetString(data);

            switch (messageType)
            {
                case BepMessageType.Hello:
                    var hello = JsonSerializer.Deserialize<BepHello>(json);
                    
                    // Update device ID from Hello message (when TLS is disabled for testing)
                    if (!string.IsNullOrEmpty(hello?.DeviceId) && _deviceId == "unknown-device")
                    {
                        var oldDeviceId = _deviceId;
                        _deviceId = hello.DeviceId;
                        _logger.LogInformation("Device ID updated from Hello message: {OldDeviceId} -> {NewDeviceId}", oldDeviceId, _deviceId);
                        
                        // Notify about device ID change
                        DeviceIdUpdated?.Invoke(this, (oldDeviceId, _deviceId));
                    }
                    
                    _logger.LogInformation("Received BEP Hello from device {DeviceId}: {DeviceName} ({ClientName} {ClientVersion})",
                        _deviceId, hello?.DeviceName, hello?.ClientName, hello?.ClientVersion);
                    break;

                case BepMessageType.ClusterConfig:
                    var config = JsonSerializer.Deserialize<BepClusterConfig>(json);
                    _logger.LogDebug("Received BEP ClusterConfig from device {DeviceId} with {FolderCount} folders",
                        _deviceId, config?.Folders.Count ?? 0);
                    break;

                case BepMessageType.Index:
                    var index = JsonSerializer.Deserialize<BepIndex>(json);
                    _logger.LogDebug("Received BEP Index from device {DeviceId} for folder {FolderId} with {FileCount} files",
                        _deviceId, index?.Folder, index?.Files.Count ?? 0);
                    if (index != null)
                        MessageReceived?.Invoke(this, (messageType, index));
                    break;

                case BepMessageType.IndexUpdate:
                    var indexUpdate = JsonSerializer.Deserialize<BepIndexUpdate>(json);
                    _logger.LogDebug("Received BEP IndexUpdate from device {DeviceId} for folder {FolderId} with {FileCount} files",
                        _deviceId, indexUpdate?.Folder, indexUpdate?.Files.Count ?? 0);
                    if (indexUpdate != null)
                        MessageReceived?.Invoke(this, (messageType, indexUpdate));
                    break;

                case BepMessageType.Request:
                    var request = JsonSerializer.Deserialize<BepRequest>(json);
                    _logger.LogInformation("🔥 BepConnection: Received block request {RequestId} from device {DeviceId} for {FileName} - offset={Offset} size={Size}",
                        request?.Id, _deviceId, request?.Name, request?.Offset, request?.Size);
                    if (request != null)
                    {
                        _logger.LogInformation("📢 BepConnection: Triggering MessageReceived event for block request {RequestId}", request.Id);
                        MessageReceived?.Invoke(this, (messageType, request));
                    }
                    else
                    {
                        _logger.LogError("❌ BepConnection: Failed to deserialize block request from device {DeviceId}", _deviceId);
                    }
                    break;

                case BepMessageType.Response:
                    var response = JsonSerializer.Deserialize<BepResponse>(json);
                    var receivedSize = response?.Data?.Length ?? 0;
                    
                    _logger.LogInformation("📨 BepConnection: Received block response {ResponseId} from device {DeviceId} - data size: {DataSize}, compression: {CompressionType}, encryption: {EncryptionType}",
                        response?.Id, _deviceId, receivedSize, response?.CompressionType ?? CompressionType.None, response?.EncryptionType ?? EncryptionType.None);
                    
                    // Process pipeline: Decryption → Decompression
                    if (response != null && response.Data != null && response.Data.Length > 0)
                    {
                        // Step 1: Decryption
                        if (response.EncryptionType != EncryptionType.None)
                        {
                            try
                            {
                                // TODO: Get actual folder ID and password from context
                    // For now, use hardcoded values for testing
                    var encryptionKey = await _keyManager.GetOrCreateFolderKeyAsync("default", "test-folder-password-123");
                                var decryptedData = _encryptionEngine.DecryptBlock(response.Data, encryptionKey);
                                
                                _logger.LogInformation("🔓 BepConnection: Block decrypted: {EncryptedSize} → {DecryptedSize} using AES-256-GCM",
                                    response.Data.Length, decryptedData.Length);
                                
                                response.Data = decryptedData;
                                response.EncryptionType = EncryptionType.None;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "❌ BepConnection: Failed to decrypt block response {ResponseId} from device {DeviceId}",
                                    response.Id, _deviceId);
                                // Continue with encrypted data - let the caller handle the error
                            }
                        }
                        
                        // Step 2: Decompression
                        if (response.CompressionType != CompressionType.None)
                        {
                            try
                            {
                                var preDecompressionSize = response.Data.Length;
                                var decompressedData = _compressionEngine.DecompressBlock(response.Data, response.CompressionType);
                                response.Data = decompressedData;
                                
                                _logger.LogInformation("🗜️ BepConnection: Block decompressed: {CompressedSize} → {DecompressedSize} using {CompressionType}",
                                    preDecompressionSize, decompressedData.Length, response.CompressionType);
                                
                                // Reset compression type to indicate data is now uncompressed
                                response.CompressionType = CompressionType.None;
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "❌ BepConnection: Failed to decompress block response {ResponseId} from device {DeviceId}",
                                    response.Id, _deviceId);
                                // Continue with compressed data - let the caller handle the error
                            }
                        }
                    }
                    
                    if (response != null && _pendingRequests.TryRemove(response.Id, out var tcs))
                    {
                        _logger.LogInformation("✅ BepConnection: Found pending request for response {ResponseId}, completing task", response.Id);
                        tcs.SetResult(response);
                    }
                    else if (response != null)
                    {
                        _logger.LogWarning("⚠️ BepConnection: No pending request found for response {ResponseId} from device {DeviceId}", 
                            response.Id, _deviceId);
                    }
                    else
                    {
                        _logger.LogError("❌ BepConnection: Failed to deserialize block response from device {DeviceId}", _deviceId);
                    }
                    break;

                case BepMessageType.DownloadProgress:
                    var progress = JsonSerializer.Deserialize<BepDownloadProgress>(json);
                    _logger.LogDebug("Received BEP download progress from device {DeviceId} for folder {FolderId}",
                        _deviceId, progress?.Folder);
                    if (progress != null)
                        MessageReceived?.Invoke(this, (messageType, progress));
                    break;

                case BepMessageType.Ping:
                    // Respond with pong
                    await SendMessageAsync(BepMessageType.Ping, new BepPing());
                    break;

                case BepMessageType.Close:
                    var close = JsonSerializer.Deserialize<BepClose>(json);
                    _logger.LogInformation("Received BEP close from device {DeviceId}: {Reason}",
                        _deviceId, close?.Reason);
                    await DisconnectAsync();
                    break;

                default:
                    _logger.LogWarning("Unknown BEP message type {MessageType} from device {DeviceId}", messageType, _deviceId);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing BEP message type {MessageType} from device {DeviceId}", messageType, _deviceId);
        }
    }

    // Block request handling removed - now handled via MessageReceived event -> BlockRequestHandler

    public async Task DisconnectAsync()
    {
        if (!_isConnected) return;

        _isConnected = false;
        _cancellationTokenSource.Cancel();

        try
        {
            // Send close message
            await SendMessageAsync(BepMessageType.Close, new BepClose { Reason = "Connection closed" });
        }
        catch
        {
            // Ignore errors when closing
        }

        // Cancel all pending requests
        foreach (var pending in _pendingRequests.Values)
        {
            pending.SetCanceled();
        }
        _pendingRequests.Clear();

        _stream.Dispose();
        _tcpClient.Dispose();

        _logger.LogInformation("Disconnected BEP connection from device {DeviceId}", _deviceId);
    }

    public void Dispose()
    {
        _ = DisconnectAsync();
        _cancellationTokenSource.Dispose();
        _sendSemaphore.Dispose();
    }
}