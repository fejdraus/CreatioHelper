using Microsoft.AspNetCore.Mvc;
using CreatioHelper.Agent.Abstractions;

namespace CreatioHelper.Agent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SystemController : ControllerBase
{
    private readonly IPlatformService _platformService;
    private readonly ILogger<SystemController> _logger;

    public SystemController(IPlatformService platformService, ILogger<SystemController> logger)
    {
        _platformService = platformService;
        _logger = logger;
    }

    [HttpGet("info")]
    public async Task<IActionResult> GetSystemInfo()
    {
        try
        {
            var info = await _platformService.GetSystemInfoAsync();
            return Ok(info);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting system info");
            return StatusCode(500, ex.Message);
        }
    }

    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { Status = "Healthy", Timestamp = DateTime.UtcNow });
    }
}