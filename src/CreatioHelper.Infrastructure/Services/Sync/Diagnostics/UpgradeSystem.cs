using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Diagnostics;

/// <summary>
/// Release information
/// </summary>
public record ReleaseInfo
{
    [JsonPropertyName("tag")]
    public string Tag { get; init; } = string.Empty;

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; init; }

    [JsonPropertyName("assets")]
    public List<ReleaseAsset> Assets { get; init; } = new();

    [JsonPropertyName("changelog")]
    public string Changelog { get; init; } = string.Empty;

    [JsonPropertyName("publishedAt")]
    public DateTime PublishedAt { get; init; }

    public Version? ParsedVersion => Version.TryParse(Tag.TrimStart('v'), out var v) ? v : null;
}

/// <summary>
/// Release asset (downloadable file)
/// </summary>
public record ReleaseAsset
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;
}

/// <summary>
/// Upgrade check result
/// </summary>
public record UpgradeCheckResult
{
    public bool UpdateAvailable { get; init; }
    public ReleaseInfo? Release { get; init; }
    public ReleaseAsset? Asset { get; init; }
    public string? Error { get; init; }
    public Version? CurrentVersion { get; init; }
    public Version? NewVersion { get; init; }
}

/// <summary>
/// Upgrade progress
/// </summary>
public record UpgradeProgress
{
    public UpgradeState State { get; init; }
    public double ProgressPercent { get; init; }
    public string Message { get; init; } = string.Empty;
    public long BytesDownloaded { get; init; }
    public long TotalBytes { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Upgrade states
/// </summary>
public enum UpgradeState
{
    Idle,
    CheckingForUpdate,
    DownloadingUpdate,
    VerifyingUpdate,
    ExtractingUpdate,
    InstallingUpdate,
    RestartRequired,
    Failed,
    UpToDate
}

/// <summary>
/// Configuration for upgrade system
/// </summary>
public class UpgradeOptions
{
    /// <summary>
    /// URL to check for releases
    /// </summary>
    public string ReleaseUrl { get; set; } = "https://api.github.com/repos/fejdraus/CreatioHelper/releases";

    /// <summary>
    /// Whether to check for updates automatically
    /// </summary>
    public bool AutoCheck { get; set; } = true;

    /// <summary>
    /// Whether to download updates automatically
    /// </summary>
    public bool AutoDownload { get; set; }

    /// <summary>
    /// Whether to apply updates automatically (requires restart)
    /// </summary>
    public bool AutoUpgrade { get; set; }

    /// <summary>
    /// Include prerelease versions
    /// </summary>
    public bool IncludePrereleases { get; set; }

    /// <summary>
    /// Check interval
    /// </summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// Download directory for updates
    /// </summary>
    public string DownloadDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CreatioHelper", "Updates");
}

/// <summary>
/// Provides automatic update functionality (based on Syncthing lib/upgrade/)
/// </summary>
public interface IUpgradeService
{
    /// <summary>
    /// Current upgrade state
    /// </summary>
    UpgradeState State { get; }

    /// <summary>
    /// Check for available updates
    /// </summary>
    Task<UpgradeCheckResult> CheckForUpdateAsync(CancellationToken ct = default);

    /// <summary>
    /// Download an update
    /// </summary>
    Task<bool> DownloadUpdateAsync(ReleaseInfo release, IProgress<UpgradeProgress>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Apply downloaded update
    /// </summary>
    Task<bool> ApplyUpdateAsync(CancellationToken ct = default);

    /// <summary>
    /// Get current version
    /// </summary>
    Version GetCurrentVersion();

    /// <summary>
    /// Get upgrade progress
    /// </summary>
    UpgradeProgress GetProgress();

    /// <summary>
    /// Start automatic update checking
    /// </summary>
    void StartAutoCheck(CancellationToken ct = default);

    /// <summary>
    /// Stop automatic update checking
    /// </summary>
    void StopAutoCheck();

    /// <summary>
    /// Event raised when update is available
    /// </summary>
    event EventHandler<UpgradeCheckResult>? UpdateAvailable;
}

/// <summary>
/// Implementation of upgrade system (based on Syncthing lib/upgrade/)
/// </summary>
public class UpgradeService : IUpgradeService, IDisposable
{
    private readonly ILogger<UpgradeService> _logger;
    private readonly HttpClient _httpClient;
    private readonly UpgradeOptions _options;

    private UpgradeState _state = UpgradeState.Idle;
    private UpgradeProgress _progress = new() { State = UpgradeState.Idle };
    private string? _downloadedUpdatePath;
    private CancellationTokenSource? _autoCheckCts;
    private Task? _autoCheckTask;

    public UpgradeState State => _state;
    public event EventHandler<UpgradeCheckResult>? UpdateAvailable;

    public UpgradeService(
        ILogger<UpgradeService> logger,
        HttpClient httpClient,
        UpgradeOptions options)
    {
        _logger = logger;
        _httpClient = httpClient;
        _options = options;

        // Ensure download directory exists
        Directory.CreateDirectory(_options.DownloadDirectory);
    }

    public async Task<UpgradeCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        _state = UpgradeState.CheckingForUpdate;
        UpdateProgress(UpgradeState.CheckingForUpdate, 0, "Checking for updates...");

        try
        {
            var currentVersion = GetCurrentVersion();

            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("CreatioHelper");
            var releases = await _httpClient.GetFromJsonAsync<List<ReleaseInfo>>(_options.ReleaseUrl, ct);

            if (releases == null || releases.Count == 0)
            {
                _state = UpgradeState.UpToDate;
                return new UpgradeCheckResult
                {
                    UpdateAvailable = false,
                    CurrentVersion = currentVersion,
                    Error = "No releases found"
                };
            }

            // Find latest applicable release
            var latestRelease = releases
                .Where(r => _options.IncludePrereleases || !r.Prerelease)
                .Where(r => r.ParsedVersion != null)
                .OrderByDescending(r => r.ParsedVersion)
                .FirstOrDefault();

            if (latestRelease == null || latestRelease.ParsedVersion == null)
            {
                _state = UpgradeState.UpToDate;
                return new UpgradeCheckResult
                {
                    UpdateAvailable = false,
                    CurrentVersion = currentVersion
                };
            }

            if (latestRelease.ParsedVersion <= currentVersion)
            {
                _state = UpgradeState.UpToDate;
                _logger.LogDebug("Already running latest version: {Version}", currentVersion);
                return new UpgradeCheckResult
                {
                    UpdateAvailable = false,
                    CurrentVersion = currentVersion,
                    NewVersion = latestRelease.ParsedVersion
                };
            }

            // Find appropriate asset for current platform
            var asset = FindAssetForPlatform(latestRelease);

            var result = new UpgradeCheckResult
            {
                UpdateAvailable = true,
                Release = latestRelease,
                Asset = asset,
                CurrentVersion = currentVersion,
                NewVersion = latestRelease.ParsedVersion
            };

            _logger.LogInformation("Update available: {CurrentVersion} -> {NewVersion}",
                currentVersion, latestRelease.ParsedVersion);

            _state = UpgradeState.Idle;
            UpdateAvailable?.Invoke(this, result);

            return result;
        }
        catch (Exception ex)
        {
            _state = UpgradeState.Failed;
            _logger.LogError(ex, "Error checking for updates");
            return new UpgradeCheckResult
            {
                UpdateAvailable = false,
                Error = ex.Message
            };
        }
    }

    public async Task<bool> DownloadUpdateAsync(ReleaseInfo release, IProgress<UpgradeProgress>? progress = null, CancellationToken ct = default)
    {
        var asset = FindAssetForPlatform(release);
        if (asset == null)
        {
            _logger.LogError("No suitable asset found for current platform");
            return false;
        }

        _state = UpgradeState.DownloadingUpdate;
        UpdateProgress(UpgradeState.DownloadingUpdate, 0, $"Downloading {asset.Name}...");

        try
        {
            var downloadPath = Path.Combine(_options.DownloadDirectory, asset.Name);

            using var response = await _httpClient.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? asset.Size;
            var downloadedBytes = 0L;

            await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            int bytesRead;

            while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                downloadedBytes += bytesRead;

                var percent = totalBytes > 0 ? (double)downloadedBytes / totalBytes * 100 : 0;
                UpdateProgress(UpgradeState.DownloadingUpdate, percent, $"Downloading... {downloadedBytes / 1024 / 1024}MB / {totalBytes / 1024 / 1024}MB", downloadedBytes, totalBytes);
                progress?.Report(_progress);
            }

            // Verify download
            _state = UpgradeState.VerifyingUpdate;
            UpdateProgress(UpgradeState.VerifyingUpdate, 100, "Verifying download...");

            if (!string.IsNullOrEmpty(asset.Sha256))
            {
                var hash = await ComputeFileHashAsync(downloadPath, ct);
                if (!hash.Equals(asset.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogError("Hash mismatch: expected {Expected}, got {Actual}", asset.Sha256, hash);
                    File.Delete(downloadPath);
                    _state = UpgradeState.Failed;
                    return false;
                }
            }

            _downloadedUpdatePath = downloadPath;
            _state = UpgradeState.RestartRequired;
            UpdateProgress(UpgradeState.RestartRequired, 100, "Update downloaded. Restart required to apply.");

            _logger.LogInformation("Update downloaded successfully: {Path}", downloadPath);
            return true;
        }
        catch (Exception ex)
        {
            _state = UpgradeState.Failed;
            _logger.LogError(ex, "Error downloading update");
            UpdateProgress(UpgradeState.Failed, 0, "Download failed", error: ex.Message);
            return false;
        }
    }

    public async Task<bool> ApplyUpdateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_downloadedUpdatePath) || !File.Exists(_downloadedUpdatePath))
        {
            _logger.LogError("No downloaded update to apply");
            return false;
        }

