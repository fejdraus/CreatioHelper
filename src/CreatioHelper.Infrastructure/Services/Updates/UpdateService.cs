using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Application.Services.Updates;
using CreatioHelper.Domain.Enums;
using CreatioHelper.Shared.Interfaces;
using NuGet.Versioning;

namespace CreatioHelper.Infrastructure.Services.Updates;

public class UpdateService : IUpdateService, IDisposable
{
    private const string RepoOwner = "fejdraus";
    private const string RepoName = "CreatioHelper";
    private const string UserAgent = "CreatioHelper-Updater";
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    private readonly IHttpClientFactory _httpFactory;
    private readonly IAppSettingsManager _settingsManager;
    private readonly IOutputWriter _output;
    private readonly SemaphoreSlim _checkLock = new(1, 1);
    private readonly Timer _timer;

    private UpdateState _state = new UpdateState.Idle();
    private bool _started;
    private ProcessStartInfo? _pendingApply;

    public UpdateService(IHttpClientFactory httpFactory, IAppSettingsManager settingsManager, IOutputWriter output)
    {
        _httpFactory = httpFactory;
        _settingsManager = settingsManager;
        _output = output;
        CurrentVersion = ReadCurrentInformationalVersion();
        _timer = new Timer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public UpdateState State => _state;

    public string CurrentVersion { get; }

    public string? LastSeenVersion { get; private set; }

    public event EventHandler<UpdateState>? StateChanged;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _timer.Change(InitialDelay, PollInterval);
    }

    public Task CheckNowAsync(bool explicitly = true, UpdateChannel? channelOverride = null, CancellationToken cancellationToken = default)
    {
        return CheckCoreAsync(explicitly, channelOverride, cancellationToken);
    }

