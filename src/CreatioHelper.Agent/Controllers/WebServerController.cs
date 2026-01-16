using CreatioHelper.Agent.Services.Windows;
using CreatioHelper.Agent.Authorization;
using CreatioHelper.Contracts.Requests;
using CreatioHelper.Contracts.Responses;
using Microsoft.AspNetCore.Authorization;
using WebServerResultDto = CreatioHelper.Contracts.Responses.WebServerResult;
using DataDto = CreatioHelper.Contracts.Responses.Data;

namespace CreatioHelper.Agent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WebServerController : ControllerBase
{
    private readonly IWebServerServiceFactory _webServerFactory;
    private readonly IisStatusService? _iisStatusService;
    private readonly IPlatformService _platformService;
    private readonly ILogger<WebServerController> _logger;
    private readonly IConfiguration _configuration;

    public WebServerController(
        IWebServerServiceFactory webServerFactory, 
        IPlatformService platformService,
        ILogger<WebServerController> logger,
        IConfiguration configuration,
        IisStatusService? iisStatusService = null)
    {
        _webServerFactory = webServerFactory;
        _platformService = platformService;
        _logger = logger;
        _configuration = configuration;
        _iisStatusService = iisStatusService;
    }

    // Add IsWebServerSupported property
    private bool IsWebServerSupported => _webServerFactory.IsWebServerSupported();

