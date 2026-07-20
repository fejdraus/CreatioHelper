using System.Diagnostics;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Enums;
using CreatioHelper.Shared.Utils;

namespace CreatioHelper.Agent.Services;

public class WebSiteRegistryService : IDisposable
{
    private readonly ILogger<WebSiteRegistryService> _logger;
    private readonly WebServerAccessStatus _accessStatus;
    private readonly string _registryPath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private WebSiteRegistry _registry = new();
    private bool _loaded;
    private bool _disposed;

    /// <summary>
    /// Escapes a string for safe use in bash single-quoted strings.
    /// Single quotes are escaped by ending the string, adding an escaped single quote, and starting a new string.
    /// </summary>
    private static string EscapeBashString(string value)
        => BashRunner.EscapeSingleQuoted(value);

    public WebSiteRegistryService(ILogger<WebSiteRegistryService> logger, WebServerAccessStatus accessStatus, IWebHostEnvironment environment)
    {
        _logger = logger;
        _accessStatus = accessStatus;
        _registryPath = Path.Combine(environment.ContentRootPath, "website-registry.json");
    }

    private async Task EnsureLoadedAsync()
    {
        if (_loaded)
            return;

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_loaded)
            {
                await LoadRegistryAsync().ConfigureAwait(false);
                _loaded = true;
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<List<WebSiteInfo>> GetAllSitesAsync()
    {
        await EnsureLoadedAsync().ConfigureAwait(false);

        var sites = _registry.Sites
            .Where(s => !s.AutoDiscovered)
            .OrderBy(s => s.Name)
            .ToList();

        foreach (var site in sites)
        {
            if (_registry.WebServerTypeOverrides.TryGetValue(site.Name, out var kind))
            {
                site.WebServerType = kind;
            }
        }

        return sites;
    }

    public async Task<string?> DetectIisSiteByPathAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var candidates = await GetIisCandidatesAsync().ConfigureAwait(false);
        return ResolveOwningSite(candidates, path);
    }