        _state = UpgradeState.InstallingUpdate;
        UpdateProgress(UpgradeState.InstallingUpdate, 0, "Installing update...");

        try
        {
            var currentExe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(currentExe))
            {
                _logger.LogError("Could not determine current executable path");
                return false;
            }

            var installDir = Path.GetDirectoryName(currentExe)!;
            var backupDir = Path.Combine(_options.DownloadDirectory, "backup");

            // Create backup
            if (Directory.Exists(backupDir))
                Directory.Delete(backupDir, true);
            Directory.CreateDirectory(backupDir);

            // Backup current installation
            foreach (var file in Directory.GetFiles(installDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(installDir, file);
                var backupPath = Path.Combine(backupDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(file, backupPath, true);
            }

            UpdateProgress(UpgradeState.ExtractingUpdate, 50, "Extracting update...");

            // Extract update
            if (_downloadedUpdatePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var extractDir = Path.Combine(_options.DownloadDirectory, "extract");
                if (Directory.Exists(extractDir))
                    Directory.Delete(extractDir, true);

                ZipFile.ExtractToDirectory(_downloadedUpdatePath, extractDir);

                // Copy extracted files
                foreach (var file in Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories))
                {
                    var relativePath = Path.GetRelativePath(extractDir, file);
                    var destPath = Path.Combine(installDir, relativePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

                    // Skip the currently running executable
                    if (destPath.Equals(currentExe, StringComparison.OrdinalIgnoreCase))
                    {
                        // Rename current exe and copy new one
                        var oldExe = currentExe + ".old";
                        File.Move(currentExe, oldExe, true);
                        File.Copy(file, destPath, true);
                    }
                    else
                    {
                        File.Copy(file, destPath, true);
                    }
                }

                Directory.Delete(extractDir, true);
            }

            UpdateProgress(UpgradeState.RestartRequired, 100, "Update installed. Restart required.");

            _logger.LogInformation("Update installed successfully. Restart required.");
            return true;
        }
        catch (Exception ex)
        {
            _state = UpgradeState.Failed;
            _logger.LogError(ex, "Error applying update");
            UpdateProgress(UpgradeState.Failed, 0, "Installation failed", error: ex.Message);
            return false;
        }
    }

