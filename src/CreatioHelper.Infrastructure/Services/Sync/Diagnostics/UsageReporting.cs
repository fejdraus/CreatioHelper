using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Diagnostics;

/// <summary>
/// Usage report data (based on Syncthing usage_report.go)
/// </summary>
public record UsageReport
{
    [JsonPropertyName("uniqueId")]
    public string UniqueId { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("longVersion")]
    public string LongVersion { get; init; } = string.Empty;

    [JsonPropertyName("platform")]
    public string Platform { get; init; } = string.Empty;

    [JsonPropertyName("numFolders")]
    public int NumFolders { get; init; }

    [JsonPropertyName("numDevices")]
    public int NumDevices { get; init; }

    [JsonPropertyName("totFiles")]
    public long TotalFiles { get; init; }

    [JsonPropertyName("folderMaxFiles")]
    public long FolderMaxFiles { get; init; }

    [JsonPropertyName("totMiB")]
    public long TotalMiB { get; init; }

    [JsonPropertyName("folderMaxMiB")]
    public long FolderMaxMiB { get; init; }

    [JsonPropertyName("memoryUsageMiB")]
    public int MemoryUsageMiB { get; init; }

    [JsonPropertyName("sha256Perf")]
    public double Sha256Performance { get; init; }

    [JsonPropertyName("memorySize")]
    public int MemorySizeMiB { get; init; }

    [JsonPropertyName("numCPU")]
    public int NumCpu { get; init; }

    [JsonPropertyName("folderUses")]
    public FolderUsageStats FolderUses { get; init; } = new();

    [JsonPropertyName("deviceUses")]
    public DeviceUsageStats DeviceUses { get; init; } = new();

    [JsonPropertyName("upgradeAllowedManual")]
    public bool UpgradeAllowedManual { get; init; }

    [JsonPropertyName("upgradeAllowedAuto")]
    public bool UpgradeAllowedAuto { get; init; }

    [JsonPropertyName("date")]
    public string Date { get; init; } = DateTime.UtcNow.ToString("yyyy-MM-dd");
}

/// <summary>
/// Folder usage statistics
/// </summary>
public record FolderUsageStats
{
    [JsonPropertyName("sendonly")]
    public int SendOnly { get; init; }

    [JsonPropertyName("sendreceive")]
    public int SendReceive { get; init; }

    [JsonPropertyName("receiveonly")]
    public int ReceiveOnly { get; init; }

    [JsonPropertyName("receiveencrypted")]
    public int ReceiveEncrypted { get; init; }

    [JsonPropertyName("ignorePerms")]
    public int IgnorePerms { get; init; }

    [JsonPropertyName("ignoreDelete")]
    public int IgnoreDelete { get; init; }

    [JsonPropertyName("fsWatcher")]
    public int FsWatcher { get; init; }

    [JsonPropertyName("autoNormalize")]
    public int AutoNormalize { get; init; }
}

/// <summary>
/// Device usage statistics
/// </summary>
public record DeviceUsageStats
{
    [JsonPropertyName("compressAlways")]
    public int CompressAlways { get; init; }

    [JsonPropertyName("compressMetadata")]
    public int CompressMetadata { get; init; }

    [JsonPropertyName("compressNever")]
    public int CompressNever { get; init; }

    [JsonPropertyName("introducer")]
    public int Introducer { get; init; }

    [JsonPropertyName("untrusted")]
    public int Untrusted { get; init; }
}

/// <summary>
/// Configuration for usage reporting
/// </summary>
public class UsageReportingOptions
{
    /// <summary>
    /// Whether usage reporting is enabled
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// URL to submit reports to
    /// </summary>
    public string ReportUrl { get; set; } = "https://data.syncthing.net/newdata";

    /// <summary>
    /// Reporting interval
    /// </summary>
    public TimeSpan ReportInterval { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Unique report ID (generated on first run)
    /// </summary>
    public string UniqueId { get; set; } = string.Empty;
}

/// <summary>
/// Provides anonymous usage reporting (based on Syncthing usage_report.go)
/// </summary>
public interface IUsageReportingService
{
    /// <summary>
    /// Whether usage reporting is enabled
    /// </summary>
    bool Enabled { get; set; }

    /// <summary>
    /// Generate a usage report
    /// </summary>
    UsageReport GenerateReport();

    /// <summary>
    /// Submit usage report
    /// </summary>
    Task<bool> SubmitReportAsync(CancellationToken ct = default);

    /// <summary>
    /// Start automatic reporting
    /// </summary>
    void StartAutoReporting(CancellationToken ct = default);

    /// <summary>
    /// Stop automatic reporting
    /// </summary>
    void StopAutoReporting();
}

/// <summary>
/// Implementation of usage reporting (based on Syncthing usage_report.go)
/// </summary>
public class UsageReportingService : IUsageReportingService, IDisposable
{
    private readonly ILogger<UsageReportingService> _logger;
    private readonly HttpClient _httpClient;
    private readonly UsageReportingOptions _options;
    private readonly Func<UsageReportData> _dataProvider;
    private CancellationTokenSource? _autoReportCts;
    private Task? _autoReportTask;