    public async Task<Dictionary<string, string?>> DetectIisSitesByPathsAsync(IReadOnlyList<string> paths)
    {
        var map = new Dictionary<string, string?>();
        if (paths == null || paths.Count == 0)
        {
            return map;
        }

        var candidates = await GetIisCandidatesAsync().ConfigureAwait(false);
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && !map.ContainsKey(path))
            {
                map[path] = ResolveOwningSite(candidates, path);
            }
        }

        return map;
    }

    public async Task<List<DiscoveredSite>> DiscoverCreatioSitesAsync(IReadOnlyCollection<string> configuredServiceNames)
    {
        var result = new List<DiscoveredSite>();
        if (!OperatingSystem.IsWindows())
        {
            return result;
        }

        var candidates = await GetIisCandidatesAsync().ConfigureAwait(false);
        var configured = new HashSet<string>(configuredServiceNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var site in candidates.Where(c => !c.Name.Contains('/')))
        {
            if (configured.Contains(site.Name))
            {
                continue;
            }

            var webAppAlias = candidates.FirstOrDefault(c => c.Name.Equals(site.Name + "/0", StringComparison.OrdinalIgnoreCase));
            var appRoot = webAppAlias.Name != null ? webAppAlias.PhysicalPath : site.PhysicalPath;

            // Terrasoft.Configuration is a Creatio-specific marker present in both .NET Framework and .NET Core editions
            var configurationPath = System.IO.Path.Combine(appRoot, "Terrasoft.Configuration");
            if (!Directory.Exists(configurationPath))
            {
                continue;
            }

            var folders = new List<DiscoveredFolder>();
            var confPath = System.IO.Path.Combine(appRoot, "conf");
            if (Directory.Exists(confPath))
            {
                folders.Add(new DiscoveredFolder { Name = "conf", Path = confPath });
            }
            folders.Add(new DiscoveredFolder { Name = "Terrasoft.Configuration", Path = configurationPath });

            result.Add(new DiscoveredSite
            {
                SiteName = site.Name,
                AppRootPath = appRoot,
                Folders = folders
            });
        }

        return result;
    }

    public async Task<Dictionary<string, string>> GetIisSiteStatesAsync()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
        {
            return map;
        }

        var script = "Get-Website | Select-Object Name, State | ConvertTo-Json";
        var result = await ExecutePowerShellAsync(script).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result))
        {
            return map;
        }

        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(result);
            var elements = json.ValueKind == JsonValueKind.Array
                ? json.EnumerateArray().ToList()
                : new List<JsonElement> { json };

            foreach (var element in elements)
            {
                var name = GetPropertyIgnoreCase(element, "Name");
                var state = GetPropertyIgnoreCase(element, "State");
                if (!string.IsNullOrEmpty(name))
                {
                    map[name!] = state ?? string.Empty;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read IIS site states");
        }

        return map;
    }

    public async Task<Dictionary<string, SiteControlInfo>> GetIisControlMapAsync()
    {
        var map = new Dictionary<string, SiteControlInfo>(StringComparer.OrdinalIgnoreCase);
        if (!OperatingSystem.IsWindows())
        {
            return map;
        }

        var script = "$items = Get-Website | ForEach-Object { $s=$_; [pscustomobject]@{ Name=$s.name; Pool=$s.applicationPool; State=[string]$s.state }; Get-WebApplication -Site $s.name | ForEach-Object { [pscustomobject]@{ Name=($s.name+$_.path); Pool=$_.applicationPool; State='' } } }; $pools = Get-ChildItem 'IIS:\\AppPools' | ForEach-Object { [pscustomobject]@{ Name=('POOL::'+$_.Name); Pool=$_.Name; State=[string]$_.State } }; @($items) + @($pools) | ConvertTo-Json";
        var result = await ExecutePowerShellAsync(script).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result))
        {
            return map;
        }

        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(result);
            var elements = json.ValueKind == JsonValueKind.Array
                ? json.EnumerateArray().ToList()
                : new List<JsonElement> { json };

            var entries = new List<(string Name, string Pool, string State)>();
            var poolStates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var element in elements)
            {
                var name = GetPropertyIgnoreCase(element, "Name");
                var pool = GetPropertyIgnoreCase(element, "Pool") ?? string.Empty;
                var state = GetPropertyIgnoreCase(element, "State") ?? string.Empty;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (name!.StartsWith("POOL::", StringComparison.Ordinal))
                {
                    poolStates[pool] = state;
                }
                else
                {
                    entries.Add((name, pool, state));
                }
            }

            var poolMembers = entries
                .GroupBy(e => e.Pool, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Name).ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                var isNested = entry.Name.Contains('/');
                var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entry.Name, entry.Name + "/0" };
                var members = poolMembers.TryGetValue(entry.Pool, out var m) ? m : new List<string>();
                var shared = members.Any(member => !own.Contains(member));
                poolStates.TryGetValue(entry.Pool, out var poolState);

                map[entry.Name] = new SiteControlInfo
                {
                    ServiceName = entry.Name,
                    AppPool = entry.Pool,
                    SiteState = entry.State,
                    PoolState = poolState ?? string.Empty,
                    IsNested = isNested,
                    PoolShared = shared
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read IIS control map");
        }

        return map;
    }

    public async Task<(bool Success, string Message)> SetAppPoolStateAsync(string appPool, bool start)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(appPool))
        {
            return (false, "app pool control unavailable");
        }

        var escaped = appPool.Replace("'", "''");
        var target = start ? "Started" : "Stopped";
        var verb = start ? "Start-WebAppPool" : "Stop-WebAppPool";
        var script = $"if ((Get-WebAppPoolState -Name '{escaped}').Value -ne '{target}') {{ {verb} -Name '{escaped}' }}; 'ok'";

        var result = await ExecutePowerShellAsync(script).ConfigureAwait(false);
        var ok = result != null && result.Contains("ok");
        return ok
            ? (true, $"app pool '{appPool}' {(start ? "started" : "stopped")}")
            : (false, $"failed to {(start ? "start" : "stop")} app pool '{appPool}'");
    }

    private async Task<List<(string Name, string PhysicalPath)>> GetIisCandidatesAsync()
    {
        var candidates = new List<(string Name, string PhysicalPath)>();
        if (!OperatingSystem.IsWindows())
        {
            return candidates;
        }

        var script = "Get-Website | ForEach-Object { $s = $_; [pscustomobject]@{ Name = $s.name; PhysicalPath = $s.physicalPath }; Get-WebApplication -Site $s.name | ForEach-Object { [pscustomobject]@{ Name = ($s.name + $_.path); PhysicalPath = $_.PhysicalPath } } } | ConvertTo-Json";
        var result = await ExecutePowerShellAsync(script).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result))
        {
            return candidates;
        }

        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(result);
            var elements = json.ValueKind == JsonValueKind.Array
                ? json.EnumerateArray().ToList()
                : new List<JsonElement> { json };

            foreach (var element in elements)
            {
                var name = GetPropertyIgnoreCase(element, "Name");
                var physical = GetPropertyIgnoreCase(element, "PhysicalPath");
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(physical))
                {
                    candidates.Add((name!, Environment.ExpandEnvironmentVariables(physical!)));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enumerate IIS sites and applications");
        }

        return candidates;
    }

    public static string? ResolveOwningSite(IReadOnlyList<(string Name, string PhysicalPath)> candidates, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return null;
        }

        var target = NormalizePath(targetPath);
        string? bestName = null;
        var bestLength = -1;

        foreach (var (name, physicalPath) in candidates)
        {
            var normalized = NormalizePath(physicalPath);
            if (IsSameOrAncestor(normalized, target) && normalized.Length > bestLength)
            {
                bestLength = normalized.Length;
                bestName = name;
            }
        }

        if (bestName == null)
        {
            return null;
        }

        if (bestName.EndsWith("/0", StringComparison.Ordinal))
        {
            bestName = bestName.Substring(0, bestName.Length - 2);
        }

        return bestName;
    }

    private static string? GetPropertyIgnoreCase(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
            }
        }

        return null;
    }

    private static string NormalizePath(string path)
        => path.Replace('/', '\\').TrimEnd('\\');

    private static bool IsSameOrAncestor(string ancestor, string target)
    {
        if (string.IsNullOrEmpty(ancestor))
        {
            return false;
        }

        if (target.Equals(ancestor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return target.StartsWith(ancestor + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SetWebServerTypeAsync(string siteName, WebServerKind kind)
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            if (kind == WebServerKind.Auto)
            {
                _registry.WebServerTypeOverrides.Remove(siteName);
            }
            else
            {
                _registry.WebServerTypeOverrides[siteName] = kind;
            }

            var manual = _registry.Sites.FirstOrDefault(s => s.Name.Equals(siteName, StringComparison.OrdinalIgnoreCase));
            if (manual != null)
            {
                manual.WebServerType = kind;
                manual.LastUpdated = DateTime.UtcNow;
            }

            _registry.LastUpdated = DateTime.UtcNow;
            await SaveRegistryAsync().ConfigureAwait(false);
            _logger.LogInformation("Set web server type for site {SiteName} to {Kind}", siteName, kind);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<WebSiteInfo?> GetSiteInfoAsync(string siteName)
    {
        var sites = await GetAllSitesAsync();
        return sites.FirstOrDefault(s => s.Name.Equals(siteName, StringComparison.OrdinalIgnoreCase));
    }
    
    // Auto-discover IIS sites
    private async Task<List<WebSiteInfo>> DiscoverIisSitesAsync()
    {
        var sites = new List<WebSiteInfo>();

        try
        {
            var script = "Get-Website | Select-Object Name, State | ConvertTo-Json";
            var result = await ExecutePowerShellAsync(script).ConfigureAwait(false);
            
            if (!string.IsNullOrWhiteSpace(result))
            {
                // Handle both single-site and multi-site results
                JsonElement json = JsonSerializer.Deserialize<JsonElement>(result);
                
                if (json.ValueKind == JsonValueKind.Array)
                {
                    var iisSites = JsonSerializer.Deserialize<IisSiteInfo[]>(result, JsonDefaults.CaseInsensitive);
                    
                    foreach (var site in iisSites ?? Array.Empty<IisSiteInfo>())
                    {
                        sites.Add(CreateIisSiteInfo(site));
                    }
                }
                else if (json.ValueKind == JsonValueKind.Object)
                {
                    var iisSite = JsonSerializer.Deserialize<IisSiteInfo>(result, JsonDefaults.CaseInsensitive);
                    
                    if (iisSite != null)
                    {
                        sites.Add(CreateIisSiteInfo(iisSite));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover IIS sites");
        }
        
        return sites;
    }

    private WebSiteInfo CreateIisSiteInfo(IisSiteInfo iisSite)
    {
        return new WebSiteInfo
        {
            Name = iisSite.Name,
            Type = "IIS",
            ServiceName = iisSite.Name,
            AutoDiscovered = true,
            Status = iisSite.State,
            LastUpdated = DateTime.UtcNow,
            Properties = new Dictionary<string, string>
            {
                ["IisState"] = iisSite.State
            }
        };
    }
    
    // Auto-discover Systemd services (only those marked as web sites)
    private async Task<List<WebSiteInfo>> DiscoverSystemdSitesAsync()
    {
        var sites = new List<WebSiteInfo>();

        try
        {
            // Search for services with specific names or descriptions
            var script = "systemctl list-units --type=service --state=loaded --no-legend | grep -E '(kestrel|web|http|api)' | awk '{print $1}' | sed 's/.service$//'";
            var result = await ExecuteBashAsync(script).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(result))
            {
                var serviceNames = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                foreach (var serviceName in serviceNames)
                {
                    var status = await GetSystemdServiceStatusAsync(serviceName).ConfigureAwait(false);
                    sites.Add(new WebSiteInfo
                    {
                        Name = serviceName,
                        Type = "Systemd",
                        ServiceName = serviceName,
                        AutoDiscovered = true,
                        Status = status,
                        LastUpdated = DateTime.UtcNow
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to discover Systemd sites");
        }
        
        return sites;
    }
    
    // Manual site registration
    public async Task RegisterWebSiteAsync(string displayName, string type, string serviceName, List<string>? folderIds = null, Dictionary<string, string>? properties = null, string? appPool = null)
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var kind = string.Equals(type, "IIS", StringComparison.OrdinalIgnoreCase)
                ? WebServerKind.Iis
                : WebServerKind.Service;

            var existing = _registry.Sites.FirstOrDefault(s => s.Name.Equals(displayName, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Type = type;
                existing.WebServerType = kind;
                existing.ServiceName = serviceName;
                existing.AppPool = appPool ?? string.Empty;
                existing.FolderIds = folderIds ?? new List<string>();
                existing.Properties = properties ?? new Dictionary<string, string>();
                existing.LastUpdated = DateTime.UtcNow;
            }
            else
            {
                _registry.Sites.Add(new WebSiteInfo
                {
                    Name = displayName,
                    Type = type,
                    WebServerType = kind,
                    ServiceName = serviceName,
                    AppPool = appPool ?? string.Empty,
                    AutoDiscovered = false,
                    FolderIds = folderIds ?? new List<string>(),
                    Properties = properties ?? new Dictionary<string, string>(),
                    LastUpdated = DateTime.UtcNow
                });
            }

            _registry.LastUpdated = DateTime.UtcNow;
            await SaveRegistryAsync().ConfigureAwait(false);
            _logger.LogInformation("Registered website: {DisplayName} -> {ServiceName} ({Type})", displayName, serviceName, type);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task RemoveFolderFromAllSitesAsync(string folderId)
    {
        if (string.IsNullOrEmpty(folderId))
        {
            return;
        }

        await EnsureLoadedAsync().ConfigureAwait(false);
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var changed = false;
            foreach (var site in _registry.Sites)
            {
                if (site.FolderIds != null && site.FolderIds.Remove(folderId))
                {
                    site.LastUpdated = DateTime.UtcNow;
                    changed = true;
                }
            }

            if (changed)
            {
                _registry.LastUpdated = DateTime.UtcNow;
                await SaveRegistryAsync().ConfigureAwait(false);
                _logger.LogInformation("Removed deleted folder {FolderId} from linked sites", folderId);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task UnregisterWebSiteAsync(string siteName)
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var removed = _registry.Sites.RemoveAll(s =>
                s.Name.Equals(siteName, StringComparison.OrdinalIgnoreCase) && !s.AutoDiscovered);

            if (removed > 0)
            {
                _registry.WebServerTypeOverrides.Remove(siteName);
                _registry.LastUpdated = DateTime.UtcNow;
                await SaveRegistryAsync().ConfigureAwait(false);
                _logger.LogInformation("Unregistered website: {SiteName}", siteName);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task<bool> SiteExistsAsync(string siteName)
    {
        var site = await GetSiteInfoAsync(siteName);
        return site != null;
    }

    // PowerShell for Windows
    private async Task<string?> ExecutePowerShellAsync(string script)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        try
        {
            var command = PowerShellRunner.Utf8OutputPrologue + PowerShellRunner.ImportWebAdministration + script;
            var result = await PowerShellRunner
                .RunAsync(command, executionPolicy: "RemoteSigned", useUtf8: true)
                .ConfigureAwait(false);

            if (result is null)
                return null;

            if (result.HasError)
            {
                if (WebServerPermission.IsPermissionError(result.Error))
                {
                    _accessStatus.ReportPermissionIssue("IIS site discovery", result.Error, _logger);
                }
                else
                {
                    _logger.LogWarning("PowerShell warning: {Error}", result.Error);
                }
            }
            else
            {
                _accessStatus.ReportSuccess();
            }

            return result.Output.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PowerShell execution failed");
            return null;
        }
    }

    // Bash for Linux
    private async Task<string?> ExecuteBashAsync(string script)
    {
        if (!OperatingSystem.IsLinux())
            return null;

        try
        {
            var result = await BashRunner.RunAsync(script).ConfigureAwait(false);
            if (result is null)
                return null;

            if (result.HasError)
            {
                _logger.LogWarning("Bash warning: {Error}", result.Error);
            }

            return result.Output.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bash execution failed");
            return null;
        }
    }

    private async Task<string> GetSystemdServiceStatusAsync(string serviceName)
    {
        try
        {
            var result = await ExecuteBashAsync($"systemctl is-active '{EscapeBashString(serviceName)}'").ConfigureAwait(false);
            return result switch
            {
                "active" => "Started",
                "inactive" => "Stopped",
                "failed" => "Failed",
                _ => "Unknown"
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get systemd service status for {ServiceName}", serviceName);
            return "Unknown";
        }
    }

    private async Task LoadRegistryAsync()
    {
        if (!File.Exists(_registryPath))
        {
            _registry = new WebSiteRegistry();
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_registryPath).ConfigureAwait(false);
            _registry = JsonSerializer.Deserialize<WebSiteRegistry>(json) ?? new WebSiteRegistry();
            _logger.LogInformation("Loaded {Count} websites from registry", _registry.Sites.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load website registry from {Path}", _registryPath);
            _registry = new WebSiteRegistry();
        }
    }

    private async Task SaveRegistryAsync()
    {
        try
        {
            var json = JsonSerializer.Serialize(_registry, new JsonSerializerOptions
            {
                WriteIndented = true
            });
            await File.WriteAllTextAsync(_registryPath, json).ConfigureAwait(false);
            _logger.LogDebug("Saved website registry to {Path}", _registryPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save website registry to {Path}", _registryPath);
            throw;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _semaphore.Dispose();
            _disposed = true;
        }
    }
}
