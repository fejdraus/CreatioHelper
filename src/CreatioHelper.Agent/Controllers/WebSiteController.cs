using CreatioHelper.Agent.Services;
using CreatioHelper.Agent.Authorization;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Contracts.Requests;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Enums;
using Microsoft.AspNetCore.Authorization;

namespace CreatioHelper.Agent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WebSiteController : ControllerBase
{
    private readonly WebSiteRegistryService _registryService;
    private readonly IWebServerServiceFactory _webServerFactory;
    private readonly WebServerAccessStatus _accessStatus;
    private readonly ILogger<WebSiteController> _logger;

    public WebSiteController(
        WebSiteRegistryService registryService,
        IWebServerServiceFactory webServerFactory,
        WebServerAccessStatus accessStatus,
        ILogger<WebSiteController> logger)
    {
        _registryService = registryService;
        _webServerFactory = webServerFactory;
        _accessStatus = accessStatus;
        _logger = logger;
    }

    private async Task<string> GetLiveStateAsync(WebSiteInfo site)
    {
        try
        {
            var manager = await _webServerFactory.CreateWebServerServiceForSiteAsync(site);
            var status = await manager.GetSiteStatusAsync(site.ServiceName);
            return status.Data?.Status ?? site.Status;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read live state for site {SiteName}", site.Name);
            return "Unknown";
        }
    }
    
