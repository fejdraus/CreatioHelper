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
    private readonly ILogger<WebSiteController> _logger;

    public WebSiteController(
        WebSiteRegistryService registryService,
        IWebServerServiceFactory webServerFactory,
        ILogger<WebSiteController> logger)
    {
        _registryService = registryService;
        _webServerFactory = webServerFactory;
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
    public async Task<IActionResult> GetAllSites()
    {
        try
        {
            var sites = await _registryService.GetAllSitesAsync();

            var enriched = new List<object>();
            foreach (var site in sites)
            {
                enriched.Add(new
                {
                    site.Name,
                    site.Type,
                    site.WebServerType,
                    site.ServiceName,
                    site.AutoDiscovered,
                    site.FolderIds,
                    Status = await GetLiveStateAsync(site)
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
                request.Properties);

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
                request.Properties);

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
            return Ok(new { SiteName = siteName });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting IIS site by path {Path}", path);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    [HttpPost("{siteName}/start")]
    [Authorize(Roles = Roles.WriteRoles)]
    public Task<IActionResult> StartSite(string siteName) => ChangeSiteStateAsync(siteName, start: true);

    [HttpPost("{siteName}/stop")]
    [Authorize(Roles = Roles.WriteRoles)]
    public Task<IActionResult> StopSite(string siteName) => ChangeSiteStateAsync(siteName, start: false);

    private async Task<IActionResult> ChangeSiteStateAsync(string siteName, bool start)
    {
        var site = await _registryService.GetSiteInfoAsync(siteName);
        if (site == null)
        {
            return NotFound($"Site '{siteName}' not found");
        }

        try
        {
            var manager = await _webServerFactory.CreateWebServerServiceForSiteAsync(site);
            var result = start
                ? await manager.StartSiteAsync(site.ServiceName)
                : await manager.StopSiteAsync(site.ServiceName);

            if (result.Success)
            {
                return Ok(new { result.Success, result.Message });
            }

            return BadRequest(new { result.Success, result.Message });
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