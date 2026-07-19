using System.Diagnostics;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Enums;

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
        => value.Replace("'", "'\\''");

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
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var script = "Get-Website | Select-Object Name, @{Name='PhysicalPath';Expression={$_.physicalPath}} | ConvertTo-Json";
        var result = await ExecutePowerShellAsync(script).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result))
        {
            return null;
        }

        var target = NormalizePath(path);

        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(result);
            var elements = json.ValueKind == JsonValueKind.Array
                ? json.EnumerateArray().ToList()
                : new List<JsonElement> { json };

            string? bestName = null;
            var bestLength = -1;

            foreach (var element in elements)
            {
                var name = element.TryGetProperty("Name", out var n) ? n.GetString() : null;
                var physical = element.TryGetProperty("PhysicalPath", out var p) ? p.GetString() : null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(physical))
                {
                    continue;
                }

                var normalized = NormalizePath(Environment.ExpandEnvironmentVariables(physical));
                if (target.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) && normalized.Length > bestLength)
                {
                    bestLength = normalized.Length;
                    bestName = name;
                }
            }

            return bestName;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to detect IIS site by path {Path}", path);
            return null;
        }
    }

    private static string NormalizePath(string path)
        => path.Replace('/', '\\').TrimEnd('\\');

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
                    var iisSites = JsonSerializer.Deserialize<IisSiteInfo[]>(result, new JsonSerializerOptions 
                    { 
                        PropertyNameCaseInsensitive = true 
                    });
                    
                    foreach (var site in iisSites ?? Array.Empty<IisSiteInfo>())
                    {
                        sites.Add(CreateIisSiteInfo(site));
                    }
                }
                else if (json.ValueKind == JsonValueKind.Object)
                {
                    var iisSite = JsonSerializer.Deserialize<IisSiteInfo>(result, new JsonSerializerOptions 
                    { 
                        PropertyNameCaseInsensitive = true 
                    });
                    
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
    public async Task RegisterWebSiteAsync(string displayName, string type, string serviceName, List<string>? folderIds = null, Dictionary<string, string>? properties = null)
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
            var command = $"[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; Import-Module WebAdministration; {script}";

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy RemoteSigned -Command \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return null;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(error))
            {
                if (WebServerPermission.IsPermissionError(error))
                {
                    _accessStatus.ReportPermissionIssue("IIS site discovery", error, _logger);
                }
                else
                {
                    _logger.LogWarning("PowerShell warning: {Error}", error);
                }
            }
            else
            {
                _accessStatus.ReportSuccess();
            }

            return output.Trim();
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
            var startInfo = new ProcessStartInfo
            {
                FileName = "/bin/bash",
                Arguments = $"-c \"{script}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
                return null;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);

            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(error))
            {
                _logger.LogWarning("Bash warning: {Error}", error);
            }

            return output.Trim();
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
