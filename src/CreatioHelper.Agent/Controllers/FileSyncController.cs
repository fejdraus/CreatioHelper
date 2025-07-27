using CreatioHelper.Contracts.Requests;
using CreatioHelper.Contracts.Responses;

namespace CreatioHelper.Agent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FileSyncController : ControllerBase
{
    private readonly IFileSyncService _fileSyncService;
    private readonly ILogger<FileSyncController> _logger;

    public FileSyncController(IFileSyncService fileSyncService, ILogger<FileSyncController> logger)
    {
        _fileSyncService = fileSyncService;
        _logger = logger;
    }

    [HttpPost("validate-path")]
    public async Task<IActionResult> ValidatePath([FromBody] ValidatePathRequest request)
    {
        try
        {
            var isValid = await _fileSyncService.ValidatePathAsync(request.Path);
            return Ok(new { Path = request.Path, IsValid = isValid });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating path: {Path}", request.Path);
            return StatusCode(500, ex.Message);
        }
    }

    [HttpPost("sync")]
    public async Task<IActionResult> SyncFiles([FromBody] SyncRequest request)
    {
        try
        {
            var result = await _fileSyncService.SyncAsync(request.SourcePath, request.DestinationPath);
            var dto = new SyncResult
            {
                Success = result.Success,
                Message = result.Message,
                BytesTransferred = result.BytesTransferred,
                Duration = result.Duration
            };
            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing files from {Source} to {Destination}",
                request.SourcePath, request.DestinationPath);
            return StatusCode(500, ex.Message);
        }
    }

    [HttpPost("sync-advanced")]
    public async Task<IActionResult> SyncFilesAdvanced([FromBody] SyncOptions options)
    {
        try
        {
            var domainOptions = new SyncOptions
            {
                SourcePath = options.SourcePath,
                DestinationPath = options.DestinationPath,
                OverwriteExisting = options.OverwriteExisting,
                Recursive = options.Recursive,
                ExcludePatterns = options.ExcludePatterns
            };
            var result = await _fileSyncService.SyncAsync(domainOptions);
            var dto = new SyncResult
            {
                Success = result.Success,
                Message = result.Message,
                BytesTransferred = result.BytesTransferred,
                Duration = result.Duration
            };
            return Ok(dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in advanced sync");
            return StatusCode(500, ex.Message);
        }
    }
}
