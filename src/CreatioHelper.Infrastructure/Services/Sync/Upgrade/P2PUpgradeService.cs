using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Infrastructure.Services.Sync.Proto;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Upgrade;

/// <summary>
/// Configuration options for P2P agent upgrades
/// </summary>
public class P2PUpgradeOptions
{
    /// <summary>
    /// Enable P2P upgrade functionality
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Time of day to apply updates (format: "HH:mm")
    /// </summary>
    public string ScheduleTime { get; set; } = "03:00";

    /// <summary>
    /// Schedule type: Daily, Weekdays, Weekend, Manual
    /// </summary>
    public string ScheduleDays { get; set; } = "Daily";

    /// <summary>
    /// Automatically download updates when available
    /// </summary>
    public bool AutoDownload { get; set; } = true;

    /// <summary>
    /// Maximum chunk size for binary transfer (default 1MB)
    /// </summary>
    public int MaxChunkSize { get; set; } = 1024 * 1024;

    /// <summary>
    /// Directory for storing downloaded updates
    /// </summary>
    public string DownloadDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CreatioHelper", "P2PUpdates");

    /// <summary>
    /// Timeout for update requests in seconds
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 300;
}

/// <summary>
/// Information about a peer's agent version
/// </summary>
public record PeerVersionInfo
{
    public string DeviceId { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string BinaryHash { get; init; } = string.Empty;
    public long BinarySize { get; init; }
    public string Platform { get; init; } = string.Empty;
    public DateTime DiscoveredAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Status of a pending update download
/// </summary>
public record P2PUpdateDownload
{
    public string Version { get; init; } = string.Empty;
    public string SourceDeviceId { get; init; } = string.Empty;
    public string Platform { get; init; } = string.Empty;
    public string ExpectedHash { get; init; } = string.Empty;
    public long TotalSize { get; init; }
    public long DownloadedSize { get; set; }
    public int TotalChunks { get; init; }
    public int ReceivedChunks { get; set; }
    public string TempFilePath { get; init; } = string.Empty;
    public P2PDownloadState State { get; set; } = P2PDownloadState.Pending;
    public string? Error { get; set; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
}

public enum P2PDownloadState
{
    Pending,
    Downloading,
    Verifying,
    Ready,
    Failed
}

/// <summary>
/// Event args for peer version discovered
/// </summary>
public class PeerVersionDiscoveredEventArgs : EventArgs
{
    public PeerVersionInfo PeerInfo { get; init; } = null!;
    public bool IsNewer { get; init; }
}

/// <summary>
/// Event args for update download progress
/// </summary>
public class P2PUpdateProgressEventArgs : EventArgs
{
    public string Version { get; init; } = string.Empty;
    public double ProgressPercent { get; init; }
    public long BytesReceived { get; init; }
    public long TotalBytes { get; init; }
}

/// <summary>
/// Interface for P2P upgrade service
/// </summary>
public interface IP2PUpgradeService
{
    /// <summary>
    /// Current agent version
    /// </summary>
    Version CurrentVersion { get; }

    /// <summary>
    /// Current platform identifier
    /// </summary>
    string CurrentPlatform { get; }

    /// <summary>
    /// SHA256 hash of current agent binary
    /// </summary>
    string CurrentBinaryHash { get; }

    /// <summary>
    /// Size of current agent binary
    /// </summary>
    long CurrentBinarySize { get; }

    /// <summary>
    /// Known peer versions
    /// </summary>
    IReadOnlyDictionary<string, PeerVersionInfo> KnownPeerVersions { get; }

    /// <summary>
    /// Get the best available update source
    /// </summary>
    PeerVersionInfo? GetBestUpdateSource();

    /// <summary>
    /// Request update from a specific peer
    /// </summary>
    Task<bool> RequestUpdateFromPeerAsync(string deviceId, CancellationToken ct = default);

    /// <summary>
    /// Check if an update is ready to apply
    /// </summary>
    bool IsUpdateReady();

    /// <summary>
    /// Apply downloaded update
    /// </summary>
    Task<bool> ApplyUpdateAsync(CancellationToken ct = default);

    /// <summary>
    /// Handle agent update request from peer
    /// </summary>
    Task HandleUpdateRequestAsync(string fromDeviceId, BepAgentUpdateRequest request, Func<BepAgentUpdateResponse, Task> sendResponse, CancellationToken ct = default);

    /// <summary>
    /// Handle agent update response from peer
    /// </summary>
    Task HandleUpdateResponseAsync(string fromDeviceId, BepAgentUpdateResponse response, CancellationToken ct = default);

    /// <summary>
    /// Register peer version from Hello message
    /// </summary>
    void RegisterPeerVersion(string deviceId, string deviceName, string version, BepHelloExtensions? extensions);

    /// <summary>
    /// Event raised when a new peer version is discovered
    /// </summary>
    event EventHandler<PeerVersionDiscoveredEventArgs>? PeerVersionDiscovered;

    /// <summary>
    /// Event raised when update download progress changes
    /// </summary>
    event EventHandler<P2PUpdateProgressEventArgs>? UpdateProgress;

    /// <summary>
    /// Event raised when update is ready to apply
    /// </summary>
    event EventHandler<PeerVersionInfo>? UpdateReady;
}

/// <summary>
/// P2P agent upgrade service that distributes updates through the BEP protocol network
/// </summary>
public class P2PUpgradeService : IP2PUpgradeService, IHostedService, IDisposable
{
    private readonly ILogger<P2PUpgradeService> _logger;
    private readonly P2PUpgradeOptions _options;
    private readonly ISyncProtocol? _syncProtocol;

    private readonly Dictionary<string, PeerVersionInfo> _peerVersions = new();
    private readonly object _peerLock = new();

    private P2PUpdateDownload? _currentDownload;
    private FileStream? _downloadStream;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    private Version? _currentVersion;
    private string? _currentBinaryHash;
    private long _currentBinarySize;
    private string? _currentPlatform;

    private Timer? _scheduleTimer;
    private CancellationTokenSource? _cts;

    public event EventHandler<PeerVersionDiscoveredEventArgs>? PeerVersionDiscovered;
    public event EventHandler<P2PUpdateProgressEventArgs>? UpdateProgress;
    public event EventHandler<PeerVersionInfo>? UpdateReady;

    public Version CurrentVersion => _currentVersion ??= GetCurrentVersion();
    public string CurrentPlatform => _currentPlatform ??= GetCurrentPlatform();
    public string CurrentBinaryHash => _currentBinaryHash ??= ComputeCurrentBinaryHash();
    public long CurrentBinarySize => _currentBinarySize > 0 ? _currentBinarySize : (_currentBinarySize = GetCurrentBinarySize());

    public IReadOnlyDictionary<string, PeerVersionInfo> KnownPeerVersions
    {
        get
        {
            lock (_peerLock)
            {
                return new Dictionary<string, PeerVersionInfo>(_peerVersions);
            }
        }
    }

    public P2PUpgradeService(
        ILogger<P2PUpgradeService> logger,
        P2PUpgradeOptions options,
        ISyncProtocol? syncProtocol = null)
    {
        _logger = logger;
        _options = options;
        _syncProtocol = syncProtocol;

        // Ensure download directory exists (use default if not configured)
        if (string.IsNullOrEmpty(_options.DownloadDirectory))
        {
            _options.DownloadDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CreatioHelper", "P2PUpdates");
        }
        Directory.CreateDirectory(_options.DownloadDirectory);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation("P2P upgrade service is disabled");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _logger.LogInformation("P2P upgrade service started. Version: {Version}, Platform: {Platform}, Hash: {Hash}",
            CurrentVersion, CurrentPlatform,
            (CurrentBinaryHash.Length >= 16 ? CurrentBinaryHash[..16] : CurrentBinaryHash) + "...");

        // Set up schedule timer
        SetupScheduleTimer();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("P2P upgrade service stopping");

        _cts?.Cancel();
        _scheduleTimer?.Dispose();
        _downloadStream?.Dispose();

        return Task.CompletedTask;
    }

    public void RegisterPeerVersion(string deviceId, string deviceName, string version, BepHelloExtensions? extensions)
    {
        if (string.IsNullOrEmpty(version))
        {
            return;
        }

        var peerInfo = new PeerVersionInfo
        {
            DeviceId = deviceId,
            DeviceName = deviceName,
            Version = version,
            BinaryHash = extensions?.AgentBinaryHash ?? string.Empty,
            BinarySize = extensions?.AgentBinarySize ?? 0,
            Platform = extensions?.AgentPlatform ?? string.Empty,
            DiscoveredAt = DateTime.UtcNow
        };

        bool isNewer;
        lock (_peerLock)
        {
            _peerVersions[deviceId] = peerInfo;
            isNewer = IsVersionNewer(version);
        }

        _logger.LogInformation("Discovered peer version: {DeviceName} ({DeviceId}) running v{Version} on {Platform}",
            deviceName, deviceId[..8], version, peerInfo.Platform);

        PeerVersionDiscovered?.Invoke(this, new PeerVersionDiscoveredEventArgs
        {
            PeerInfo = peerInfo,
            IsNewer = isNewer
        });

        // Auto-download if enabled and version is newer with matching platform
        if (_options.AutoDownload && isNewer &&
            !string.IsNullOrEmpty(peerInfo.BinaryHash) &&
            IsPlatformCompatible(peerInfo.Platform))
        {
            _logger.LogInformation("Newer version v{Version} available from {DeviceName}. Starting auto-download.",
                version, deviceName);

            _ = Task.Run(async () =>
            {
                try
                {
                    await RequestUpdateFromPeerAsync(deviceId, _cts?.Token ?? CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to auto-download update from {DeviceId}", deviceId);
                }
            });
        }
    }

    public PeerVersionInfo? GetBestUpdateSource()
    {
        lock (_peerLock)
        {
            return _peerVersions.Values
                .Where(p => IsVersionNewer(p.Version))
                .Where(p => IsPlatformCompatible(p.Platform))
                .Where(p => !string.IsNullOrEmpty(p.BinaryHash))
                .OrderByDescending(p => Version.TryParse(p.Version, out var v) ? v : new Version(0, 0))
                .ThenByDescending(p => p.BinarySize) // Prefer peers with larger binaries (might be complete)
                .FirstOrDefault();
        }
    }

    public async Task<bool> RequestUpdateFromPeerAsync(string deviceId, CancellationToken ct = default)
    {
        if (_syncProtocol == null)
        {
            _logger.LogWarning("Cannot request update - no sync protocol available");
            return false;
        }

        PeerVersionInfo? peerInfo;
        lock (_peerLock)
        {
            if (!_peerVersions.TryGetValue(deviceId, out peerInfo))
            {
                _logger.LogWarning("Unknown device {DeviceId}", deviceId);
                return false;
            }
        }

        await _downloadLock.WaitAsync(ct);
        try
        {
            // Check if we already have this version downloaded
            if (_currentDownload?.Version == peerInfo.Version && _currentDownload.State == P2PDownloadState.Ready)
            {
                _logger.LogInformation("Update v{Version} is already downloaded and ready", peerInfo.Version);
                return true;
            }

            // Initialize download tracking
            var tempPath = Path.Combine(_options.DownloadDirectory, $"update-{peerInfo.Version}-{Guid.NewGuid():N}.tmp");

            _currentDownload = new P2PUpdateDownload
            {
                Version = peerInfo.Version,
                SourceDeviceId = deviceId,
                Platform = peerInfo.Platform,
                ExpectedHash = peerInfo.BinaryHash,
                TotalSize = peerInfo.BinarySize,
                TotalChunks = (int)Math.Ceiling((double)peerInfo.BinarySize / _options.MaxChunkSize),
                TempFilePath = tempPath,
                State = P2PDownloadState.Pending
            };

            _downloadStream?.Dispose();
            _downloadStream = null;
            try
            {
                _downloadStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create download file at {Path}", tempPath);
                throw;
            }

            _logger.LogInformation("Requesting update v{Version} from {DeviceName} ({ChunkCount} chunks)",
                peerInfo.Version, peerInfo.DeviceName, _currentDownload.TotalChunks);

            // Send update request
            var request = new BepAgentUpdateRequest
            {
                FromVersion = CurrentVersion.ToString(),
                ToVersion = peerInfo.Version,
                Platform = CurrentPlatform
            };

            // The actual sending is done by BepConnection - we just prepare the request
            // The caller (BepConnection/SyncEngine) will send this via the protocol
            _currentDownload.State = P2PDownloadState.Downloading;

            return true;
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    public async Task HandleUpdateRequestAsync(string fromDeviceId, BepAgentUpdateRequest request, Func<BepAgentUpdateResponse, Task> sendResponse, CancellationToken ct = default)
    {
        _logger.LogInformation("Received update request from {DeviceId}: v{FromVersion} -> v{ToVersion} ({Platform})",
            fromDeviceId[..8], request.FromVersion, request.ToVersion, request.Platform);

        // Check if we have the requested version (we can only provide our current version)
        if (request.ToVersion != CurrentVersion.ToString())
        {
            _logger.LogWarning("Cannot provide version {RequestedVersion}, current version is {CurrentVersion}",
                request.ToVersion, CurrentVersion);

            await sendResponse(new BepAgentUpdateResponse
            {
                Version = CurrentVersion.ToString(),
                Platform = CurrentPlatform,
                Error = BepErrorCode.InvalidFile
            });
            return;
        }

        // Check platform compatibility
        if (!IsPlatformCompatible(request.Platform))
        {
            _logger.LogWarning("Platform mismatch: requested {RequestedPlatform}, current {CurrentPlatform}",
                request.Platform, CurrentPlatform);

            await sendResponse(new BepAgentUpdateResponse
            {
                Version = CurrentVersion.ToString(),
                Platform = CurrentPlatform,
                Error = BepErrorCode.InvalidFile
            });
            return;
        }

        // Get current binary path
        var binaryPath = GetCurrentBinaryPath();
        if (!File.Exists(binaryPath))
        {
            _logger.LogError("Cannot find current binary at {Path}", binaryPath);

            await sendResponse(new BepAgentUpdateResponse
            {
                Version = CurrentVersion.ToString(),
                Platform = CurrentPlatform,
                Error = BepErrorCode.NoSuchFile
            });
            return;
        }

        // Send binary in chunks
        await using var fileStream = new FileStream(binaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var totalSize = fileStream.Length;
        var totalChunks = (int)Math.Ceiling((double)totalSize / _options.MaxChunkSize);
        var chunkIndex = 0;
        var buffer = new byte[_options.MaxChunkSize];

        _logger.LogInformation("Sending update v{Version} to {DeviceId} ({TotalSize} bytes in {ChunkCount} chunks)",
            CurrentVersion, fromDeviceId[..8], totalSize, totalChunks);

        while (!ct.IsCancellationRequested)
        {
            var bytesRead = await fileStream.ReadAsync(buffer.AsMemory(0, _options.MaxChunkSize), ct);
            if (bytesRead == 0) break;

            var isLastChunk = chunkIndex == totalChunks - 1;
            var chunkData = new byte[bytesRead];
            Array.Copy(buffer, chunkData, bytesRead);

            await sendResponse(new BepAgentUpdateResponse
            {
                Version = CurrentVersion.ToString(),
                Platform = CurrentPlatform,
                Data = chunkData,
                Hash = CurrentBinaryHash,
                TotalSize = totalSize,
                ChunkIndex = chunkIndex,
                TotalChunks = totalChunks,
                IsComplete = isLastChunk,
                Error = BepErrorCode.NoError
            });

            chunkIndex++;
        }

        _logger.LogInformation("Completed sending update to {DeviceId}", fromDeviceId[..8]);
    }

    public async Task HandleUpdateResponseAsync(string fromDeviceId, BepAgentUpdateResponse response, CancellationToken ct = default)
    {
        await _downloadLock.WaitAsync(ct);
        try
        {
            if (_currentDownload == null || _currentDownload.SourceDeviceId != fromDeviceId)
            {
                _logger.LogWarning("Received unexpected update response from {DeviceId}", fromDeviceId[..8]);
                return;
            }

            if (response.Error != BepErrorCode.NoError)
            {
                _logger.LogError("Update request failed with error: {Error}", response.Error);
                _currentDownload.State = P2PDownloadState.Failed;
                _currentDownload.Error = response.Error.ToString();
                return;
            }

            // Write chunk to file
            if (_downloadStream != null && response.Data.Length > 0)
            {
                await _downloadStream.WriteAsync(response.Data, ct);
                _currentDownload.DownloadedSize += response.Data.Length;
                _currentDownload.ReceivedChunks++;

                var progress = (double)_currentDownload.DownloadedSize / _currentDownload.TotalSize * 100;

                _logger.LogDebug("Received chunk {Chunk}/{Total} from {DeviceId} ({Progress:F1}%)",
                    _currentDownload.ReceivedChunks, _currentDownload.TotalChunks, fromDeviceId[..8], progress);

                UpdateProgress?.Invoke(this, new P2PUpdateProgressEventArgs
                {
                    Version = _currentDownload.Version,
                    ProgressPercent = progress,
                    BytesReceived = _currentDownload.DownloadedSize,
                    TotalBytes = _currentDownload.TotalSize
                });
            }

            // Check if download is complete - use only chunk count to avoid premature completion
            if (_currentDownload.ReceivedChunks >= _currentDownload.TotalChunks)
            {
                await _downloadStream!.FlushAsync(ct);
                _downloadStream.Close();
                _downloadStream = null;

                _currentDownload.State = P2PDownloadState.Verifying;
                _logger.LogInformation("Download complete, verifying hash...");

                // Verify hash
                var actualHash = await ComputeFileHashAsync(_currentDownload.TempFilePath, ct);
                if (actualHash.Equals(_currentDownload.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    _currentDownload.State = P2PDownloadState.Ready;
                    _logger.LogInformation("Update v{Version} downloaded and verified successfully", _currentDownload.Version);

                    PeerVersionInfo? peerInfo;
                    lock (_peerLock)
                    {
                        _peerVersions.TryGetValue(fromDeviceId, out peerInfo);
                    }

                    if (peerInfo != null)
                    {
                        UpdateReady?.Invoke(this, peerInfo);
                    }
                }
                else
                {
                    _currentDownload.State = P2PDownloadState.Failed;
                    _currentDownload.Error = "Hash verification failed";
                    _logger.LogError("Hash verification failed. Expected: {Expected}, Actual: {Actual}",
                        _currentDownload.ExpectedHash.Length >= 16 ? _currentDownload.ExpectedHash[..16] : _currentDownload.ExpectedHash,
                        actualHash.Length >= 16 ? actualHash[..16] : actualHash);

                    // Clean up failed download
                    try
                    {
                        File.Delete(_currentDownload.TempFilePath);
                    }
                    catch { }
                }
            }
        }
        finally
        {
            _downloadLock.Release();
        }
    }

    public bool IsUpdateReady()
    {
        return _currentDownload?.State == P2PDownloadState.Ready;
    }

    public async Task<bool> ApplyUpdateAsync(CancellationToken ct = default)
    {
        if (_currentDownload?.State != P2PDownloadState.Ready)
        {
            _logger.LogWarning("No update ready to apply");
            return false;
        }

        var updatePath = _currentDownload.TempFilePath;
        if (!File.Exists(updatePath))
        {
            _logger.LogError("Update file not found: {Path}", updatePath);
            return false;
        }

        try
        {
            var currentBinaryPath = GetCurrentBinaryPath();
            var backupPath = currentBinaryPath + ".backup";
            var newBinaryPath = currentBinaryPath + ".new";

            _logger.LogInformation("Applying update v{Version}...", _currentDownload.Version);

            // Copy new binary to .new file
            File.Copy(updatePath, newBinaryPath, true);

            // On Windows, we need to rename files since the running executable can't be replaced
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // Backup current binary
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
                File.Move(currentBinaryPath, backupPath);

                // Move new binary to current location
                File.Move(newBinaryPath, currentBinaryPath);

                _logger.LogInformation("Update applied successfully. Restart required to complete.");
            }
            else
            {
                // On Unix, we can use atomic rename
                File.Move(newBinaryPath, currentBinaryPath, true);
                _logger.LogInformation("Update applied successfully. Restart required to complete.");
            }

            // Clean up
            File.Delete(updatePath);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply update");
            return false;
        }
    }

    #region Private Methods

    private void SetupScheduleTimer()
    {
        if (_options.ScheduleDays == "Manual")
        {
            _logger.LogInformation("Automatic update application disabled (Manual mode)");
            return;
        }

        // Parse schedule time
        if (!TimeSpan.TryParse(_options.ScheduleTime, out var scheduleTime))
        {
            scheduleTime = new TimeSpan(3, 0, 0); // Default to 3:00 AM
        }

        // Calculate next run time
        var now = DateTime.Now;
        var nextRun = now.Date + scheduleTime;
        if (nextRun <= now)
        {
            nextRun = nextRun.AddDays(1);
        }

        // Adjust for schedule type
        while (!IsScheduledDay(nextRun))
        {
            nextRun = nextRun.AddDays(1);
        }

        var initialDelay = nextRun - now;
        _logger.LogInformation("Next scheduled update check: {NextRun} ({Delay})", nextRun, initialDelay);

        _scheduleTimer = new Timer(OnScheduledUpdate, null, initialDelay, TimeSpan.FromDays(1));
    }

    private bool IsScheduledDay(DateTime date)
    {
        return _options.ScheduleDays switch
        {
            "Daily" => true,
            "Weekdays" => date.DayOfWeek >= DayOfWeek.Monday && date.DayOfWeek <= DayOfWeek.Friday,
            "Weekend" => date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday,
            _ => false
        };
    }

    private void OnScheduledUpdate(object? state)
    {
        // Fire-and-forget with proper error handling via the async method
        _ = OnScheduledUpdateAsync();
    }

    private async Task OnScheduledUpdateAsync()
    {
        if (_cts?.IsCancellationRequested == true)
        {
            return;
        }

        if (!IsScheduledDay(DateTime.Now))
        {
            return;
        }

        _logger.LogInformation("Running scheduled update check");

        try
        {
            if (IsUpdateReady())
            {
                _logger.LogInformation("Applying scheduled update...");
                await ApplyUpdateAsync(_cts?.Token ?? CancellationToken.None);
            }
            else
            {
                // Try to download update from best source
                var bestSource = GetBestUpdateSource();
                if (bestSource != null)
                {
                    _logger.LogInformation("Downloading update from {DeviceName}...", bestSource.DeviceName);
                    await RequestUpdateFromPeerAsync(bestSource.DeviceId, _cts?.Token ?? CancellationToken.None);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during scheduled update");
        }
    }

    private bool IsVersionNewer(string versionString)
    {
        if (!Version.TryParse(versionString, out var version))
        {
            return false;
        }

        return version > CurrentVersion;
    }

    private bool IsPlatformCompatible(string platform)
    {
        if (string.IsNullOrEmpty(platform))
        {
            return false;
        }

        return platform.Equals(CurrentPlatform, StringComparison.OrdinalIgnoreCase);
    }

    private static Version GetCurrentVersion()
    {
        var assembly = typeof(P2PUpgradeService).Assembly;
        return assembly.GetName().Version ?? new Version(1, 0, 0);
    }

    private static string GetCurrentPlatform()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" :
                 RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
                 RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "darwin" : "unknown";

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => "unknown"
        };

        return $"{os}-{arch}";
    }

    private static string GetCurrentBinaryPath()
    {
        return Process.GetCurrentProcess().MainModule?.FileName
               ?? throw new InvalidOperationException("Cannot determine current binary path");
    }

    private string ComputeCurrentBinaryHash()
    {
        try
        {
            var binaryPath = GetCurrentBinaryPath();
            if (!File.Exists(binaryPath))
            {
                return string.Empty;
            }

            using var stream = new FileStream(binaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute binary hash");
            return string.Empty;
        }
    }

    private long GetCurrentBinarySize()
    {
        try
        {
            var binaryPath = GetCurrentBinaryPath();
            if (!File.Exists(binaryPath))
            {
                return 0;
            }

            return new System.IO.FileInfo(binaryPath).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<string> ComputeFileHashAsync(string filePath, CancellationToken ct)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    #endregion

    public void Dispose()
    {
        _scheduleTimer?.Dispose();
        _downloadStream?.Dispose();
        _downloadLock.Dispose();
        _cts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
