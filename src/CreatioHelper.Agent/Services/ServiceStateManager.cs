using CreatioHelper.Agent.Configuration;

namespace CreatioHelper.Agent.Services;

/// <summary>
/// Manages service state (stop/start) across different platforms
/// Tracks what was stopped to restore it later
/// </summary>
public class ServiceStateManager : IDisposable
{
    private readonly IWebServerServiceFactory _webServerFactory;
    private readonly ILogger<ServiceStateManager> _logger;
    private readonly SyncthingAutoStopSettings _settings;

    // Track what was stopped (using int for Interlocked operations)
    // Separate flags for each platform to avoid conflicts
    private int _poolWasStopped = 0;              // Windows IIS Pool
    private int _iisSiteWasStopped = 0;           // Windows IIS Site
    private int _windowsServiceWasStopped = 0;    // Windows Service
    private int _linuxServiceWasStopped = 0;      // Linux systemd
    private int _macosServiceWasStopped = 0;      // MacOS launchd
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public ServiceStateManager(
        IWebServerServiceFactory webServerFactory,
        ILogger<ServiceStateManager> logger,
        SyncthingAutoStopSettings settings)
    {
        _webServerFactory = webServerFactory;
        _logger = logger;
        _settings = settings;
    }

    /// <summary>
    /// Stop all configured services for the current platform
    /// Returns true if at least one service was stopped
    /// </summary>
    public async Task<bool> StopServicesAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var webServerService = await _webServerFactory.CreateWebServerServiceAsync().ConfigureAwait(false);

            if (!webServerService.IsSupported())
            {
                _logger.LogWarning("Web server service not supported on this platform");
                return false;
            }

            bool anyServiceStopped = false;

            if (OperatingSystem.IsWindows() && _settings.Windows != null)
            {
                anyServiceStopped = await StopWindowsServicesAsync(webServerService, cancellationToken);
            }
            else if (OperatingSystem.IsLinux() && _settings.Linux != null)
            {
                anyServiceStopped = await StopLinuxServicesAsync(webServerService, cancellationToken);
            }
            else if (OperatingSystem.IsMacOS() && _settings.MacOS != null)
            {
                anyServiceStopped = await StopMacOsServicesAsync(webServerService, cancellationToken);
            }
            else
            {
                _logger.LogWarning("No platform-specific configuration found");
            }

            return anyServiceStopped;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping services");
            return false;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Start all services that were previously stopped
    /// </summary>
    public async Task<bool> StartServicesAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var webServerService = await _webServerFactory.CreateWebServerServiceAsync().ConfigureAwait(false);

            if (!webServerService.IsSupported())
            {
                _logger.LogWarning("Web server service not supported on this platform");
                return false;
            }

            bool anyServiceStarted = false;

            if (OperatingSystem.IsWindows() && _settings.Windows != null)
            {
                anyServiceStarted = await StartWindowsServicesAsync(webServerService, cancellationToken);
            }
            else if (OperatingSystem.IsLinux() && _settings.Linux != null)
            {
                anyServiceStarted = await StartLinuxServicesAsync(webServerService, cancellationToken);
            }
            else if (OperatingSystem.IsMacOS() && _settings.MacOS != null)
            {
                anyServiceStarted = await StartMacOsServicesAsync(webServerService, cancellationToken);
            }