    public async Task DownloadAndInstallAsync(CancellationToken cancellationToken = default)
    {
        if (_state is not UpdateState.Available available)
        {
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await InstallOnWindowsAsync(available, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            OpenReleasePage(available.ReleaseUrl);
            SetState(new UpdateState.Idle());
        }
    }

    public void SkipCurrentAvailable()
    {
        if (_state is not UpdateState.Available available)
        {
            return;
        }

        var settings = _settingsManager.Load();
        settings.SkipUpdateVersion = available.Version;
        _settingsManager.Save(settings);
        SetState(new UpdateState.Idle());
    }

    public void Dispose()
    {
        _timer.Dispose();
        _checkLock.Dispose();
    }

    private async void OnTimer(object? _)
    {
        try
        {
            await CheckCoreAsync(explicitly: false, channelOverride: null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Update check failed: {ex.Message}");
        }
    }

    private async Task CheckCoreAsync(bool explicitly, UpdateChannel? channelOverride, CancellationToken ct)
    {
        if (!await _checkLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var settings = _settingsManager.Load();
            if (!settings.UpdateCheckEnabled)
            {
                SetState(new UpdateState.Disabled());
                return;
            }

            SetState(new UpdateState.Checking());

            var channel = channelOverride ?? settings.UpdateChannel;
            var release = await FetchTopReleaseAsync(channel, ct).ConfigureAwait(false);
            if (release is null)
            {
                LastSeenVersion = null;
                SetState(new UpdateState.Idle(NotAvailable: explicitly));
                return;
            }

            LastSeenVersion = release.Version.ToString();

            if (!NuGetVersion.TryParse(CurrentVersion, out var current))
            {
                _output.WriteLine($"[WARNING] Cannot parse current version '{CurrentVersion}', skipping update check.");
                SetState(new UpdateState.Idle());
                return;
            }

            if (release.Version <= current)
            {
                SetState(new UpdateState.Idle(NotAvailable: explicitly));
                return;
            }

            if (!explicitly && string.Equals(settings.SkipUpdateVersion, release.Version.ToString(), StringComparison.Ordinal))
            {
                SetState(new UpdateState.Idle());
                return;
            }

            SetState(new UpdateState.Available(
                Version: release.Version.ToString(),
                ReleaseUrl: release.HtmlUrl,
                AssetUrl: release.AssetUrl,
                IsPrerelease: release.IsPrerelease));
        }
        finally
        {
            _checkLock.Release();
        }
    }

    private async Task<GitHubRelease?> FetchTopReleaseAsync(UpdateChannel channel, CancellationToken ct)
    {
        var http = _httpFactory.CreateClient(nameof(UpdateService));
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=20";
        var json = await http.GetStringAsync(url, ct).ConfigureAwait(false);
        var array = JsonNode.Parse(json) as JsonArray;
        if (array is null)
        {
            return null;
        }

        var ridPattern = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win-x64" : "linux-x64";

        GitHubRelease? best = null;
        foreach (var node in array)
        {
            if (node is not JsonObject obj)
            {
                continue;
            }

            bool isPrerelease = obj["prerelease"]?.GetValue<bool>() ?? false;
            if (channel == UpdateChannel.Stable && isPrerelease)
            {
                continue;
            }

            if (channel == UpdateChannel.Beta && !isPrerelease)
            {
                continue;
            }

            var tag = obj["tag_name"]?.GetValue<string>();
            if (string.IsNullOrEmpty(tag))
            {
                continue;
            }

            var versionString = tag.StartsWith('v') ? tag[1..] : tag;
            if (!NuGetVersion.TryParse(versionString, out var version))
            {
                continue;
            }

            if (best is not null && version <= best.Version)
            {
                continue;
            }

            string? assetUrl = null;
            if (obj["assets"] is JsonArray assets)
            {
                foreach (var assetNode in assets)
                {
                    if (assetNode is not JsonObject asset)
                    {
                        continue;
                    }

                    var name = asset["name"]?.GetValue<string>() ?? string.Empty;
                    if (name.Contains(ridPattern, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        assetUrl = asset["browser_download_url"]?.GetValue<string>();
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(assetUrl))
            {
                continue;
            }

            best = new GitHubRelease(
                Version: version,
                HtmlUrl: obj["html_url"]?.GetValue<string>() ?? string.Empty,
                AssetUrl: assetUrl,
                IsPrerelease: isPrerelease);
        }

        return best;
    }

    private async Task InstallOnWindowsAsync(UpdateState.Available available, CancellationToken ct)
    {
        var installDir = Path.GetDirectoryName(Environment.ProcessPath)
            ?? throw new InvalidOperationException("Cannot determine install directory.");
        var stagingRoot = Path.Combine(Path.GetTempPath(), $"CreatioHelper-update-{available.Version}");
        if (Directory.Exists(stagingRoot))
        {
            Directory.Delete(stagingRoot, recursive: true);
        }
        Directory.CreateDirectory(stagingRoot);

        var zipPath = Path.Combine(stagingRoot, "package.zip");
        var extractDir = Path.Combine(stagingRoot, "extracted");
        Directory.CreateDirectory(extractDir);

        SetState(new UpdateState.Downloading(available.Version, 0));

        var http = _httpFactory.CreateClient(nameof(UpdateService));
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

        using (var resp = await http.GetAsync(available.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? -1L;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = File.Create(zipPath);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0)
                {
                    SetState(new UpdateState.Downloading(available.Version, read * 100.0 / total));
                }
            }
        }

        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        var scriptPath = Path.Combine(stagingRoot, "apply-update.cmd");
        var pid = Environment.ProcessId;
        var exeName = Path.GetFileName(Environment.ProcessPath) ?? "CreatioHelper.exe";
        var script = $@"@echo off
setlocal enableextensions

:wait_loop
tasklist /FI ""PID eq {pid}"" 2>nul | find ""{pid}"" >nul
if not errorlevel 1 (
    timeout /t 1 /nobreak >nul
    goto wait_loop
)

xcopy /E /Y /I /Q ""{extractDir}\*"" ""{installDir}\""
if errorlevel 1 (
    exit /b 1
)

start """" ""{installDir}\{exeName}""

rmdir /S /Q ""{stagingRoot}"" 2>nul
del ""%~f0""
";
        await File.WriteAllTextAsync(scriptPath, script, ct).ConfigureAwait(false);

        _pendingApply = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        _output.WriteLine($"[INFO] Update {available.Version} staged. Awaiting user confirmation to restart.");
        SetState(new UpdateState.Ready(available.Version));
    }

    public void QuitAndApply()
    {
        if (_pendingApply is null)
        {
            return;
        }

        try
        {
            Process.Start(_pendingApply);
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to launch update applier: {ex.Message}");
            return;
        }

        Environment.Exit(0);
    }

    private void OpenReleasePage(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _output.WriteLine($"[ERROR] Failed to open release page {url}: {ex.Message}");
        }
    }

    private void SetState(UpdateState newState)
    {
        _state = newState;
        StateChanged?.Invoke(this, newState);
    }

    private static string ReadCurrentInformationalVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        var attrVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (LooksLikeFullVersion(attrVersion))
        {
            return Strip(attrVersion!);
        }

        try
        {
            var path = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                var fvi = FileVersionInfo.GetVersionInfo(path);
                var product = fvi.ProductVersion;
                if (LooksLikeFullVersion(product))
                {
                    return Strip(product!);
                }
            }
        }
        catch
        {
            // ignored — fall through to attribute or Assembly.Version
        }

        if (!string.IsNullOrEmpty(attrVersion))
        {
            return Strip(attrVersion);
        }

        return assembly.GetName().Version?.ToString() ?? "0.0.0";

        static bool LooksLikeFullVersion(string? value) =>
            !string.IsNullOrEmpty(value) && (value.Contains('-') || value.Contains('+'));

        static string Strip(string raw)
        {
            var plus = raw.IndexOf('+');
            return plus >= 0 ? raw[..plus] : raw;
        }
    }

    private sealed record GitHubRelease(NuGetVersion Version, string HtmlUrl, string AssetUrl, bool IsPrerelease);
}