    public Version GetCurrentVersion()
    {
        var assembly = typeof(UpgradeService).Assembly;
        return assembly.GetName().Version ?? new Version(1, 0, 0);
    }

    public UpgradeProgress GetProgress() => _progress;

    public void StartAutoCheck(CancellationToken ct = default)
    {
        if (_autoCheckTask != null)
            return;

        _autoCheckCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _autoCheckTask = AutoCheckLoopAsync(_autoCheckCts.Token);

        _logger.LogInformation("Started automatic update checking");
    }

    public void StopAutoCheck()
    {
        _autoCheckCts?.Cancel();
        _autoCheckTask = null;
        _autoCheckCts = null;

        _logger.LogInformation("Stopped automatic update checking");
    }

    private async Task AutoCheckLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.CheckInterval, ct);

                var result = await CheckForUpdateAsync(ct);

                if (result.UpdateAvailable && result.Release != null)
                {
                    if (_options.AutoDownload)
                    {
                        await DownloadUpdateAsync(result.Release, null, ct);
                    }

                    if (_options.AutoUpgrade && _state == UpgradeState.RestartRequired)
                    {
                        await ApplyUpdateAsync(ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in auto-check loop");
            }
        }
    }

    private ReleaseAsset? FindAssetForPlatform(ReleaseInfo release)
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" :
                 RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
                 RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "darwin" : "unknown";

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "386",
            Architecture.Arm => "arm",
            _ => "unknown"
        };

        var patterns = new[]
        {
            $"{os}-{arch}.zip",
            $"{os}_{arch}.zip",
            $"{os}-{arch}.tar.gz",
            $"{os}.zip"
        };

        foreach (var pattern in patterns)
        {
            var asset = release.Assets.FirstOrDefault(a =>
                a.Name.Contains(pattern, StringComparison.OrdinalIgnoreCase));
            if (asset != null)
                return asset;
        }

        return release.Assets.FirstOrDefault();
    }

    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, true);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void UpdateProgress(UpgradeState state, double percent, string message, long bytesDownloaded = 0, long totalBytes = 0, string? error = null)
    {
        _progress = new UpgradeProgress
        {
            State = state,
            ProgressPercent = percent,
            Message = message,
            BytesDownloaded = bytesDownloaded,
            TotalBytes = totalBytes,
            Error = error
        };
    }

    public void Dispose()
    {
        StopAutoCheck();
        _autoCheckCts?.Dispose();
    }
}
