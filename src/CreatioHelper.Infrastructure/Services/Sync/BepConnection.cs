using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CreatioHelper.Infrastructure.Services.Sync.Proto;
using Google.Protobuf;
using K4os.Compression.LZ4;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Serialization mode for BEP messages.
/// </summary>
public enum BepSerializationMode
{
    /// <summary>
    /// JSON serialization (legacy, for testing between CreatioHelper instances).
    /// </summary>
    Json,

    /// <summary>
    /// Protobuf serialization (native Syncthing wire compatibility).
    /// </summary>
    Protobuf
}

/// <summary>
/// BEP Connection handling exact Syncthing wire format.
/// Supports both JSON (legacy) and Protobuf (native Syncthing) serialization.
/// Wire format: [2 bytes: header length][Header: protobuf][4 bytes: message length][Message: protobuf]
/// </summary>
public class BepConnection : IDisposable
{
    private string _deviceId;
    private readonly TcpClient _tcpClient;
    private readonly Stream _stream;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly SemaphoreSlim _sendSemaphore = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<BepResponse>> _pendingRequests = new();
    private volatile bool _isConnected = true;
    private volatile bool _magicSent = false;
    private readonly bool _isOutgoing; // true if we initiated the connection (client), false if incoming (server)
    private readonly BepSerializationMode _serializationMode;
    private readonly BepProtobufSerializer _protobufSerializer;

    // BEP Constants
    private const uint BepMagic = 0x2EA7D90B;
    private const int CompressionThreshold = 128;
    private const int MaxMessageSize = 1024 * 1024 * 16; // 16MB

    public string DeviceId => _deviceId;
    public bool IsConnected => _isConnected && _tcpClient.Connected;
    public BepSerializationMode SerializationMode => _serializationMode;

    // Events for message processing
    public event EventHandler<(BepMessageType Type, object Message)>? MessageReceived;
    public event EventHandler<(string OldDeviceId, string NewDeviceId)>? DeviceIdUpdated;

    public BepConnection(string deviceId, TcpClient tcpClient, Stream stream, ILogger logger, bool isOutgoing = false, BepSerializationMode serializationMode = BepSerializationMode.Protobuf)
    {
        _deviceId = deviceId;
        _tcpClient = tcpClient;
        _stream = stream;
        _logger = logger;
        _isOutgoing = isOutgoing;
        _serializationMode = serializationMode;
        _protobufSerializer = new BepProtobufSerializer(logger as ILogger<BepProtobufSerializer>);
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
            _logger.LogWarning("BepConnection: Cannot send {MessageType} to device {DeviceId} - not connected", messageType, _deviceId);
            return;
        }

        // Add extra logging for Request messages
        if (messageType == BepMessageType.Request && message is BepRequest request)
        {
            _logger.LogDebug("BepConnection: Preparing to send block request {RequestId} for {FileName} to device {DeviceId}",
                request.Id, request.Name, _deviceId);
        }

