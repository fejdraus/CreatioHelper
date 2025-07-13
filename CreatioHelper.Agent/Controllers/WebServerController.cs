using Microsoft.AspNetCore.Mvc;
using CreatioHelper.Core.Models;
using CreatioHelper.Agent.Services.Windows;
using CreatioHelper.Core.Abstractions;

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

    // Добавляем свойство IsWebServerSupported
    private bool IsWebServerSupported => _webServerFactory.IsWebServerSupported();

    [HttpGet("info")]
    public async Task<IActionResult> GetWebServerInfo()
    {
        return Ok(new
        {
            Platform = _platformService.GetPlatform().ToString(),
            SupportedWebServer = await _webServerFactory.GetSupportedWebServerTypeAsync(), // Теперь async
            AvailableWebServerTypes = _webServerFactory.GetAvailableWebServerTypes(),
            IsSupported = IsWebServerSupported,
            Features = new[]
            {
                new { Name = "StartSite", Available = IsWebServerSupported },
                new { Name = "StopSite", Available = IsWebServerSupported },
                new { Name = "StartAppPool", Available = IsWebServerSupported },
                new { Name = "StopAppPool", Available = IsWebServerSupported },
                new { Name = "GetStatus", Available = IsWebServerSupported },
                new { Name = "IIS", Available = _platformService.IsFeatureSupported(FeatureNames.IISManagement) },
                new { Name = "WindowsService", Available = _platformService.IsFeatureSupported(FeatureNames.WindowsServiceManagement) },
                new { Name = "Systemd", Available = _platformService.IsFeatureSupported(FeatureNames.SystemdManagement) },
                new { Name = "Launchd", Available = _platformService.IsFeatureSupported(FeatureNames.LaunchdManagement) },
                new { Name = "FileSync", Available = _platformService.IsFeatureSupported(FeatureNames.FileSync) }
            }
        });
    }
    
    [HttpGet("webserver-type")]
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
            return StatusCode(500, new { Message = "Failed to get setting", Error = ex.Message });
        }
    }

    [HttpPost("webserver-type")]
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

        // Здесь можно сохранить выбор в конфигурацию
        _configuration["WebServer:PreferredType"] = request.Type;
        
        var currentType = await _webServerFactory.GetSupportedWebServerTypeAsync();
        return Ok(new { Message = $"Web server type set to {request.Type}", CurrentType = currentType });
    }

    [HttpPost("sites/{siteName}/stop")]
    public async Task<IActionResult> StopSite(string siteName)
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            var webServerService = _webServerFactory.CreateWebServerService();
            var result = await webServerService.StopSiteAsync(siteName);
            
            if (result.Success)
            {
                return Ok(result);
            }
            else
            {
                return BadRequest(result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping site {SiteName}", siteName);
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpPost("apppools/{poolName}/start")]
    public async Task<IActionResult> StartAppPool(string poolName)
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            var webServerService = _webServerFactory.CreateWebServerService();
            var result = await webServerService.StartAppPoolAsync(poolName);
            
            if (result.Success)
            {
                return Ok(result);
            }
            else
            {
                return BadRequest(result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting app pool {PoolName}", poolName);
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpPost("apppools/{poolName}/stop")]
    public async Task<IActionResult> StopAppPool(string poolName)
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            var webServerService = _webServerFactory.CreateWebServerService();
            var result = await webServerService.StopAppPoolAsync(poolName);
            
            if (result.Success)
            {
                return Ok(result);
            }
            else
            {
                return BadRequest(result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping app pool {PoolName}", poolName);
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpGet("sites/{siteName}/status")]
    public async Task<IActionResult> GetSiteStatus(string siteName, [FromQuery] string? poolName = null)
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            if (_iisStatusService != null)
            {
                var status = await _iisStatusService.GetServerStatusAsync(siteName, poolName);
                return Ok(status);
            }
            else
            {
                var webServerService = _webServerFactory.CreateWebServerService();
                var siteResult = await webServerService.GetSiteStatusAsync(siteName);
                return Ok(siteResult);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting status for site {SiteName}", siteName);
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpGet("sites")]
    public async Task<IActionResult> GetAllSites()
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            var webServerService = _webServerFactory.CreateWebServerService();
            var sites = await webServerService.GetAllSitesAsync();
            return Ok(sites);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all sites");
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpGet("apppools")]
    public async Task<IActionResult> GetAllAppPools()
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            var webServerService = _webServerFactory.CreateWebServerService();
            var appPools = await webServerService.GetAllAppPoolsAsync();
            return Ok(appPools);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all app pools");
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpPost("status/multiple")]
    public async Task<IActionResult> GetMultipleServerStatus([FromBody] ServerRequest[] requests)
    {
        if (!IsWebServerSupported || _iisStatusService == null)
        {
            return BadRequest("Server status checking is not supported on this platform");
        }

        try
        {
            var statuses = await _iisStatusService.GetMultipleServersStatusAsync(requests);
            return Ok(statuses);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting multiple server statuses");
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    // Новые методы с детальной информацией
    [HttpGet("sites/detailed")]
    public async Task<IActionResult> GetAllSitesDetailed()
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            var webServerService = _webServerFactory.CreateWebServerService();
            var sites = await webServerService.GetAllSitesAsync();
            
            // Группируем по статусу для удобного отображения
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
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpGet("apppools/detailed")]
    public async Task<IActionResult> GetAllAppPoolsDetailed()
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            var webServerService = _webServerFactory.CreateWebServerService();
            var appPools = await webServerService.GetAllAppPoolsAsync();
            
            // Группируем по статусу для удобного отображения
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
            return StatusCode(500, new { Message = ex.Message });
        }
    }

    [HttpGet("overview")]
    public async Task<IActionResult> GetIisOverview()
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            var webServerService = _webServerFactory.CreateWebServerService();
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
            return StatusCode(500, new { Message = ex.Message });
        }
    }
    
    [HttpPost("sites/{siteName}/start")]
    public async Task<IActionResult> StartSite(string siteName)
    {
        if (!IsWebServerSupported)
        {
            return BadRequest("Web server management is not supported on this platform");
        }

        try
        {
            var webServerService = _webServerFactory.CreateWebServerService();
            var result = await webServerService.StartSiteAsync(siteName);
        
            if (result.Success)
            {
                return Ok(result);
            }
            else
            {
                return BadRequest(result);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting site {SiteName}", siteName);
            return StatusCode(500, new { Message = ex.Message });
        }
    }
}