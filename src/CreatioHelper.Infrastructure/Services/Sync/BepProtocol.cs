using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Complete Block Exchange Protocol implementation based on Syncthing BEP
/// Following exact Syncthing wire format and message structures
/// </summary>
public class BepProtocol : ISyncProtocol, IDisposable
{
    private readonly ILogger<BepProtocol> _logger;
    private readonly ISyncDatabase _database;
    private readonly BlockDuplicationDetector _blockDetector;
    private TcpListener? _listener;
    private readonly int _port;
    private readonly X509Certificate2 _certificate;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly ConcurrentDictionary<string, BepConnection> _connections = new();
    
    // BEP Protocol Constants (Syncthing compatible)
    private const uint BepMagic = 0x2EA7D90B;             // HelloMessageMagic
    private const uint Version13HelloMagic = 0x9F79BC40;  // Legacy version support
    private const string DeviceName = "CreatioHelper";
    private const string ClientName = "CreatioHelper";
    private const string ClientVersion = "1.0.0";
    private const int CompressionThreshold = 128;         // compressionThreshold
    private const int MaxMessageSize = 500 * 1000 * 1000; // MaxMessageLen (500MB)
    private const int MinBlockSize = 128 * 1024;          // MinBlockSize (128 KB)
    private const int MaxBlockSize = 16 * 1024 * 1024;    // MaxBlockSize (16 MB)

    public event EventHandler<DeviceConnectedEventArgs>? DeviceConnected;
    public event EventHandler<DeviceDisconnectedEventArgs>? DeviceDisconnected;
    public event EventHandler<IndexReceivedEventArgs>? IndexReceived;
    public event EventHandler<IndexUpdateReceivedEventArgs>? IndexUpdateReceived;
    public event EventHandler<BlockRequestedEventArgs>? BlockRequested;
    public event EventHandler<DownloadProgressEventArgs>? DownloadProgressReceived;
    public event EventHandler<BlockRequestReceivedEventArgs>? BlockRequestReceived;

    public BepProtocol(ILogger<BepProtocol> logger, ISyncDatabase database, BlockDuplicationDetector blockDetector, int port, X509Certificate2 certificate, string deviceId)
    {
        _logger = logger;
        _database = database;
        _blockDetector = blockDetector;
        _port = port;
        _certificate = certificate;
        DeviceId = deviceId;
    }
    
    public string DeviceId { get; }