        await _sendSemaphore.WaitAsync();
        try
        {
            if (_serializationMode == BepSerializationMode.Protobuf)
            {
                await SendMessageProtobufAsync(messageType, message);
            }
            else
            {
                await SendMessageJsonAsync(messageType, message);
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

    private async Task SendMessageProtobufAsync<T>(BepMessageType messageType, T message)
    {
        // Handle Hello message specially (includes magic prefix)
        if (messageType == BepMessageType.Hello && message is BepHello bepHello)
        {
            var protoHello = BepMessageConverter.ToProto(bepHello);
            var helloData = _protobufSerializer.SerializeHello(protoHello);
            await _stream.WriteAsync(helloData);
            await _stream.FlushAsync();
            _magicSent = true;
            _logger.LogDebug("Sent BEP Hello (Protobuf) to device {DeviceId}", _deviceId);
            return;
        }

        // Convert to Protobuf message and serialize
        var (protoMessage, protoMessageType) = ConvertToProto(messageType, message);
        var wireData = _protobufSerializer.SerializeMessage(protoMessage, protoMessageType, allowCompression: true);
        await _stream.WriteAsync(wireData);
        await _stream.FlushAsync();

        _logger.LogDebug("Sent BEP {MessageType} (Protobuf) to device {DeviceId}, size: {Size}",
            messageType, _deviceId, wireData.Length);
    }

    private (IMessage protoMessage, Proto.MessageType protoType) ConvertToProto<T>(BepMessageType messageType, T message)
    {
        return messageType switch
        {
            BepMessageType.ClusterConfig when message is BepClusterConfig config =>
                (BepMessageConverter.ToProto(config), Proto.MessageType.ClusterConfig),
            BepMessageType.Index when message is BepIndex index =>
                (BepMessageConverter.ToProto(index), Proto.MessageType.Index),
            BepMessageType.IndexUpdate when message is BepIndexUpdate indexUpdate =>
                (BepMessageConverter.ToProto(indexUpdate), Proto.MessageType.IndexUpdate),
            BepMessageType.Request when message is BepRequest request =>
                (BepMessageConverter.ToProto(request), Proto.MessageType.Request),
            BepMessageType.Response when message is BepResponse response =>
                (BepMessageConverter.ToProto(response), Proto.MessageType.Response),
            BepMessageType.DownloadProgress when message is BepDownloadProgress progress =>
                (BepMessageConverter.ToProto(progress), Proto.MessageType.DownloadProgress),
            BepMessageType.Ping =>
                (BepMessageConverter.ToProtoPing(), Proto.MessageType.Ping),
            BepMessageType.Close when message is BepClose close =>
                (BepMessageConverter.ToProto(close), Proto.MessageType.Close),
            _ => throw new InvalidOperationException($"Unsupported message type for Protobuf: {messageType}")
        };
    }

    private async Task SendMessageJsonAsync<T>(BepMessageType messageType, T message)
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

        _logger.LogDebug("Sent BEP {MessageType} (JSON) to device {DeviceId}, size: {Size}, compressed: {Compressed}",
            messageType, _deviceId, messageData.Length, compression != BepCompression.None);
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
    /// Send a block response to a request
    /// </summary>
    public async Task SendBlockResponseAsync(BepResponse response)
    {
        _logger.LogInformation("📤 BepConnection: Sending block response {ResponseId} to device {DeviceId} - data size: {DataSize}",
            response.Id, _deviceId, response.Data?.Length ?? 0);
        
        await SendMessageAsync(BepMessageType.Response, response);
        
        _logger.LogInformation("✅ BepConnection: Block response {ResponseId} sent to device {DeviceId}",
            response.Id, _deviceId);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_serializationMode == BepSerializationMode.Protobuf)
        {
            await ReceiveLoopProtobufAsync(cancellationToken);
        }
        else
        {
            await ReceiveLoopJsonAsync(cancellationToken);
        }
    }

    private async Task ReceiveLoopProtobufAsync(CancellationToken cancellationToken)
    {
        // For incoming connections (server), read Hello with magic first
        if (!_isOutgoing)
        {
            try
            {
                var hello = await _protobufSerializer.ReadHelloAsync(_stream, cancellationToken);
                var bepHello = BepMessageConverter.FromProto(hello);

                // Update device ID from Hello message
                if (!string.IsNullOrEmpty(hello.DeviceName) && _deviceId == "unknown-device")
                {
                    var oldDeviceId = _deviceId;
                    // In Protobuf mode, device ID comes from TLS certificate, not Hello
                    _logger.LogInformation("Received BEP Hello (Protobuf) from {DeviceName} ({ClientName} {ClientVersion})",
                        hello.DeviceName, hello.ClientName, hello.ClientVersion);
                }
                else
                {
                    _logger.LogInformation("Received BEP Hello (Protobuf) from device {DeviceId}: {DeviceName} ({ClientName} {ClientVersion})",
                        _deviceId, hello.DeviceName, hello.ClientName, hello.ClientVersion);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read Hello message from device {DeviceId}", _deviceId);
                return;
            }
        }

        while (!cancellationToken.IsCancellationRequested && IsConnected)
        {
            try
            {
                var (header, message) = await _protobufSerializer.ReadMessageAsync(_stream, cancellationToken);
                await ProcessProtobufMessageAsync(header, message);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (EndOfStreamException)
            {
                _logger.LogInformation("Connection closed by device {DeviceId}", _deviceId);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error receiving BEP message (Protobuf) from device {DeviceId}", _deviceId);
                break;
            }
        }
    }

    private async Task ProcessProtobufMessageAsync(Proto.Header header, IMessage message)
    {
        try
        {
            var messageType = (BepMessageType)(int)header.Type;

            switch (message)
            {
                case Proto.ClusterConfig config:
                    var bepConfig = BepMessageConverter.FromProto(config);
                    _logger.LogDebug("Received BEP ClusterConfig (Protobuf) from device {DeviceId} with {FolderCount} folders",
                        _deviceId, bepConfig.Folders.Count);
                    break;

                case Proto.Index index:
                    var bepIndex = BepMessageConverter.FromProto(index);
                    _logger.LogDebug("Received BEP Index (Protobuf) from device {DeviceId} for folder {FolderId} with {FileCount} files",
                        _deviceId, bepIndex.Folder, bepIndex.Files.Count);
                    MessageReceived?.Invoke(this, (BepMessageType.Index, bepIndex));
                    break;

                case Proto.IndexUpdate indexUpdate:
                    var bepIndexUpdate = BepMessageConverter.FromProto(indexUpdate);
                    _logger.LogDebug("Received BEP IndexUpdate (Protobuf) from device {DeviceId} for folder {FolderId} with {FileCount} files",
                        _deviceId, bepIndexUpdate.Folder, bepIndexUpdate.Files.Count);
                    MessageReceived?.Invoke(this, (BepMessageType.IndexUpdate, bepIndexUpdate));
                    break;

                case Proto.Request request:
                    var bepRequest = BepMessageConverter.FromProto(request);
                    _logger.LogDebug("Received block request (Protobuf) {RequestId} from device {DeviceId} for {FileName}",
                        bepRequest.Id, _deviceId, bepRequest.Name);
                    MessageReceived?.Invoke(this, (BepMessageType.Request, bepRequest));
                    break;

                case Proto.Response response:
                    var bepResponse = BepMessageConverter.FromProto(response);
                    _logger.LogDebug("Received block response (Protobuf) {ResponseId} from device {DeviceId} - data size: {DataSize}",
                        bepResponse.Id, _deviceId, bepResponse.Data?.Length ?? 0);

                    if (_pendingRequests.TryRemove(bepResponse.Id, out var tcs))
                    {
                        tcs.SetResult(bepResponse);
                    }
                    else
                    {
                        _logger.LogWarning("No pending request found for response {ResponseId} from device {DeviceId}",
                            bepResponse.Id, _deviceId);
                    }
                    break;

                case Proto.DownloadProgress progress:
                    var bepProgress = BepMessageConverter.FromProto(progress);
                    _logger.LogDebug("Received BEP download progress (Protobuf) from device {DeviceId} for folder {FolderId}",
                        _deviceId, bepProgress.Folder);
                    MessageReceived?.Invoke(this, (BepMessageType.DownloadProgress, bepProgress));
                    break;

                case Proto.Ping:
                    // Respond with pong
                    await SendMessageAsync(BepMessageType.Ping, new BepPing());
                    break;

                case Proto.Close close:
                    _logger.LogInformation("Received BEP close (Protobuf) from device {DeviceId}: {Reason}",
                        _deviceId, close.Reason);
                    await DisconnectAsync();
                    break;

                default:
                    _logger.LogWarning("Unknown message type from device {DeviceId}: {Type}", _deviceId, message.GetType().Name);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing BEP message (Protobuf) from device {DeviceId}", _deviceId);
        }
    }

    private async Task ReceiveLoopJsonAsync(CancellationToken cancellationToken)
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

                await ProcessMessageJsonAsync(header.Type, messageData);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error receiving BEP message (JSON) from device {DeviceId}", _deviceId);
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

    private async Task ProcessMessageJsonAsync(BepMessageType messageType, byte[] data)
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

                    _logger.LogInformation("Received BEP Hello (JSON) from device {DeviceId}: {DeviceName} ({ClientName} {ClientVersion})",
                        _deviceId, hello?.DeviceName, hello?.ClientName, hello?.ClientVersion);
                    break;

                case BepMessageType.ClusterConfig:
                    var config = JsonSerializer.Deserialize<BepClusterConfig>(json);
                    _logger.LogDebug("Received BEP ClusterConfig (JSON) from device {DeviceId} with {FolderCount} folders",
                        _deviceId, config?.Folders.Count ?? 0);
                    break;

                case BepMessageType.Index:
                    var index = JsonSerializer.Deserialize<BepIndex>(json);
                    _logger.LogDebug("Received BEP Index (JSON) from device {DeviceId} for folder {FolderId} with {FileCount} files",
                        _deviceId, index?.Folder, index?.Files.Count ?? 0);
                    if (index != null)
                        MessageReceived?.Invoke(this, (messageType, index));
                    break;

                case BepMessageType.IndexUpdate:
                    var indexUpdate = JsonSerializer.Deserialize<BepIndexUpdate>(json);
                    _logger.LogDebug("Received BEP IndexUpdate (JSON) from device {DeviceId} for folder {FolderId} with {FileCount} files",
                        _deviceId, indexUpdate?.Folder, indexUpdate?.Files.Count ?? 0);
                    if (indexUpdate != null)
                        MessageReceived?.Invoke(this, (messageType, indexUpdate));
                    break;

                case BepMessageType.Request:
                    var request = JsonSerializer.Deserialize<BepRequest>(json);
                    _logger.LogDebug("Received block request (JSON) {RequestId} from device {DeviceId} for {FileName}",
                        request?.Id, _deviceId, request?.Name);
                    if (request != null)
                    {
                        MessageReceived?.Invoke(this, (messageType, request));
                    }
                    else
                    {
                        _logger.LogError("Failed to deserialize block request (JSON) from device {DeviceId}", _deviceId);
                    }
                    break;

                case BepMessageType.Response:
                    var response = JsonSerializer.Deserialize<BepResponse>(json);
                    _logger.LogDebug("Received block response (JSON) {ResponseId} from device {DeviceId} - data size: {DataSize}",
                        response?.Id, _deviceId, response?.Data?.Length ?? 0);

                    if (response != null && _pendingRequests.TryRemove(response.Id, out var tcs))
                    {
                        tcs.SetResult(response);
                    }
                    else if (response != null)
                    {
                        _logger.LogWarning("No pending request found for response {ResponseId} from device {DeviceId}",
                            response.Id, _deviceId);
                    }
                    else
                    {
                        _logger.LogError("Failed to deserialize block response (JSON) from device {DeviceId}", _deviceId);
                    }
                    break;

                case BepMessageType.DownloadProgress:
                    var progress = JsonSerializer.Deserialize<BepDownloadProgress>(json);
                    _logger.LogDebug("Received BEP download progress (JSON) from device {DeviceId} for folder {FolderId}",
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
                    _logger.LogInformation("Received BEP close (JSON) from device {DeviceId}: {Reason}",
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
            _logger.LogError(ex, "Error processing BEP message type {MessageType} (JSON) from device {DeviceId}", messageType, _deviceId);
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