    public bool Enabled
    {
        get => _options.Enabled;
        set => _options.Enabled = value;
    }

    public UsageReportingService(
        ILogger<UsageReportingService> logger,
        HttpClient httpClient,
        UsageReportingOptions options,
        Func<UsageReportData>? dataProvider = null)
    {
        _logger = logger;
        _httpClient = httpClient;
        _options = options;
        _dataProvider = dataProvider ?? (() => new UsageReportData());

        // Generate unique ID if not set
        if (string.IsNullOrEmpty(_options.UniqueId))
        {
            _options.UniqueId = GenerateUniqueId();
        }
    }

    public UsageReport GenerateReport()
    {
        var data = _dataProvider();
        var process = Process.GetCurrentProcess();

        return new UsageReport
        {
            UniqueId = _options.UniqueId,
            Version = GetVersion(),
            LongVersion = GetLongVersion(),
            Platform = GetPlatform(),
            NumFolders = data.NumFolders,
            NumDevices = data.NumDevices,
            TotalFiles = data.TotalFiles,
            FolderMaxFiles = data.FolderMaxFiles,
            TotalMiB = data.TotalBytes / (1024 * 1024),
            FolderMaxMiB = data.FolderMaxBytes / (1024 * 1024),
            MemoryUsageMiB = (int)(process.WorkingSet64 / (1024 * 1024)),
            Sha256Performance = MeasureSha256Performance(),
            MemorySizeMiB = GetSystemMemoryMiB(),
            NumCpu = Environment.ProcessorCount,
            FolderUses = data.FolderUses,
            DeviceUses = data.DeviceUses,
            UpgradeAllowedManual = true,
            UpgradeAllowedAuto = false,
            Date = DateTime.UtcNow.ToString("yyyy-MM-dd")
        };
    }

    public async Task<bool> SubmitReportAsync(CancellationToken ct = default)
    {
        if (!Enabled)
        {
            _logger.LogDebug("Usage reporting is disabled");
            return false;
        }

        try
        {
            var report = GenerateReport();

            _logger.LogDebug("Submitting usage report to: {Url}", _options.ReportUrl);

            var response = await _httpClient.PostAsJsonAsync(_options.ReportUrl, report, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Usage report submitted successfully");
                return true;
            }

            _logger.LogWarning("Failed to submit usage report: {StatusCode}", response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error submitting usage report");
            return false;
        }
    }

    public void StartAutoReporting(CancellationToken ct = default)
    {
        if (_autoReportTask != null)
            return;

        _autoReportCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _autoReportTask = AutoReportLoopAsync(_autoReportCts.Token);

        _logger.LogInformation("Started automatic usage reporting");
    }

    public void StopAutoReporting()
    {
        _autoReportCts?.Cancel();
        _autoReportTask = null;
        _autoReportCts = null;

        _logger.LogInformation("Stopped automatic usage reporting");
    }

    private async Task AutoReportLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.ReportInterval, ct);
                await SubmitReportAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in auto-report loop");
            }
        }
    }

    private static string GenerateUniqueId()
    {
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string GetVersion()
    {
        var assembly = typeof(UsageReportingService).Assembly;
        var version = assembly.GetName().Version;
        return version?.ToString(3) ?? "1.0.0";
    }

    private static string GetLongVersion()
    {
        var assembly = typeof(UsageReportingService).Assembly;
        var version = assembly.GetName().Version;
        return $"CreatioHelper v{version} ({RuntimeInformation.RuntimeIdentifier})";
    }

    private static string GetPlatform()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows" :
                 RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
                 RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "darwin" : "unknown";

        var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        return $"{os}-{arch}";
    }

    private static double MeasureSha256Performance()
    {
        var data = new byte[128 * 1024]; // 128KB
        RandomNumberGenerator.Fill(data);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 100; i++)
        {
            SHA256.HashData(data);
        }
        sw.Stop();

        // MB/s
        return (data.Length * 100.0 / (1024 * 1024)) / sw.Elapsed.TotalSeconds;
    }

    private static int GetSystemMemoryMiB()
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
            }
            return (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
        }
        catch
        {
            return 0;
        }
    }

    public void Dispose()
    {
        StopAutoReporting();
        _autoReportCts?.Dispose();
    }
}

/// <summary>
/// Data provider for usage reports
/// </summary>
public record UsageReportData
{
    public int NumFolders { get; init; }
    public int NumDevices { get; init; }
    public long TotalFiles { get; init; }
    public long FolderMaxFiles { get; init; }
    public long TotalBytes { get; init; }
    public long FolderMaxBytes { get; init; }
    public FolderUsageStats FolderUses { get; init; } = new();
    public DeviceUsageStats DeviceUses { get; init; } = new();
}