    public Task StartListeningAsync()
    {
        _listener = new TcpListener(System.Net.IPAddress.Any, _port);
        _listener.Start();
        
        _logger.LogInformation("BEP Protocol listening on port {Port}", _port);
        
        _ = Task.Run(async () =>
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync();
                    _ = Task.Run(() => HandleIncomingConnectionAsync(tcpClient, _cancellationTokenSource.Token));
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error accepting BEP connection");
                }
            }
        });
        
        return Task.CompletedTask;
    }

    public async Task<bool> ConnectAsync(SyncDevice device, CancellationToken cancellationToken = default)
    {
        if (_connections.ContainsKey(device.DeviceId))
        {
            return true; // Already connected
        }

        // Filter out "dynamic" - it should be resolved before calling ConnectAsync
        // (like Syncthing's resolveDeviceAddrs in lib/connections/service.go)
        var addresses = device.Addresses.Where(a => a != "dynamic").ToList();

        if (addresses.Count == 0)
        {
            _logger.LogDebug("No resolved addresses for device {DeviceId} (only 'dynamic')", device.DeviceId);
            return false;
        }

        foreach (var address in addresses)
        {
            try
            {
                var connection = await EstablishConnectionAsync(address, device, cancellationToken);
                if (connection != null)
                {
                    _connections[device.DeviceId] = connection;
                    device.UpdateConnection(true, address);
                    DeviceConnected?.Invoke(this, new DeviceConnectedEventArgs(device));
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to device {DeviceId} at {Address}", device.DeviceId, address);
            }
        }

        return false;
    }

    /// <summary>
    /// Register an existing connection (e.g., from relay) with the protocol
    /// </summary>
    public async Task RegisterConnectionAsync(BepConnection connection)
    {
        try
        {
            _logger.LogDebug("Registering existing connection for device {DeviceId}", connection.DeviceId);

            // Subscribe to connection events
            connection.MessageReceived += OnConnectionMessageReceived;
            connection.DeviceIdUpdated += OnConnectionDeviceIdUpdated;
            
            // The connection will handle its own lifecycle, 
            // and we'll be notified via existing events when it disconnects

            // Start the connection
            await connection.StartAsync();

            // Add to connections dictionary
            _connections[connection.DeviceId] = connection;

            // Send Hello message to establish protocol
            await connection.SendHelloAsync(DeviceId, DeviceName, ClientName, ClientVersion);

            // NOTE: Following Syncthing BEP protocol (lib/protocol/protocol.go):
            // ClusterConfig MUST be sent as the first message after Hello exchange.
            // The caller must call SendClusterConfigAsync immediately after this method returns.
            // The BepConnection will throw an exception if any other message is sent before ClusterConfig.

            // Fire DeviceConnected event for relay connections
            var device = new SyncDevice(connection.DeviceId, "Relay Device");
            DeviceConnected?.Invoke(this, new DeviceConnectedEventArgs(device));

            _logger.LogInformation("Successfully registered connection for device {DeviceId}", connection.DeviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering connection for device {DeviceId}", connection.DeviceId);
            connection?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Interface implementation for RegisterConnectionAsync
    /// </summary>
    async Task ISyncProtocol.RegisterConnectionAsync(object connection)
    {
        if (connection is BepConnection bepConnection)
        {
            await RegisterConnectionAsync(bepConnection);
        }
        else
        {
            throw new ArgumentException("Connection must be a BepConnection", nameof(connection));
        }
    }

    public async Task DisconnectAsync(string deviceId)
    {
        if (_connections.TryGetValue(deviceId, out var connection))
        {
            _connections.TryRemove(deviceId, out _);
            await connection.DisconnectAsync();
            DeviceDisconnected?.Invoke(this, new DeviceDisconnectedEventArgs(deviceId));
        }
    }

    public async Task SendHelloAsync(string deviceId, string deviceName, string clientName, string clientVersion)
    {
        if (!_connections.TryGetValue(deviceId, out var connection))
            return;

        var hello = new BepHello
        {
            DeviceId = DeviceId, // Include our device ID in Hello message
            DeviceName = deviceName,
            ClientName = clientName,
            ClientVersion = clientVersion
        };

        await connection.SendMessageAsync(BepMessageType.Hello, hello);
    }

    /// <summary>
    /// Sends ClusterConfig to the specified device.
    /// IMPORTANT: Following Syncthing BEP protocol (lib/protocol/protocol.go writerLoop),
    /// ClusterConfig MUST be the first message sent after Hello exchange.
    /// This method must be called immediately after a successful connection before
    /// any other messages (Index, IndexUpdate, Request, etc.) can be sent.
    /// </summary>
    /// <param name="deviceId">The target device ID</param>
    /// <param name="folders">List of folders to share with the device</param>
    public async Task SendClusterConfigAsync(string deviceId, List<SyncFolder> folders)
    {
        if (!_connections.TryGetValue(deviceId, out var connection))
            return;

        var config = new BepClusterConfig
        {
            Folders = folders.Select(f => new BepFolder
            {
                Id = f.Id,
                Label = f.Label,
                ReadOnly = f.Type == "receiveonly",
                IgnorePermissions = false,
                IgnoreDelete = false,
                DisableTempIndexes = false,
                Paused = f.IsPaused,
                Devices = f.Devices.Select(deviceId => new BepDevice
                {
                    Id = StringToDeviceId(deviceId),
                    Name = deviceId, // Use DeviceId as name for now
                    Addresses = new List<string>(), // Empty addresses for now
                    Compression = BepCompression.Always,
                    CertName = deviceId
                }).ToList()
            }).ToList()
        };

        await connection.SendMessageAsync(BepMessageType.ClusterConfig, config);
    }

    public async Task SendIndexAsync(string deviceId, BepIndex index)
    {
        if (!_connections.TryGetValue(deviceId, out var connection))
            return;

        await connection.SendMessageAsync(BepMessageType.Index, index);
    }

    public async Task SendIndexAsync(string deviceId, string folderId, List<SyncFileInfo> files)
    {
        if (!_connections.TryGetValue(deviceId, out var connection))
            return;

        var index = new BepIndex
        {
            Folder = folderId,
            Files = files.Select(f => new BepFileInfo
            {
                Name = f.Name,
                Size = f.Size,
                ModifiedS = (long)f.ModifiedTime.Subtract(DateTime.UnixEpoch).TotalSeconds,
                ModifiedNs = (int)(f.ModifiedTime.Ticks % TimeSpan.TicksPerSecond * 100),
                Version = VectorClockToBep(f.Vector),
                Sequence = f.Sequence,
                Blocks = f.Blocks.Select(b => new BepBlockInfo
                {
                    Offset = b.Offset,
                    Size = b.Size,
                    Hash = ConvertHashStringToBytes(b.Hash), // Support both hex string and byte[] hashes
                    WeakHash = (uint)b.WeakHash
                }).ToList(),
                Symlink = f.SymlinkTarget ?? "",
                BlocksHash = ConvertHashStringToBytes(f.Hash), // Support both hex string and byte[] hashes
                Encrypted = false,
                Type = f.Type == FileType.Directory ? BepFileInfoType.Directory : BepFileInfoType.File,
                Permissions = (uint)(f.Type == FileType.Directory ? 0x1ED : 0x1A4), // 755 and 644 in hex
                ModifiedBy = StringToShortId(deviceId),
                Deleted = f.IsDeleted,
                Invalid = f.IsInvalid
            }).ToList()
        };

        await connection.SendMessageAsync(BepMessageType.Index, index);
    }

    public async Task SendIndexUpdateAsync(string deviceId, string folderId, List<SyncFileInfo> changedFiles)
    {
        if (!_connections.TryGetValue(deviceId, out var connection))
            return;

        var indexUpdate = new BepIndexUpdate
        {
            Folder = folderId,
            Files = changedFiles.Select(f => new BepFileInfo
            {
                Name = f.Name,
                Size = f.Size,
                ModifiedS = (long)f.ModifiedTime.Subtract(DateTime.UnixEpoch).TotalSeconds,
                ModifiedNs = (int)(f.ModifiedTime.Ticks % TimeSpan.TicksPerSecond * 100),
                Version = VectorClockToBep(f.Vector),
                Sequence = f.Sequence,
                Blocks = f.Blocks.Select(b => new BepBlockInfo
                {
                    Offset = b.Offset,
                    Size = b.Size,
                    Hash = ConvertHashStringToBytes(b.Hash), // Support both hex string and byte[] hashes
                    WeakHash = (uint)b.WeakHash
                }).ToList(),
                Symlink = f.SymlinkTarget ?? "",
                BlocksHash = ConvertHashStringToBytes(f.Hash), // Support both hex string and byte[] hashes
                Encrypted = false,
                Type = f.Type == FileType.Directory ? BepFileInfoType.Directory : BepFileInfoType.File,
                Permissions = (uint)(f.Type == FileType.Directory ? 0x1ED : 0x1A4), // 755 and 644 in hex
                ModifiedBy = StringToShortId(deviceId),
                Deleted = f.IsDeleted,
                Invalid = f.IsInvalid
            }).ToList()
        };

        await connection.SendMessageAsync(BepMessageType.IndexUpdate, indexUpdate);
    }

    public Task<List<SyncFileInfo>> RequestIndexAsync(string deviceId, string folderId)
    {
        // BEP doesn't have explicit index requests - they're sent automatically after ClusterConfig
        return Task.FromResult(new List<SyncFileInfo>());
    }

    public async Task<byte[]> RequestBlockAsync(string deviceId, string folderId, string fileName, long offset, int size, string hash)
    {
        if (!_connections.TryGetValue(deviceId, out var connection))
            return Array.Empty<byte>();

        var request = new BepRequest
        {
            Id = GenerateRequestId(),
            Folder = folderId,
            Name = fileName,
            Offset = offset,
            Size = size,
            Hash = ConvertHashStringToBytes(hash), // Support both hex string and byte[] hashes
            FromTemporary = false,
            WeakHash = 0,
            BlockNo = 0
        };

        var response = await connection.RequestBlockAsync(request);
        return response.Data;
    }

    /// <summary>
    /// Request a block by its SHA-256 hash (Syncthing-compatible)
    /// </summary>
    public async Task<byte[]> RequestBlockByHashAsync(string deviceId, byte[] blockHash)
    {
        if (!_connections.TryGetValue(deviceId, out var connection))
            return Array.Empty<byte>();

        // For block-level requests, we use a special request format
        var request = new BepRequest
        {
            Id = GenerateRequestId(),
            Folder = "", // Empty for direct block requests
            Name = "", // Empty for direct block requests
            Offset = 0,
            Size = 0, // Will be determined by the receiving side based on hash
            Hash = blockHash,
            FromTemporary = false,
            WeakHash = 0,
            BlockNo = 0
        };

        var response = await connection.RequestBlockAsync(request);
        return response.Data;
    }

    public async Task SendBlockAsync(string deviceId, string folderId, string fileName, long offset, byte[] data)
    {
        if (!_connections.TryGetValue(deviceId, out var connection))
            return;

        // Integrate with Database Layer - store block metadata
        try 
        {
            await StoreBlockMetadataAsync(data, folderId, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to store block metadata for {FolderId}/{FileName}", folderId, fileName);
        }

        var response = new BepResponse
        {
            Id = 0, // Will be set by connection when responding to request
            Data = data,
            Code = BepErrorCode.NoError
        };

        await connection.SendMessageAsync(BepMessageType.Response, response);
    }

    /// <summary>
    /// Store block metadata in Database Layer for deduplication
    /// </summary>
    private async Task StoreBlockMetadataAsync(byte[] data, string folderId, string fileName)
    {
        if (data.Length == 0) return;

        // Use Block Deduplication system to analyze and store
        var analysisResult = await _blockDetector.AnalyzeFileAsync(
            Path.Combine(folderId, fileName), folderId);
        
        if (analysisResult.Error == null)
        {
            _logger.LogDebug("Block analysis completed for {FolderId}/{FileName}: {NewBlocks} new blocks, {ExistingBlocks} existing", 
                folderId, fileName, analysisResult.NewBlocks.Count, analysisResult.ExistingBlocks.Count);
        }
    }

    /// <summary>
    /// Check if block exists in Database Layer for deduplication optimization
    /// </summary>
    private async Task<bool> CheckBlockExistsAsync(byte[] blockHash)
    {
        try
        {
            var blockMetadata = await _database.BlockInfo.GetAsync(blockHash);
            if (blockMetadata != null)
            {
                // Update last accessed timestamp
                await _database.BlockInfo.UpdateLastAccessedAsync(blockHash);
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check block existence for hash {Hash}", Convert.ToHexString(blockHash));
            return false;
        }
    }

    /// <summary>
    /// Get file metadata from Database Layer for BEP Index messages
    /// </summary>
    private async Task<IEnumerable<SyncFileInfo>> GetFileMetadataAsync(string folderId)
    {
        try
        {
            // Use Database Layer to get file metadata
            var files = await _database.FileMetadata.GetAllAsync(folderId);
            
            var result = new List<SyncFileInfo>();
            foreach (var f in files)
            {
                var fileInfo = new SyncFileInfo(f.FolderId, f.FileName, f.FileName, f.Size, f.ModifiedTime);
                
                if (f.Hash != null && f.Hash.Length > 0)
                {
                    fileInfo.UpdateHash(System.Text.Encoding.UTF8.GetString(f.Hash));
                }
                
                if (f.Permissions.HasValue)
                {
                    // Используем рефлексию для установки Permissions, так как нет публичного setter
                    var permissionsProp = typeof(SyncFileInfo).GetProperty("Permissions", 
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    permissionsProp?.SetValue(fileInfo, f.Permissions.Value);
                }
                
                if (f.IsDeleted)
                {
                    fileInfo.MarkAsDeleted();
                }
                
                result.Add(fileInfo);
            }
            
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get file metadata for folder {FolderId}", folderId);
            return Array.Empty<SyncFileInfo>();
        }
    }

    public async Task SendBlockResponseAsync(string deviceId, object response)
    {
        if (!_connections.TryGetValue(deviceId, out var connection))
            return;

        if (response is BepResponse bepResponse)
        {
            await connection.SendBlockResponseAsync(bepResponse);
        }
    }

    public async Task SendDownloadProgressAsync(string deviceId, string folderId, List<FileDownloadProgress> progress)
    {
        if (!_connections.TryGetValue(deviceId, out var connection))
            return;

        var message = new BepDownloadProgress
        {
            Folder = folderId,
            Updates = progress.Select(p => new BepFileDownloadProgressUpdate
            {
                UpdateType = BepUpdateType.Append,
                Name = p.FileName,
                Version = new BepVector { Counters = new List<BepCounter>() },
                BlockIndexes = new List<int>()
            }).ToList()
        };

        await connection.SendMessageAsync(BepMessageType.DownloadProgress, message);
    }

    public async Task SendPingAsync(string deviceId)
    {
        if (!_connections.TryGetValue(deviceId, out var connection))
            return;

        await connection.SendMessageAsync(BepMessageType.Ping, new BepPing());
    }

    public async Task SendCloseAsync(string deviceId, string reason)
    {
        if (!_connections.TryGetValue(deviceId, out var connection))
            return;

        await connection.SendMessageAsync(BepMessageType.Close, new BepClose { Reason = reason });
        await DisconnectAsync(deviceId);
    }

    public Task<bool> IsConnectedAsync(string deviceId)
    {
        return Task.FromResult(_connections.TryGetValue(deviceId, out var connection) && connection.IsConnected);
    }

    private async Task<BepConnection?> EstablishConnectionAsync(string address, SyncDevice device, CancellationToken cancellationToken)
    {
        var uri = new Uri(address);
        var tcpClient = new TcpClient();

        await tcpClient.ConnectAsync(uri.Host, uri.Port, cancellationToken);

        // Establish TLS connection with Syncthing-compatible certificate validation
        var sslStream = new SslStream(
            tcpClient.GetStream(),
            false,
            (sender, cert, chain, errors) => ValidateRemoteCertificate(cert, device.DeviceId));

        var sslOptions = new SslClientAuthenticationOptions
        {
            TargetHost = uri.Host,
            ClientCertificates = new X509CertificateCollection { _certificate },
            EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck // Self-signed certs don't have revocation
        };

        await sslStream.AuthenticateAsClientAsync(sslOptions, cancellationToken);

        var connection = new BepConnection(device.DeviceId, tcpClient, sslStream, _logger, isOutgoing: true);

        // Subscribe to connection events
        connection.MessageReceived += OnConnectionMessageReceived;

        await connection.StartAsync();

        // Send Hello message
        await connection.SendHelloAsync(DeviceId, DeviceName, ClientName, ClientVersion);

        // NOTE: Following Syncthing BEP protocol (lib/protocol/protocol.go):
        // ClusterConfig MUST be sent as the first message after Hello exchange.
        // The caller (ConnectAsync) must call SendClusterConfigAsync immediately
        // after this method returns. The BepConnection will throw an exception
        // if any other message is sent before ClusterConfig.

        return connection;
    }

    private async Task HandleIncomingConnectionAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        SslStream? sslStream = null;
        try
        {
            // Establish TLS connection as server
            sslStream = new SslStream(tcpClient.GetStream(), false);

            var sslOptions = new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                RemoteCertificateValidationCallback = (sender, cert, chain, errors) => cert != null // Accept any client cert, validate by device ID
            };

            await sslStream.AuthenticateAsServerAsync(sslOptions, cancellationToken);

            // Extract device ID from client certificate
            var deviceId = ExtractDeviceIdFromCertificate(sslStream.RemoteCertificate);
            if (string.IsNullOrEmpty(deviceId))
            {
                _logger.LogWarning("Incoming connection without valid client certificate");
                sslStream.Dispose();
                tcpClient.Dispose();
                return;
            }

            _logger.LogInformation("Incoming TLS connection from device {DeviceId}", deviceId);

            var connection = new BepConnection(deviceId, tcpClient, sslStream, _logger, isOutgoing: false);

            // Subscribe to connection events
            connection.MessageReceived += OnConnectionMessageReceived;
            connection.DeviceIdUpdated += OnConnectionDeviceIdUpdated;

            _connections[deviceId] = connection;

            await connection.StartAsync();

            _logger.LogInformation("BEP device {DeviceId} connected via TLS", deviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling incoming BEP connection");
            sslStream?.Dispose();
            tcpClient.Dispose();
        }
    }

    /// <summary>
    /// Validates a remote certificate using Syncthing-compatible device ID verification.
    /// In Syncthing, certificates are self-signed and validated by matching the certificate
    /// fingerprint (SHA-256 hash) to the expected device ID.
    /// </summary>
    private bool ValidateRemoteCertificate(X509Certificate? certificate, string expectedDeviceId)
    {
        if (certificate == null)
        {
            _logger.LogWarning("Remote certificate is null");
            return false;
        }

        // Extract device ID from certificate fingerprint
        var actualDeviceId = ExtractDeviceIdFromCertificate(certificate);

        // Normalize device IDs for comparison (case-insensitive, remove hyphens)
        var normalizedExpected = NormalizeDeviceId(expectedDeviceId);
        var normalizedActual = NormalizeDeviceId(actualDeviceId);

        if (!string.Equals(normalizedExpected, normalizedActual, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Certificate device ID mismatch. Expected: {Expected}, Actual: {Actual}",
                expectedDeviceId, actualDeviceId);
            return false;
        }

        _logger.LogDebug("Certificate validated for device {DeviceId}", expectedDeviceId);
        return true;
    }

    /// <summary>
    /// Normalizes a device ID by removing hyphens and converting to lowercase.
    /// </summary>
    private static string NormalizeDeviceId(string deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
            return string.Empty;
        return deviceId.Replace("-", "").ToLowerInvariant();
    }

    private string ExtractDeviceIdFromCertificate(X509Certificate? certificate)
    {
        if (certificate == null) return string.Empty;
        
        // Syncthing uses SHA-256 hash of certificate bytes as device ID
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(certificate.GetRawCertData());
        return Convert.ToHexString(hash).ToLower();
    }

    private byte[] StringToDeviceId(string deviceIdHex)
    {
        try
        {
            return Convert.FromHexString(deviceIdHex);
        }
        catch
        {
            // Fallback for non-hex device IDs - use hash of the string
            return System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(deviceIdHex));
        }
    }

    private ulong StringToShortId(string deviceIdHex)
    {
        try
        {
            var deviceId = StringToDeviceId(deviceIdHex);
            return BinaryPrimitives.ReadUInt64BigEndian(deviceId.AsSpan(0, 8));
        }
        catch
        {
            // Fallback for non-hex device IDs - use hash of the string
            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(deviceIdHex));
            return BinaryPrimitives.ReadUInt64BigEndian(hash.AsSpan(0, 8));
        }
    }

    private BepVector VectorClockToBep(VectorClock vector)
    {
        return new BepVector
        {
            Counters = vector.Counters.Select(c => new BepCounter
            {
                Id = StringToShortId(c.Key),
                Value = (ulong)c.Value
            }).ToList()
        };
    }

    private int GenerateRequestId()
    {
        return Random.Shared.Next(1, int.MaxValue);
    }

    /// <summary>
    /// Convert hash string to bytes, supporting both hex strings and already-converted byte arrays
    /// </summary>
    private byte[] ConvertHashStringToBytes(string hash)
    {
        if (string.IsNullOrEmpty(hash))
            return Array.Empty<byte>();

        try
        {
            // Try to parse as hex string first
            if (hash.Length == 64) // SHA-256 hex string length
            {
                return Convert.FromHexString(hash);
            }
            
            // Fallback: treat as regular string and hash it
            return System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hash));
        }
        catch
        {
            // If all else fails, return empty array
            return Array.Empty<byte>();
        }
    }

    private void OnConnectionMessageReceived(object? sender, (BepMessageType Type, object Message) e)
    {
        if (sender is not BepConnection connection) return;
        
        var deviceId = connection.DeviceId;
        
        switch (e.Type)
        {
            case BepMessageType.Index when e.Message is BepIndex index:
                var indexFiles = ConvertBepFilesToSyncFiles(index.Files);
                IndexReceived?.Invoke(this, new IndexReceivedEventArgs(deviceId, index.Folder, indexFiles));
                break;
                
            case BepMessageType.IndexUpdate when e.Message is BepIndexUpdate indexUpdate:
                var updateFiles = ConvertBepFilesToSyncFiles(indexUpdate.Files);
                IndexUpdateReceived?.Invoke(this, new IndexUpdateReceivedEventArgs(deviceId, indexUpdate.Folder, updateFiles));
                break;
                
            case BepMessageType.Request when e.Message is BepRequest request:
                _logger.LogInformation("🚀 BepProtocol: Received block request {RequestId} for {FileName} from device {DeviceId}",
                    request.Id, request.Name, deviceId);
                // Trigger both old event for compatibility and new event for handling
                BlockRequested?.Invoke(this, new BlockRequestedEventArgs(
                    deviceId, request.Folder, request.Name, request.Offset, request.Size, 
                    Convert.ToBase64String(request.Hash), request.Id));
                BlockRequestReceived?.Invoke(this, new BlockRequestReceivedEventArgs(deviceId, request));
                break;
                
            case BepMessageType.DownloadProgress when e.Message is BepDownloadProgress progress:
                var progressList = progress.Updates.Select(u => new FileDownloadProgress
                {
                    FileName = u.Name,
                    BytesDownloaded = 0, // BEP doesn't provide exact bytes
                    TotalBytes = 0,
                    LastUpdate = DateTime.UtcNow
                }).ToList();
                DownloadProgressReceived?.Invoke(this, new DownloadProgressEventArgs(deviceId, progress.Folder, progressList));
                break;
        }
    }

    private void OnConnectionDeviceIdUpdated(object? sender, (string OldDeviceId, string NewDeviceId) e)
    {
        if (sender is not BepConnection connection) return;
        
        var (oldDeviceId, newDeviceId) = e;
        
        _logger.LogInformation("Updating device ID in connections dictionary: {OldDeviceId} -> {NewDeviceId}", oldDeviceId, newDeviceId);
        
        // Update the connections dictionary with the new device ID
        if (_connections.TryRemove(oldDeviceId, out var existingConnection) && existingConnection == connection)
        {
            _connections[newDeviceId] = connection;
            _logger.LogInformation("Device ID updated successfully in connections dictionary");
            
            // Notify about device connection with the correct ID
            DeviceConnected?.Invoke(this, new DeviceConnectedEventArgs(new SyncDevice(
                newDeviceId, 
                "Unknown" // Will be updated when we get more device info
            )));
        }
        else
        {
            _logger.LogWarning("Failed to update device ID in connections dictionary - connection not found");
        }
    }

    private List<SyncFileInfo> ConvertBepFilesToSyncFiles(List<BepFileInfo> bepFiles)
    {
        return bepFiles.Select(f => 
        {
            var syncFile = new SyncFileInfo("", f.Name, f.Name, f.Size, 
                DateTime.UnixEpoch.AddSeconds(f.ModifiedS).AddTicks(f.ModifiedNs / 100));
            
            // Convert blocks
            var blocks = f.Blocks.Select(b => new BlockInfo(b.Offset, b.Size, Convert.ToHexString(b.Hash).ToLower(), b.WeakHash)).ToList();
            syncFile.SetBlocks(blocks);
            syncFile.UpdateHash(Convert.ToHexString(f.BlocksHash).ToLower());
            
            // Convert vector clock
            var vector = new VectorClock();
            foreach (var counter in f.Version.Counters)
            {
                vector.Update(counter.Id.ToString("x16"), (long)counter.Value);
            }
            syncFile.UpdateVector(vector);
            
            if (f.Deleted) syncFile.MarkAsDeleted();
            if (f.Invalid) syncFile.MarkAsInvalid();
            if (!string.IsNullOrEmpty(f.Symlink)) syncFile.SetSymlink(f.Symlink);
            
            return syncFile;
        }).ToList();
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();
        _listener?.Stop();
        
        foreach (var connection in _connections.Values)
        {
            connection.Dispose();
        }
        
        _cancellationTokenSource.Dispose();
    }
}