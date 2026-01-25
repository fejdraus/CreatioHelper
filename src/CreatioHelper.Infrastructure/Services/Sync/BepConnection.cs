using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services.Metrics;
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
/// BEP Protocol state tracking.
/// Following Syncthing pattern: ClusterConfig must be the first message after Hello.
/// See Syncthing lib/protocol/protocol.go stateInitial/stateReady.
/// </summary>
public enum BepProtocolState
{
    /// <summary>
    /// Initial state - waiting for ClusterConfig to be sent/received.
    /// Only Hello, ClusterConfig, and Close messages are allowed.
    /// </summary>
    Initial,

    /// <summary>
    /// Ready state - ClusterConfig has been exchanged.
    /// All message types are allowed.
    /// </summary>
    Ready
}

/// <summary>
/// BEP Connection handling exact Syncthing wire format.
/// Supports both JSON (legacy) and Protobuf (native Syncthing) serialization.
/// Wire format: [2 bytes: header length][Header: protobuf][4 bytes: message length][Message: protobuf]
/// </summary>
public class BepConnection : IDisposable, IConnectionLifecycle
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

    // BEP Protocol state tracking (following Syncthing pattern from lib/protocol/protocol.go)
    // ClusterConfig MUST be the first message after Hello exchange
    private volatile BepProtocolState _protocolState = BepProtocolState.Initial;

    // Connection lifecycle tracking
    private ConnectionState _state = ConnectionState.Connecting;
    private DateTime _lastActivity = DateTime.UtcNow;
    private long _bytesSent = 0;
    private long _bytesReceived = 0;
    private int _errorCount = 0;
    private readonly object _stateLock = new();

    public string DeviceId => _deviceId;
    public bool IsConnected => _isConnected && _tcpClient.Connected;
    public BepSerializationMode SerializationMode => _serializationMode;

    /// <summary>
    /// Gets the current BEP protocol state.
    /// ClusterConfig must be sent/received before other messages.
    /// </summary>
    public BepProtocolState ProtocolState => _protocolState;

    /// <summary>
    /// Gets whether the connection is ready for data messages (ClusterConfig was exchanged).
    /// </summary>
    public bool IsProtocolReady => _protocolState == BepProtocolState.Ready;

    // IConnectionLifecycle implementation
    public event EventHandler<ConnectionStateEventArgs>? StateChanged;
    public ConnectionState State
    {
        get
        {
            lock (_stateLock)
            {
                return _state;
            }
        }
        private set
        {
            ConnectionState oldState;
            lock (_stateLock)
            {
                if (_state == value) return;
                oldState = _state;
                _state = value;
            }
            OnStateChanged(oldState, value);
        }
    }

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

    /// <summary>
    /// Gets the current health status of the connection.
    /// </summary>
    public ConnectionHealth GetHealth()
    {
        // Calculate health score based on various factors
        double score = 100.0;

        // Deduct for errors (up to 50 points)
        if (_errorCount > 0)
        {
            score -= Math.Min(50, _errorCount * 10);
        }

        // Deduct for inactivity (up to 30 points)
        var inactivitySeconds = (DateTime.UtcNow - _lastActivity).TotalSeconds;
        if (inactivitySeconds > 60)
        {
            score -= Math.Min(30, (inactivitySeconds - 60) / 10);
        }

        // Deduct if not connected (30 points)
        if (!IsConnected)
        {
            score -= 30;
        }

        // Ensure score is within bounds
        score = Math.Max(0, Math.Min(100, score));

        // Update health score metric
        ConnectionMetrics.SetHealthScore(_deviceId, score);

        return new ConnectionHealth
        {
            Score = score,
            Latency = TimeSpan.Zero, // TODO: Implement ping-based latency measurement
            LastActivity = _lastActivity,
            BytesSent = _bytesSent,
            BytesReceived = _bytesReceived,
            ErrorCount = _errorCount
        };
    }

    private void OnStateChanged(ConnectionState oldState, ConnectionState newState, string? reason = null)
    {
        _logger.LogInformation("Connection state changed for device {DeviceId}: {OldState} -> {NewState}{Reason}",
            _deviceId, oldState, newState, reason != null ? $" ({reason})" : "");

        // Record state transition metric
        ConnectionMetrics.RecordStateTransition(oldState.ToString(), newState.ToString());

        StateChanged?.Invoke(this, new ConnectionStateEventArgs
        {
            OldState = oldState,
            NewState = newState,
            Reason = reason,
            DeviceId = _deviceId
        });
    }

    private void UpdateActivity()
    {
        _lastActivity = DateTime.UtcNow;
    }

    private void IncrementBytesSent(long bytes)
    {
        Interlocked.Add(ref _bytesSent, bytes);
        UpdateActivity();
    }

    private void IncrementBytesReceived(long bytes)
    {
        Interlocked.Add(ref _bytesReceived, bytes);
        UpdateActivity();
    }

    private void IncrementErrorCount()
    {
        Interlocked.Increment(ref _errorCount);
    }

    /// <summary>
    /// Validates that the protocol state allows sending the specified message type.
    /// Following Syncthing BEP protocol: ClusterConfig MUST be the first message after Hello.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when trying to send data messages before ClusterConfig.</exception>
    private void ValidateProtocolStateForSend(BepMessageType messageType)
    {
        // Hello, ClusterConfig, and Close are always allowed regardless of state
        if (messageType == BepMessageType.Hello ||
            messageType == BepMessageType.ClusterConfig ||
            messageType == BepMessageType.Close)
        {
            return;
        }

        // For all other messages (Index, IndexUpdate, Request, Response, DownloadProgress, Ping),
        // ClusterConfig must have been sent first
        if (_protocolState != BepProtocolState.Ready)
        {
            _logger.LogWarning(
                "BepConnection: Protocol violation - attempting to send {MessageType} before ClusterConfig for device {DeviceId}. " +
                "ClusterConfig MUST be the first message after Hello exchange (Syncthing BEP protocol requirement).",
                messageType, _deviceId);

            throw new InvalidOperationException(
                $"BEP protocol violation: Cannot send {messageType} before ClusterConfig. " +
                "ClusterConfig must be the first message after Hello exchange.");
        }
    }

    /// <summary>
    /// Validates that the protocol state allows receiving the specified message type.
    /// Following Syncthing BEP protocol: ClusterConfig MUST be the first message received after Hello.
    /// </summary>
    /// <returns>True if the message should be processed, false if it should be rejected.</returns>
    private bool ValidateProtocolStateForReceive(BepMessageType messageType)
    {
        // ClusterConfig transitions state from Initial to Ready
        if (messageType == BepMessageType.ClusterConfig)
        {
            if (_protocolState == BepProtocolState.Initial)
            {
                _protocolState = BepProtocolState.Ready;
                _logger.LogDebug("BepConnection: ClusterConfig received, protocol state transitioned to Ready for device {DeviceId}", _deviceId);
            }
            return true;
        }

        // Close is always allowed
        if (messageType == BepMessageType.Close)
        {
            return true;
        }

        // For all other messages, protocol must be in Ready state
        if (_protocolState != BepProtocolState.Ready)
        {
            _logger.LogWarning(
                "BepConnection: Protocol violation - received {MessageType} before ClusterConfig from device {DeviceId}. " +
                "ClusterConfig MUST be the first message after Hello exchange (Syncthing BEP protocol requirement).",
                messageType, _deviceId);
            return false;
        }

        return true;
    }

    public Task StartAsync()
    {
        // Transition to Connected state
        State = ConnectionState.Connected;

        _ = Task.Run(async () =>
        {
            try
            {
                await ReceiveLoopAsync(_cancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in BEP receive loop for device {DeviceId}", _deviceId);
                IncrementErrorCount();
                State = ConnectionState.Failed;
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
        // Validate BEP protocol ordering: ClusterConfig MUST be the first message after Hello
        // Following Syncthing pattern from lib/protocol/protocol.go dispatcherLoop
        // Protocol validation comes FIRST - protocol violations are programmer errors
        // and should always throw, regardless of connection state.
        ValidateProtocolStateForSend(messageType);

        if (!IsConnected)
        {
            _logger.LogWarning("BepConnection: Cannot send {MessageType} to device {DeviceId} - not connected", messageType, _deviceId);
            return;
        }

        // Update protocol state when ClusterConfig is sent
        if (messageType == BepMessageType.ClusterConfig)
        {
            _protocolState = BepProtocolState.Ready;
            _logger.LogDebug("BepConnection: ClusterConfig sent, protocol state transitioned to Ready for device {DeviceId}", _deviceId);
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
            IncrementErrorCount();
            await DisconnectAsync("Error sending message");
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
            IncrementBytesSent(helloData.Length);
            _logger.LogDebug("Sent BEP Hello (Protobuf) to device {DeviceId}", _deviceId);
            return;
        }

        // Convert to Protobuf message and serialize
        var (protoMessage, protoMessageType) = ConvertToProto(messageType, message);
        var wireData = _protobufSerializer.SerializeMessage(protoMessage, protoMessageType, allowCompression: true);
        await _stream.WriteAsync(wireData);
        await _stream.FlushAsync();
        IncrementBytesSent(wireData.Length);

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
            // CreatioHelper P2P Upgrade Extensions
            BepMessageType.AgentUpdateRequest when message is BepAgentUpdateRequest updateRequest =>
                (BepMessageConverter.ToProto(updateRequest), Proto.MessageType.AgentUpdateRequest),
            BepMessageType.AgentUpdateResponse when message is BepAgentUpdateResponse updateResponse =>
                (BepMessageConverter.ToProto(updateResponse), Proto.MessageType.AgentUpdateResponse),
            _ => throw new InvalidOperationException($"Unsupported message type for Protobuf: {messageType}")
        };
    }

    private async Task SendMessageJsonAsync<T>(BepMessageType messageType, T message)
    {
        long totalBytesSent = 0;

        // Send BEP magic number only for Hello message (first message from client)
        if (messageType == BepMessageType.Hello && !_magicSent)
        {
            var magicBytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(magicBytes, BepMagic);
            await _stream.WriteAsync(magicBytes);
            _magicSent = true;
            totalBytesSent += 4;
            _logger.LogDebug("Sent BEP magic number 0x{Magic:X8} to device {DeviceId}", BepMagic, _deviceId);
        }

        // Serialize message
        var json = JsonSerializer.Serialize(message);
        var messageData = Encoding.UTF8.GetBytes(json);

        // Check if compression should be used
        var useCompression = messageData.Length >= CompressionThreshold;
        var compression = useCompression ? BepCompression.Always : BepCompression.Never;

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
                compression = BepCompression.Never;
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
        totalBytesSent += 2;

        // Write header
        await _stream.WriteAsync(headerData);
        totalBytesSent += headerData.Length;

        // Write message length (4 bytes, big endian)
        var messageLengthBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(messageLengthBytes, (uint)messageData.Length);
        await _stream.WriteAsync(messageLengthBytes);
        totalBytesSent += 4;

        // Write message
        await _stream.WriteAsync(messageData);
        totalBytesSent += messageData.Length;

        await _stream.FlushAsync();
        IncrementBytesSent(totalBytesSent);

        _logger.LogDebug("Sent BEP {MessageType} (JSON) to device {DeviceId}, size: {Size}, compressed: {Compressed}",
            messageType, _deviceId, messageData.Length, compression != BepCompression.Never);
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
                IncrementErrorCount();
                break;
            }
        }
    }

    private async Task ProcessProtobufMessageAsync(Proto.Header header, IMessage message)
    {
        try
        {
            // Track bytes received (estimate based on serialized message size)
            IncrementBytesReceived(message.CalculateSize());

            var messageType = (BepMessageType)(int)header.Type;

            // Validate protocol state: ClusterConfig must be received before other messages
            // Following Syncthing pattern from lib/protocol/protocol.go dispatcherLoop
            if (!ValidateProtocolStateForReceive(messageType))
            {
                // Protocol violation - reject the message
                await DisconnectAsync($"Protocol violation: received {messageType} before ClusterConfig");
                return;
            }

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

                // CreatioHelper P2P Upgrade Extensions
                case Proto.AgentUpdateRequest updateRequest:
                    var bepUpdateRequest = BepMessageConverter.FromProto(updateRequest);
                    _logger.LogInformation("Received agent update request (Protobuf) from device {DeviceId}: v{FromVersion} -> v{ToVersion}",
                        _deviceId, bepUpdateRequest.FromVersion, bepUpdateRequest.ToVersion);
                    MessageReceived?.Invoke(this, (BepMessageType.AgentUpdateRequest, bepUpdateRequest));
                    break;

                case Proto.AgentUpdateResponse updateResponse:
                    var bepUpdateResponse = BepMessageConverter.FromProto(updateResponse);
                    _logger.LogDebug("Received agent update response (Protobuf) from device {DeviceId}: chunk {Chunk}/{Total}",
                        _deviceId, bepUpdateResponse.ChunkIndex + 1, bepUpdateResponse.TotalChunks);
                    MessageReceived?.Invoke(this, (BepMessageType.AgentUpdateResponse, bepUpdateResponse));
                    break;

                default:
                    _logger.LogWarning("Unknown message type from device {DeviceId}: {Type}", _deviceId, message.GetType().Name);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing BEP message (Protobuf) from device {DeviceId}", _deviceId);
            IncrementErrorCount();
        }
    }

    private async Task ReceiveLoopJsonAsync(CancellationToken cancellationToken)
    {
        // Read magic number only for incoming connections (we are server)
        if (!_isOutgoing)
        {
            var magicBytes = new byte[4];
            await ReadExactAsync(magicBytes, cancellationToken);
            IncrementBytesReceived(4);
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
                long bytesReceivedThisMessage = 0;

                // Read header length (2 bytes, big endian)
                var headerLengthBytes = new byte[2];
                await ReadExactAsync(headerLengthBytes, cancellationToken);
                bytesReceivedThisMessage += 2;
                var headerLength = BinaryPrimitives.ReadUInt16BigEndian(headerLengthBytes);

                if (headerLength > 1024) // Reasonable header size limit
                {
                    _logger.LogError("Header too large from device {DeviceId}: {Length} bytes", _deviceId, headerLength);
                    IncrementErrorCount();
                    return;
                }

                // Read header
                var headerData = new byte[headerLength];
                await ReadExactAsync(headerData, cancellationToken);
                bytesReceivedThisMessage += headerLength;

                var headerJson = Encoding.UTF8.GetString(headerData);
                var header = JsonSerializer.Deserialize<BepHeader>(headerJson);
                if (header == null)
                {
                    _logger.LogError("Failed to deserialize header from device {DeviceId}", _deviceId);
                    IncrementErrorCount();
                    return;
                }

                // Read message length (4 bytes, big endian)
                var messageLengthBytes = new byte[4];
                await ReadExactAsync(messageLengthBytes, cancellationToken);
                bytesReceivedThisMessage += 4;
                var messageLength = BinaryPrimitives.ReadUInt32BigEndian(messageLengthBytes);

                if (messageLength > MaxMessageSize)
                {
                    _logger.LogError("Message too large from device {DeviceId}: {Length} bytes", _deviceId, messageLength);
                    IncrementErrorCount();
                    return;
                }

                // Read message data
                var messageData = new byte[messageLength];
                await ReadExactAsync(messageData, cancellationToken);
                bytesReceivedThisMessage += messageLength;

                // Track bytes received
                IncrementBytesReceived(bytesReceivedThisMessage);

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
                        IncrementErrorCount();
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
                IncrementErrorCount();
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
            // Validate protocol state: ClusterConfig must be received before other messages
            // Following Syncthing pattern from lib/protocol/protocol.go dispatcherLoop
            // Note: Hello is processed separately before this method is called
            if (messageType != BepMessageType.Hello && !ValidateProtocolStateForReceive(messageType))
            {
                // Protocol violation - reject the message
                await DisconnectAsync($"Protocol violation: received {messageType} before ClusterConfig");
                return;
            }

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

                // CreatioHelper P2P Upgrade Extensions
                case BepMessageType.AgentUpdateRequest:
                    var updateRequest = JsonSerializer.Deserialize<BepAgentUpdateRequest>(json);
                    if (updateRequest != null)
                    {
                        _logger.LogInformation("Received agent update request (JSON) from device {DeviceId}: v{FromVersion} -> v{ToVersion}",
                            _deviceId, updateRequest.FromVersion, updateRequest.ToVersion);
                        MessageReceived?.Invoke(this, (messageType, updateRequest));
                    }
                    else
                    {
                        _logger.LogError("Failed to deserialize agent update request (JSON) from device {DeviceId}", _deviceId);
                    }
                    break;

                case BepMessageType.AgentUpdateResponse:
                    var updateResponse = JsonSerializer.Deserialize<BepAgentUpdateResponse>(json);
                    if (updateResponse != null)
                    {
                        _logger.LogDebug("Received agent update response (JSON) from device {DeviceId}: chunk {Chunk}/{Total}",
                            _deviceId, updateResponse.ChunkIndex + 1, updateResponse.TotalChunks);
                        MessageReceived?.Invoke(this, (messageType, updateResponse));
                    }
                    else
                    {
                        _logger.LogError("Failed to deserialize agent update response (JSON) from device {DeviceId}", _deviceId);
                    }
                    break;

                default:
                    _logger.LogWarning("Unknown BEP message type {MessageType} from device {DeviceId}", messageType, _deviceId);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing BEP message type {MessageType} (JSON) from device {DeviceId}", messageType, _deviceId);
            IncrementErrorCount();
        }
    }

    // Block request handling removed - now handled via MessageReceived event -> BlockRequestHandler

    public async Task DisconnectAsync(string? reason = null)
    {
        if (!_isConnected) return;

        // Transition to Disconnecting state
        var previousState = State;
        if (previousState != ConnectionState.Failed)
        {
            State = ConnectionState.Disconnecting;
        }

        _isConnected = false;
        _cancellationTokenSource.Cancel();

        try
        {
            // Send close message
            await SendMessageAsync(BepMessageType.Close, new BepClose { Reason = reason ?? "Connection closed" });
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

        // Transition to Disconnected state (unless already Failed)
        if (previousState != ConnectionState.Failed)
        {
            State = ConnectionState.Disconnected;
        }

        _logger.LogInformation("Disconnected BEP connection from device {DeviceId}", _deviceId);
    }

    public void Dispose()
    {
        try
        {
            DisconnectAsync().Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Ignore exceptions during disposal
        }
        _cancellationTokenSource.Dispose();
        _sendSemaphore.Dispose();
    }
}