    /// <summary>
    /// Get all sites (auto-discovered plus manually registered)
    /// </summary>
    [HttpGet("")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> GetAllSites([FromQuery] bool liveState = true)
    {
        try
        {
            var sites = await _registryService.GetAllSitesAsync();

            var controlMap = liveState
                ? await _registryService.GetIisControlMapAsync()
                : new Dictionary<string, SiteControlInfo>();

            var enriched = new List<object>();
            foreach (var site in sites)
            {
                controlMap.TryGetValue(site.ServiceName, out var control);

                // A nested application has no site state of its own; its effective state is the pool state.
                var effectiveState = control == null
                    ? string.Empty
                    : (control.IsNested ? control.PoolState : control.SiteState);

                string status;
                if (!liveState)
                {
                    status = site.Status;
                }
                else if (!string.IsNullOrEmpty(effectiveState))
                {
                    status = effectiveState;
                }
                else
                {
                    status = await GetLiveStateAsync(site);
                }

                enriched.Add(new
                {
                    site.Name,
                    site.Type,
                    site.WebServerType,
                    site.ServiceName,
                    site.AutoDiscovered,
                    site.FolderIds,
                    Status = status,
                    AppPool = !string.IsNullOrEmpty(site.AppPool) ? site.AppPool : (control?.AppPool ?? string.Empty),
                    SiteState = control?.SiteState ?? string.Empty,
                    PoolState = control?.PoolState ?? string.Empty,
                    CanManage = control?.CanManage ?? true,
                    PoolShared = control?.PoolShared ?? false,
                    IsNested = control?.IsNested ?? false
                });
            }

            var response = new
            {
                TotalSites = sites.Count,
                Sites = enriched
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all sites");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
    
    /// <summary>
    /// Manually register a new site
    /// </summary>
    [HttpPost("register")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<IActionResult> RegisterSite([FromBody] RegisterSiteRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            // Check whether a site with the same name already exists
            var existingSite = await _registryService.GetSiteInfoAsync(request.DisplayName);
            if (existingSite != null && existingSite.AutoDiscovered)
            {
                return Conflict($"Site '{request.DisplayName}' already exists and was auto-discovered. Cannot register manually.");
            }
            
            await _registryService.RegisterWebSiteAsync(
                request.DisplayName,
                request.Type,
                request.ServiceName,
                request.FolderIds,
                request.Properties,
                request.AppPool);

            _logger.LogInformation("Successfully registered site: {DisplayName}", request.DisplayName);
            return Ok(new { 
                Message = $"Site '{request.DisplayName}' registered successfully",
                Site = await _registryService.GetSiteInfoAsync(request.DisplayName)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error registering site {DisplayName}", request.DisplayName);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
    
    /// <summary>
    /// Update site information
    /// </summary>
    [HttpPut("{siteName}")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<IActionResult> UpdateSite(string siteName, [FromBody] UpdateSiteRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        try
        {
            var existingSite = await _registryService.GetSiteInfoAsync(siteName);
            if (existingSite == null)
            {
                return NotFound($"Site '{siteName}' not found");
            }
            
            if (existingSite.AutoDiscovered)
            {
                return BadRequest($"Cannot update auto-discovered site '{siteName}'. Only manually registered sites can be updated.");
            }
            
            await _registryService.RegisterWebSiteAsync(
                siteName,
                request.Type,
                request.ServiceName,
                request.FolderIds,
                request.Properties,
                request.AppPool);

            return Ok(new {
                Message = $"Site '{siteName}' updated successfully",
                Site = await _registryService.GetSiteInfoAsync(siteName)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating site {SiteName}", siteName);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
    
    /// <summary>
    /// Set the effective web server type (Auto / Iis / Service) for a site
    /// </summary>
    [HttpPut("{siteName}/webserver-type")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<IActionResult> SetWebServerType(string siteName, [FromBody] SetWebServerTypeRequest request)
    {
        if (!Enum.TryParse<WebServerKind>(request.Type, ignoreCase: true, out var kind))
        {
            return BadRequest($"Invalid web server type '{request.Type}'. Allowed: Auto, Iis, Service.");
        }

        try
        {
            await _registryService.SetWebServerTypeAsync(siteName, kind);
            return Ok(new
            {
                Message = $"Web server type for '{siteName}' set to {kind}",
                Site = await _registryService.GetSiteInfoAsync(siteName)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error setting web server type for site {SiteName}", siteName);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Suggest the IIS site whose physical path contains the given folder path
    /// </summary>
    [HttpGet("detect")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> DetectIisSite([FromQuery] string path)
    {
        try
        {
            var siteName = await _registryService.DetectIisSiteByPathAsync(path);

            var appPool = string.Empty;
            if (!string.IsNullOrEmpty(siteName))
            {
                var control = (await _registryService.GetIisControlMapAsync()).GetValueOrDefault(siteName);
                appPool = control?.AppPool ?? string.Empty;
            }

            return Ok(new { SiteName = siteName, AppPool = appPool, RequiresElevation = _accessStatus.RequiresElevation });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting IIS site by path {Path}", path);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Discover Creatio sites in IIS that are not yet configured (with conf / Terrasoft.Configuration folders)
    /// </summary>
    [HttpGet("discover")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> DiscoverSites()
    {
        try
        {
            var configured = (await _registryService.GetAllSitesAsync()).Select(s => s.ServiceName).ToList();
            var sites = await _registryService.DiscoverCreatioSitesAsync(configured);
            return Ok(new { Sites = sites, RequiresElevation = _accessStatus.RequiresElevation });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error discovering Creatio sites");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Resolve the owning IIS site for several folder paths in a single IIS enumeration
    /// </summary>
    [HttpPost("detect-batch")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> DetectIisSitesBatch([FromBody] string[] paths)
    {
        try
        {
            var sites = await _registryService.DetectIisSitesByPathsAsync(paths ?? Array.Empty<string>());
            return Ok(new { Sites = sites, RequiresElevation = _accessStatus.RequiresElevation });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting IIS sites by paths");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    [HttpPost("{siteName}/start")]
    [Authorize(Roles = Roles.WriteRoles)]
    public Task<IActionResult> StartSite(string siteName) => ChangeSiteStateAsync(siteName, start: true);

    [HttpPost("{siteName}/stop")]
    [Authorize(Roles = Roles.WriteRoles)]
    public Task<IActionResult> StopSite(string siteName) => ChangeSiteStateAsync(siteName, start: false);

    [HttpPost("{siteName}/restart")]
    [Authorize(Roles = Roles.WriteRoles)]
    public Task<IActionResult> RestartSite(string siteName) => ChangeSiteStateAsync(siteName, start: true, restart: true);

    private async Task<IActionResult> ChangeSiteStateAsync(string siteName, bool start, bool restart = false)
    {
        var site = await _registryService.GetSiteInfoAsync(siteName);
        if (site == null)
        {
            return NotFound($"Site '{siteName}' not found");
        }

        try
        {
            var control = (await _registryService.GetIisControlMapAsync()).GetValueOrDefault(site.ServiceName);

            // IIS site/application: pool-aware, nesting-safe handling
            if (control != null)
            {
                if (!control.CanManage)
                {
                    return BadRequest(new
                    {
                        Success = false,
                        Message = $"'{site.ServiceName}' is a nested application on a shared app pool ('{control.AppPool}'). Stopping the pool would affect other sites, so start/stop is disabled."
                    });
                }

                var manager = await _webServerFactory.CreateWebServerServiceForSiteAsync(site);
                var steps = new List<(bool Ok, string Message)>();

                // The app pool is only touched when it is dedicated; a shared pool is left alone
                // so stopping one site cannot take down other sites/applications on the same pool.
                var targetPool = !string.IsNullOrEmpty(site.AppPool) ? site.AppPool : control.AppPool;
                var canControlPool = !string.IsNullOrEmpty(targetPool) && !control.PoolShared;

                async Task ApplyAsync(bool doStart)
                {
                    if (doStart)
                    {
                        if (canControlPool)
                        {
                            steps.Add(await _registryService.SetAppPoolStateAsync(targetPool, start: true));
                        }
                        if (!control.IsNested)
                        {
                            var r = await manager.StartSiteAsync(site.ServiceName);
                            steps.Add((r.Success, r.Message));
                        }
                    }
                    else
                    {
                        if (!control.IsNested)
                        {
                            var r = await manager.StopSiteAsync(site.ServiceName);
                            steps.Add((r.Success, r.Message));
                        }
                        if (canControlPool)
                        {
                            steps.Add(await _registryService.SetAppPoolStateAsync(targetPool, start: false));
                        }
                    }
                }

                if (restart)
                {
                    await ApplyAsync(false);
                    await ApplyAsync(true);
                }
                else
                {
                    await ApplyAsync(start);
                }

                var ok = steps.All(s => s.Ok);
                var message = string.Join("; ", steps.Select(s => s.Message));
                return ok ? Ok(new { Success = ok, Message = message }) : BadRequest(new { Success = ok, Message = message });
            }

            // Non-IIS (service-managed) sites keep the original behavior
            var mgr = await _webServerFactory.CreateWebServerServiceForSiteAsync(site);
            if (restart)
            {
                await mgr.StopSiteAsync(site.ServiceName);
            }
            var result = start
                ? await mgr.StartSiteAsync(site.ServiceName)
                : await mgr.StopSiteAsync(site.ServiceName);

            return result.Success
                ? Ok(new { result.Success, result.Message })
                : BadRequest(new { result.Success, result.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error changing state for site {SiteName}", siteName);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Remove a site from the registry (only manually registered)
    /// </summary>
    [HttpDelete("{siteName}")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<IActionResult> UnregisterSite(string siteName)
    {
        try
        {
            var existingSite = await _registryService.GetSiteInfoAsync(siteName);
            if (existingSite == null)
            {
                return NotFound($"Site '{siteName}' not found");
            }
            
            if (existingSite.AutoDiscovered)
            {
                return BadRequest($"Cannot unregister auto-discovered site '{siteName}'. Only manually registered sites can be unregistered.");
            }
            
            await _registryService.UnregisterWebSiteAsync(siteName);
            _logger.LogInformation("Successfully unregistered site: {SiteName}", siteName);
            return Ok(new { Message = $"Site '{siteName}' unregistered successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unregistering site {SiteName}", siteName);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Get information about a specific site
    /// </summary>
    [HttpGet("{siteName}")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> GetSiteInfo(string siteName)
    {
        try
        {
            var siteInfo = await _registryService.GetSiteInfoAsync(siteName);
            if (siteInfo == null)
            {
                return NotFound($"Site '{siteName}' not found");
            }
            
            return Ok(siteInfo);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting site info for {SiteName}", siteName);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
    
    /// <summary>
    /// Check if a site exists
    /// </summary>
    [HttpHead("{siteName}")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> CheckSiteExists(string siteName)
    {
        try
        {
            var exists = await _registryService.SiteExistsAsync(siteName);
            return exists ? Ok() : NotFound();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking site existence for {SiteName}", siteName);
            return StatusCode(500);
        }
    }
    
    /// <summary>
    /// Get sites by type
    /// </summary>
    [HttpGet("by-type/{type}")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> GetSitesByType(string type)
    {
        try
        {
            var allSites = await _registryService.GetAllSitesAsync();
            var sitesByType = allSites.Where(s => s.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).ToList();
            
            return Ok(new
            {
                Type = type,
                sitesByType.Count,
                Sites = sitesByType
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting sites by type {Type}", type);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
    
    /// <summary>
    /// Get site statistics
    /// </summary>
    [HttpGet("stats")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> GetSiteStats()
    {
        try
        {
            var sites = await _registryService.GetAllSitesAsync();
            
            var stats = new
            {
                TotalSites = sites.Count,
                AutoDiscovered = sites.Count(s => s.AutoDiscovered),
                ManuallyRegistered = sites.Count(s => !s.AutoDiscovered),
                ByType = sites.GroupBy(s => s.Type)
                             .ToDictionary(g => g.Key, g => new
                             {
                                 Total = g.Count(),
                                 AutoDiscovered = g.Count(s => s.AutoDiscovered),
                                 ManuallyRegistered = g.Count(s => !s.AutoDiscovered)
                             }),
                ByStatus = sites.GroupBy(s => s.Status)
                              .ToDictionary(g => g.Key, g => g.Count()),
                LastUpdated = DateTime.UtcNow
            };
            
            return Ok(stats);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting site stats");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}