            return anyServiceStarted;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting services");
            return false;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    // Note: cancellationToken is not passed to webServerService methods as IWebServerService doesn't support cancellation.
    // The token is used for semaphore acquisition in the caller. Service operations are typically quick.
    private async Task<bool> StopWindowsServicesAsync(IWebServerService webServerService, CancellationToken cancellationToken)
    {
        bool anyServiceStopped = false;
        var winSettings = _settings.Windows!;

        // Stop IIS App Pool
        if (!string.IsNullOrEmpty(winSettings.AppPoolName))
        {
            _logger.LogInformation("Stopping IIS app pool: {PoolName}", winSettings.AppPoolName);
            var result = await webServerService.StopAppPoolAsync(winSettings.AppPoolName);

            if (result.Success)
            {
                _logger.LogInformation("IIS app pool stopped: {PoolName}", winSettings.AppPoolName);
                Interlocked.Exchange(ref _poolWasStopped, 1);
                anyServiceStopped = true;
            }
            else
            {
                _logger.LogError("Failed to stop IIS app pool {PoolName}: {Message}",
                    winSettings.AppPoolName, result.Message);
            }
        }

        // Stop IIS Website
        if (!string.IsNullOrEmpty(winSettings.SiteName))
        {
            _logger.LogInformation("Stopping IIS website: {SiteName}", winSettings.SiteName);
            var result = await webServerService.StopSiteAsync(winSettings.SiteName);

            if (result.Success)
            {
                _logger.LogInformation("IIS website stopped: {SiteName}", winSettings.SiteName);
                Interlocked.Exchange(ref _iisSiteWasStopped, 1);
                anyServiceStopped = true;
            }
            else
            {
                _logger.LogError("Failed to stop IIS website {SiteName}: {Message}",
                    winSettings.SiteName, result.Message);
            }
        }

        // Stop Windows Service (if not using IIS)
        if (!string.IsNullOrEmpty(winSettings.ServiceName))
        {
            _logger.LogInformation("Stopping Windows service: {ServiceName}", winSettings.ServiceName);
            var result = await webServerService.StopSiteAsync(winSettings.ServiceName); // StopSiteAsync handles Windows Services too

            if (result.Success)
            {
                _logger.LogInformation("Windows service stopped: {ServiceName}", winSettings.ServiceName);
                Interlocked.Exchange(ref _windowsServiceWasStopped, 1);
                anyServiceStopped = true;
            }
            else
            {
                _logger.LogError("Failed to stop Windows service {ServiceName}: {Message}",
                    winSettings.ServiceName, result.Message);
            }
        }

        return anyServiceStopped;
    }

    private async Task<bool> StartWindowsServicesAsync(IWebServerService webServerService, CancellationToken cancellationToken)
    {
        bool anyServiceStarted = false;
        var winSettings = _settings.Windows!;

        // Start IIS App Pool if it was stopped
        if (Interlocked.CompareExchange(ref _poolWasStopped, 0, 0) == 1 && !string.IsNullOrEmpty(winSettings.AppPoolName))
        {
            _logger.LogInformation("Starting IIS app pool: {PoolName}", winSettings.AppPoolName);
            var result = await webServerService.StartAppPoolAsync(winSettings.AppPoolName);

            if (result.Success)
            {
                _logger.LogInformation("IIS app pool started: {PoolName}", winSettings.AppPoolName);
                Interlocked.Exchange(ref _poolWasStopped, 0);
                anyServiceStarted = true;
            }
            else
            {
                _logger.LogError("Failed to start IIS app pool {PoolName}: {Message}",
                    winSettings.AppPoolName, result.Message);
            }
        }

        // Start IIS Website if it was stopped
        if (Interlocked.CompareExchange(ref _iisSiteWasStopped, 0, 0) == 1 && !string.IsNullOrEmpty(winSettings.SiteName))
        {
            _logger.LogInformation("Starting IIS website: {SiteName}", winSettings.SiteName);
            var result = await webServerService.StartSiteAsync(winSettings.SiteName);

            if (result.Success)
            {
                _logger.LogInformation("IIS website started: {SiteName}", winSettings.SiteName);
                Interlocked.Exchange(ref _iisSiteWasStopped, 0);
                anyServiceStarted = true;
            }
            else
            {
                _logger.LogError("Failed to start IIS website {SiteName}: {Message}",
                    winSettings.SiteName, result.Message);
            }
        }

        // Start Windows Service if it was stopped
        if (Interlocked.CompareExchange(ref _windowsServiceWasStopped, 0, 0) == 1 && !string.IsNullOrEmpty(winSettings.ServiceName))
        {
            _logger.LogInformation("Starting Windows service: {ServiceName}", winSettings.ServiceName);
            var result = await webServerService.StartSiteAsync(winSettings.ServiceName); // StartSiteAsync handles Windows Services too

            if (result.Success)
            {
                _logger.LogInformation("Windows service started: {ServiceName}", winSettings.ServiceName);
                Interlocked.Exchange(ref _windowsServiceWasStopped, 0);
                anyServiceStarted = true;
            }
            else
            {
                _logger.LogError("Failed to start Windows service {ServiceName}: {Message}",
                    winSettings.ServiceName, result.Message);
            }
        }

        return anyServiceStarted;
    }

