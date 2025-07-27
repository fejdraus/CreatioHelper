using CreatioHelper.Agent.Services;
using CreatioHelper.Contracts.Requests;

namespace CreatioHelper.Agent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WebSiteController : ControllerBase
{
    private readonly WebSiteRegistryService _registryService;
    private readonly ILogger<WebSiteController> _logger;

    public WebSiteController(WebSiteRegistryService registryService, ILogger<WebSiteController> logger)
    {
        _registryService = registryService;
        _logger = logger;
    }
    
    /// <summary>
    /// Get all sites (auto-discovered plus manually registered)
    /// </summary>
    [HttpGet("")]
    public async Task<IActionResult> GetAllSites()
    {
        try
        {
            var sites = await _registryService.GetAllSitesAsync();
            
            var response = new
            {
                TotalSites = sites.Count,
                AutoDiscovered = sites.Count(s => s.AutoDiscovered),
                ManuallyRegistered = sites.Count(s => !s.AutoDiscovered),
                ByType = sites.GroupBy(s => s.Type)
                             .ToDictionary(g => g.Key, g => g.Count()),
                Sites = sites
            };
            
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all sites");
            return StatusCode(500, new { Message = ex.Message });
        }
    }
    
    /// <summary>
    /// Manually register a new site
    /// </summary>
    [HttpPost("register")]
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
            return StatusCode(500, new { Message = ex.Message });
        }
    }
    
    /// <summary>
    /// Update site information
    /// </summary>
    [HttpPut("{siteName}")]
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
                request.Properties);
                
            return Ok(new { 
                Message = $"Site '{siteName}' updated successfully",
                Site = await _registryService.GetSiteInfoAsync(siteName)
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating site {SiteName}", siteName);
            return StatusCode(500, new { Message = ex.Message });
        }
    }
    
    /// <summary>
    /// Remove a site from the registry (only manually registered)
    /// </summary>
    [HttpDelete("{siteName}")]
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
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    /// <summary>
    /// Get information about a specific site
    /// </summary>
    [HttpGet("{siteName}")]
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
            return StatusCode(500, new { Message = ex.Message });
        }
    }
    
    /// <summary>
    /// Check if a site exists
    /// </summary>
    [HttpHead("{siteName}")]
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
    public async Task<IActionResult> GetSitesByType(string type)
    {
        try
        {
            var allSites = await _registryService.GetAllSitesAsync();
            var sitesByType = allSites.Where(s => s.Type.Equals(type, StringComparison.OrdinalIgnoreCase)).ToList();
            
            return Ok(new
            {
                Type = type,
                Count = sitesByType.Count,
                Sites = sitesByType
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting sites by type {Type}", type);
            return StatusCode(500, new { Message = ex.Message });
        }
    }
    
    /// <summary>
    /// Get site statistics
    /// </summary>
    [HttpGet("stats")]
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
            return StatusCode(500, new { Message = ex.Message });
        }
    }
}