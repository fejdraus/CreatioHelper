using CreatioHelper.Agent.Authorization;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Entities.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using EventType = CreatioHelper.Domain.Entities.Events.SyncEventType;
using SyncEvent = CreatioHelper.Domain.Entities.Events.SyncEvent;

namespace CreatioHelper.Agent.Controllers;

/// <summary>
/// Extended events API controller with additional features beyond Syncthing compatibility
/// Use /rest/events for Syncthing-compatible API (SyncthingEventsController)
/// </summary>
[ApiController]
[Route("api/events")]
[Authorize(Roles = Roles.ReadRoles)]
public class EventsController : ControllerBase
{
    private readonly IEventLogger _eventLogger;
    private readonly ILogger<EventsController> _logger;

    public EventsController(IEventLogger eventLogger, ILogger<EventsController> logger)
    {
        _eventLogger = eventLogger;
        _logger = logger;
    }

    /// <summary>
    /// Получить события начиная с указанного ID (аналог GET /rest/events)
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetEvents(
        [FromQuery] int since = 0,
        [FromQuery] int limit = 100,
        [FromQuery] int timeout = 60,
        [FromQuery] EventType events = EventType.DefaultEventMask,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var timeoutSpan = TimeSpan.FromSeconds(Math.Max(1, Math.Min(timeout, 300))); // 1-300 секунд
            var maxLimit = Math.Max(1, Math.Min(limit, 10000)); // 1-10000 событий
            
            var eventList = await _eventLogger.GetEventsSinceAsync(since, events, maxLimit, timeoutSpan, cancellationToken);
            
            return Ok(eventList);
        }
        catch (OperationCanceledException)
        {
            return Ok(new List<SyncEvent>()); // Возвращаем пустой список при таймауте
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting events since {Since}: {Message}", since, ex.Message);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Получить события в режиме disk (буферизованный режим, аналог GET /rest/events/disk)
    /// </summary>
    [HttpGet("disk")]
    public async Task<IActionResult> GetEventsFromDisk(
        [FromQuery] int since = 0,
        [FromQuery] int limit = 100,
        [FromQuery] EventType events = EventType.DefaultEventMask,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var maxLimit = Math.Max(1, Math.Min(limit, 10000));
            
            using var bufferedSubscription = _eventLogger.CreateBufferedSubscription(events, 10000);
            var eventList = await bufferedSubscription.GetEventsSinceAsync(since, maxLimit, null, cancellationToken);
            
            return Ok(eventList);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting events from disk since {Since}: {Message}", since, ex.Message);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }

    /// <summary>
    /// Создать подписку на события (для веб-сокетов или SSE)
    /// </summary>
    [HttpPost("subscribe")]
    [Authorize(Roles = Roles.WriteRoles)]
    public Task<IActionResult> CreateSubscription([FromBody] EventSubscriptionRequest request)
    {
        try
        {
            var subscription = _eventLogger.Subscribe(request.EventMask);
            
            // Возвращаем информацию о созданной подписке
            var response = new
            {
                SubscriptionId = Guid.NewGuid().ToString(), // В реальной реализации используется ID из подписки
                EventMask = subscription.EventMask,
                Created = DateTime.UtcNow
            };
            
            return Task.FromResult<IActionResult>(Ok(response));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating event subscription: {Message}", ex.Message);
            return Task.FromResult<IActionResult>(StatusCode(500, new { error = "Internal server error" }));
        }
    }

    /// <summary>
    /// Отправить пользовательское событие (для тестирования)
    /// </summary>
    [HttpPost("test")]
    [Authorize(Roles = Roles.WriteRoles)]
    public Task<IActionResult> SendTestEvent([FromBody] TestEventRequest request)
    {
        try
        {
            var testEvent = new SyncEvent
            {
                Type = request.EventType,
                Message = request.Message,
                Data = request.Data,
                DeviceId = request.DeviceId,
                FolderId = request.FolderId,
                FilePath = request.FilePath
            };
            
            _eventLogger.LogEvent(testEvent);
            
            return Task.FromResult<IActionResult>(Ok(new { success = true, message = "Test event sent" }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error sending test event: {Message}", ex.Message);
            return Task.FromResult<IActionResult>(StatusCode(500, new { error = "Internal server error" }));
        }
    }

    /// <summary>
    /// Получить статистику событий
    /// </summary>
    [HttpGet("stats")]
    public Task<IActionResult> GetEventStatistics(CancellationToken cancellationToken = default)
    {
        try
        {
            var statistics = _eventLogger.GetEventStatistics();
            return Task.FromResult<IActionResult>(Ok(statistics));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting event statistics: {Message}", ex.Message);
            return Task.FromResult<IActionResult>(StatusCode(500, new { error = "Internal server error" }));
        }
    }

    /// <summary>
    /// Очистить старые события
    /// </summary>
    [HttpPost("cleanup")]
    [Authorize(Roles = Roles.WriteRoles)]
    public async Task<IActionResult> CleanupOldEvents([FromBody] CleanupRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            var maxAge = TimeSpan.FromHours(Math.Max(1, Math.Min(request.MaxAgeHours, 24 * 30))); // 1 час - 30 дней
            await _eventLogger.CleanupOldEventsAsync(maxAge, cancellationToken);
            
            return Ok(new { success = true, message = $"Cleaned up events older than {maxAge.TotalHours} hours" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up old events: {Message}", ex.Message);
            return StatusCode(500, new { error = "Internal server error" });
        }
    }
}

/// <summary>
/// Модель запроса для создания подписки на события
/// </summary>
public class EventSubscriptionRequest
{
    /// <summary>
    /// Маска типов событий для подписки
    /// </summary>
    public EventType EventMask { get; set; } = EventType.DefaultEventMask;
}

/// <summary>
/// Модель запроса для отправки тестового события
/// </summary>
public class TestEventRequest
{
    /// <summary>
    /// Тип события
    /// </summary>
    [Required]
    public EventType EventType { get; set; }

    /// <summary>
    /// Сообщение события
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Данные события
    /// </summary>
    public object? Data { get; set; }

    /// <summary>
    /// ID устройства (опционально)
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// ID папки (опционально)
    /// </summary>
    public string? FolderId { get; set; }

    /// <summary>
    /// Путь к файлу (опционально)
    /// </summary>
    public string? FilePath { get; set; }
}

/// <summary>
/// Модель запроса для очистки событий
/// </summary>
public class CleanupRequest
{
    /// <summary>
    /// Максимальный возраст событий в часах
    /// </summary>
    [Range(1, 24 * 30)] // От 1 часа до 30 дней
    public int MaxAgeHours { get; set; } = 24;
}