    private async Task<bool> StopLinuxServicesAsync(IWebServerService webServerService, CancellationToken cancellationToken)
    {
        var linuxSettings = _settings.Linux!;

        if (string.IsNullOrEmpty(linuxSettings.ServiceName))
        {
            return false;
        }

        _logger.LogInformation("Stopping Linux systemd service: {ServiceName}", linuxSettings.ServiceName);

        var result = await webServerService.StopSiteAsync(linuxSettings.ServiceName);

        if (result.Success)
        {
            _logger.LogInformation("Linux systemd service stopped: {ServiceName}", linuxSettings.ServiceName);
            Interlocked.Exchange(ref _linuxServiceWasStopped, 1);
            return true;
        }
        else
        {
            _logger.LogError("Failed to stop Linux systemd service {ServiceName}: {Message}",
                linuxSettings.ServiceName, result.Message);
            return false;
        }
    }

    private async Task<bool> StartLinuxServicesAsync(IWebServerService webServerService, CancellationToken cancellationToken)
    {
        var linuxSettings = _settings.Linux!;

        // Only start if service WAS stopped (flag == 1) AND service name is not empty
        if (Interlocked.CompareExchange(ref _linuxServiceWasStopped, 0, 0) != 1 || string.IsNullOrEmpty(linuxSettings.ServiceName))
        {
            return false;
        }

        _logger.LogInformation("Starting Linux systemd service: {ServiceName}", linuxSettings.ServiceName);

        var result = await webServerService.StartSiteAsync(linuxSettings.ServiceName);

        if (result.Success)
        {
            _logger.LogInformation("Linux systemd service started: {ServiceName}", linuxSettings.ServiceName);
            Interlocked.Exchange(ref _linuxServiceWasStopped, 0);
            return true;
        }
        else
        {
            _logger.LogError("Failed to start Linux systemd service {ServiceName}: {Message}",
                linuxSettings.ServiceName, result.Message);
            return false;
        }
    }

    private async Task<bool> StopMacOsServicesAsync(IWebServerService webServerService, CancellationToken cancellationToken)
    {
        var macSettings = _settings.MacOS!;

        if (string.IsNullOrEmpty(macSettings.ServiceName))
        {
            return false;
        }

        _logger.LogInformation("Stopping MacOS launchd service: {ServiceName}", macSettings.ServiceName);

        var result = await webServerService.StopSiteAsync(macSettings.ServiceName);

        if (result.Success)
        {
            _logger.LogInformation("MacOS launchd service stopped: {ServiceName}", macSettings.ServiceName);
            Interlocked.Exchange(ref _macosServiceWasStopped, 1);
            return true;
        }
        else
        {
            _logger.LogError("Failed to stop MacOS launchd service {ServiceName}: {Message}",
                macSettings.ServiceName, result.Message);
            return false;
        }
    }

    private async Task<bool> StartMacOsServicesAsync(IWebServerService webServerService, CancellationToken cancellationToken)
    {
        var macSettings = _settings.MacOS!;

        // Only start if service WAS stopped (flag == 1) AND service name is not empty
        if (Interlocked.CompareExchange(ref _macosServiceWasStopped, 0, 0) != 1 || string.IsNullOrEmpty(macSettings.ServiceName))
        {
            return false;
        }

        _logger.LogInformation("Starting MacOS launchd service: {ServiceName}", macSettings.ServiceName);

        var result = await webServerService.StartSiteAsync(macSettings.ServiceName);

        if (result.Success)
        {
            _logger.LogInformation("MacOS launchd service started: {ServiceName}", macSettings.ServiceName);
            Interlocked.Exchange(ref _macosServiceWasStopped, 0);
            return true;
        }
        else
        {
            _logger.LogError("Failed to start MacOS launchd service {ServiceName}: {Message}",
                macSettings.ServiceName, result.Message);
            return false;
        }
    }

    public void Dispose()
    {
        _semaphore?.Dispose();
    }
}