    [HttpGet("info")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> GetWebServerInfo()
    {
        return Ok(new
        {
            Platform = _platformService.GetPlatform().ToString(),
            SupportedWebServer = await _webServerFactory.GetSupportedWebServerTypeAsync(), // Now async
            AvailableWebServerTypes = _webServerFactory.GetAvailableWebServerTypes(),
            IsSupported = IsWebServerSupported,
            Features = new[]
            {
                new { Name = "StartSite", Available = IsWebServerSupported },
                new { Name = "StopSite", Available = IsWebServerSupported },
                new { Name = "StartAppPool", Available = IsWebServerSupported },
                new { Name = "StopAppPool", Available = IsWebServerSupported },
                new { Name = "GetStatus", Available = IsWebServerSupported },
                new { Name = "IIS", Available = _platformService.IsFeatureSupported(FeatureNames.IisManagement) },
                new { Name = "WindowsService", Available = _platformService.IsFeatureSupported(FeatureNames.WindowsServiceManagement) },
                new { Name = "Systemd", Available = _platformService.IsFeatureSupported(FeatureNames.SystemdManagement) },
                new { Name = "Launchd", Available = _platformService.IsFeatureSupported(FeatureNames.LaunchdManagement) },
                new { Name = "FileSync", Available = _platformService.IsFeatureSupported(FeatureNames.FileSync) }
            }
        });
    }
    
    [HttpGet("webserver-type")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> GetWebServerType()
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            var currentType = await _webServerFactory.GetSupportedWebServerTypeAsync();
            var availableTypes = _webServerFactory.GetAvailableWebServerTypes();
        
            return Ok(new 
            { 
                CurrentType = currentType,
                AvailableTypes = availableTypes
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get web server type setting");
            return StatusCode(500, new { error = "Failed to get setting" });
        }
    }

    [HttpPost("webserver-type")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<IActionResult> SetWebServerType([FromBody] SetWebServerTypeRequest request)
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        var availableTypes = _webServerFactory.GetAvailableWebServerTypes();
        
        if (!availableTypes.Contains(request.Type))
        {
            return BadRequest($"Web server type '{request.Type}' is not available. Available types: {string.Join(", ", availableTypes)}");
        }

        // Optionally save the selection to configuration
        _configuration["WebServer:PreferredType"] = request.Type;
        
        var currentType = await _webServerFactory.GetSupportedWebServerTypeAsync();
        return Ok(new { Message = $"Web server type set to {request.Type}", CurrentType = currentType });
    }

    [HttpPost("sites/{siteName}/stop")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<IActionResult> StopSite(string siteName)
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            var webServerService = await _webServerFactory.CreateWebServerServiceAsync();
            var result = await webServerService.StopSiteAsync(siteName);
            var dto = new WebServerResultDto
            {
                Success = result.Success,
                Message = result.Message,
                Data = result.Data is null ? null : new DataDto
                {
                    ServiceName = result.Data.ServiceName,
                    Status = result.Data.Status,
                    Details = result.Data.Details,
                    PoolName = result.Data.PoolName
                }
            };

            if (result.Success)
            {
                return Ok(dto);
            }
            else
            {
                return BadRequest(dto);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping site {SiteName}", siteName);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    [HttpPost("apppools/{poolName}/start")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<IActionResult> StartAppPool(string poolName)
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            var webServerService = await _webServerFactory.CreateWebServerServiceAsync();
            var result = await webServerService.StartAppPoolAsync(poolName);
            var dto = new WebServerResultDto
            {
                Success = result.Success,
                Message = result.Message,
                Data = result.Data is null ? null : new DataDto
                {
                    ServiceName = result.Data.ServiceName,
                    Status = result.Data.Status,
                    Details = result.Data.Details,
                    PoolName = result.Data.PoolName
                }
            };

            if (result.Success)
            {
                return Ok(dto);
            }
            else
            {
                return BadRequest(dto);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting app pool {PoolName}", poolName);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    [HttpPost("apppools/{poolName}/stop")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<IActionResult> StopAppPool(string poolName)
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            var webServerService = await _webServerFactory.CreateWebServerServiceAsync();
            var result = await webServerService.StopAppPoolAsync(poolName);
            var dto = new WebServerResultDto
            {
                Success = result.Success,
                Message = result.Message,
                Data = result.Data is null ? null : new DataDto
                {
                    ServiceName = result.Data.ServiceName,
                    Status = result.Data.Status,
                    Details = result.Data.Details,
                    PoolName = result.Data.PoolName
                }
            };

            if (result.Success)
            {
                return Ok(dto);
            }
            else
            {
                return BadRequest(dto);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping app pool {PoolName}", poolName);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    [HttpGet("sites/{siteName}/status")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> GetSiteStatus(string siteName, [FromQuery] string? poolName = null)
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            if (OperatingSystem.IsWindows() && _iisStatusService != null)
            {
                var status = await _iisStatusService.GetServerStatusAsync(siteName, poolName);
                var dto = new ServerStatusInfo
                {
                    ServerName = status.ServerName,
                    SiteName = status.SiteName,
                    PoolName = status.PoolName,
                    SiteStatus = status.SiteStatus,
                    PoolStatus = status.PoolStatus,
                    IsStatusLoading = status.IsStatusLoading,
                    IsHealthy = status.IsHealthy,
                    LastUpdated = status.LastUpdated,
                    ErrorMessage = status.ErrorMessage
                };
                return Ok(dto);
            }

            var webServerService = await _webServerFactory.CreateWebServerServiceAsync();
            var siteResult = await webServerService.GetSiteStatusAsync(siteName);
        var resultDto = new WebServerResultDto
            {
                Success = siteResult.Success,
                Message = siteResult.Message,
                Data = siteResult.Data is null ? null : new DataDto
                {
                    ServiceName = siteResult.Data.ServiceName,
                    Status = siteResult.Data.Status,
                    Details = siteResult.Data.Details,
                    PoolName = siteResult.Data.PoolName
                }
            };
            return Ok(resultDto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting status for site {SiteName}", siteName);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    [HttpGet("sites")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> GetAllSites()
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            var webServerService = await _webServerFactory.CreateWebServerServiceAsync();
            var sites = await webServerService.GetAllSitesAsync();
            var dto = sites.Select(s => new WebServerStatus
            {
                Name = s.Name,
                Status = s.Status,
                Type = s.Type,
                Port = s.Port,
                IsRunning = s.IsRunning,
                LastChecked = s.LastChecked,
                ErrorMessage = s.ErrorMessage,
                Properties = s.Properties
            });
            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all sites");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    [HttpGet("apppools")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> GetAllAppPools()
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            var webServerService = await _webServerFactory.CreateWebServerServiceAsync();
            var appPools = await webServerService.GetAllAppPoolsAsync();
            var dto = appPools.Select(p => new WebServerStatus
            {
                Name = p.Name,
                Status = p.Status,
                Type = p.Type,
                Port = p.Port,
                IsRunning = p.IsRunning,
                LastChecked = p.LastChecked,
                ErrorMessage = p.ErrorMessage,
                Properties = p.Properties
            });
            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all app pools");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    [HttpPost("status/multiple")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> GetMultipleServerStatus([FromBody] ServerRequest[] requests)
    {
        if (!IsWebServerSupported || _iisStatusService == null || !OperatingSystem.IsWindows())
        {
            return BadRequest("Server status checking is not supported on this platform");
        }

        try
        {
            var statuses = await _iisStatusService.GetMultipleServersStatusAsync(requests);
            var dto = statuses.Select(s => new ServerStatusInfo
            {
                ServerName = s.ServerName,
                SiteName = s.SiteName,
                PoolName = s.PoolName,
                SiteStatus = s.SiteStatus,
                PoolStatus = s.PoolStatus,
                IsStatusLoading = s.IsStatusLoading,
                IsHealthy = s.IsHealthy,
                LastUpdated = s.LastUpdated,
                ErrorMessage = s.ErrorMessage
            });
            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting multiple server statuses");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    // Additional methods with detailed information
    [HttpGet("sites/detailed")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> GetAllSitesDetailed()
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            var webServerService = await _webServerFactory.CreateWebServerServiceAsync();
            var sites = await webServerService.GetAllSitesAsync();
            
            // Group by status for easier display
            var result = new
            {
                TotalSites = sites.Count,
                RunningSites = sites.Count(s => s.IsRunning),
                StoppedSites = sites.Count(s => !s.IsRunning),
                Sites = sites.OrderBy(s => s.Name).ToList(),
                LastUpdated = DateTime.UtcNow
            };
            
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting detailed sites information");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    [HttpGet("apppools/detailed")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> GetAllAppPoolsDetailed()
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            var webServerService = await _webServerFactory.CreateWebServerServiceAsync();
            var appPools = await webServerService.GetAllAppPoolsAsync();
            
            // Group by status for easier display
            var result = new
            {
                TotalAppPools = appPools.Count,
                RunningAppPools = appPools.Count(s => s.IsRunning),
                StoppedAppPools = appPools.Count(s => !s.IsRunning),
                AppPools = appPools.OrderBy(s => s.Name).ToList(),
                LastUpdated = DateTime.UtcNow
            };
            
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting detailed app pools information");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    [HttpGet("overview")]
    [Authorize(Roles = Roles.ReadRoles)]
    public async Task<IActionResult> GetIisOverview()
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            var webServerService = await _webServerFactory.CreateWebServerServiceAsync();
            var sitesTask = webServerService.GetAllSitesAsync();
            var appPoolsTask = webServerService.GetAllAppPoolsAsync();
            
            await Task.WhenAll(sitesTask, appPoolsTask);
            
            var sites = await sitesTask;
            var appPools = await appPoolsTask;
            
            var webServerType = await _webServerFactory.GetSupportedWebServerTypeAsync();

            var overview = new
            {
                ServerName = Environment.MachineName,
                Platform = $"{_platformService.GetPlatform()}/{webServerType}",
                LastUpdated = DateTime.UtcNow,
                Sites = new
                {
                    Total = sites.Count,
                    Running = sites.Count(s => s.IsRunning),
                    Stopped = sites.Count(s => !s.IsRunning),
                    Details = sites.Select(s => new { s.Name, s.Status, s.Port }).ToList()
                },
                AppPools = new
                {
                    Total = appPools.Count,
                    Running = appPools.Count(s => s.IsRunning),
                    Stopped = appPools.Count(s => !s.IsRunning),
                    Details = appPools.Select(s => new { s.Name, s.Status }).ToList()
                }
            };
            
            return Ok(overview);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting web server overview");
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
    
    [HttpPost("sites/{siteName}/start")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<IActionResult> StartSite(string siteName)
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            var webServerService = await _webServerFactory.CreateWebServerServiceAsync();
            var result = await webServerService.StartSiteAsync(siteName);
            var dto = new WebServerResultDto
            {
                Success = result.Success,
                Message = result.Message,
            Data = result.Data is null ? null : new DataDto
                {
                    ServiceName = result.Data.ServiceName,
                    Status = result.Data.Status,
                    Details = result.Data.Details,
                    PoolName = result.Data.PoolName
                }
            };

            if (result.Success)
            {
                return Ok(dto);
            }
            else
            {
                return BadRequest(dto);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting site {SiteName}", siteName);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}