using System.Diagnostics;

namespace CreatioHelper.Agent.Services;

/// <summary>
/// Background service that periodically logs agent health status
/// Provides "proof of life" and basic system metrics
/// </summary>
public class HeartbeatService : BackgroundService
{
    private readonly ILogger<HeartbeatService> _logger;
    private readonly IConfiguration _configuration;
    private readonly DateTime _startTime;

    public HeartbeatService(ILogger<HeartbeatService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
        _startTime = DateTime.UtcNow;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Get heartbeat interval from configuration (default: 15 minutes)
        var intervalMinutes = _configuration.GetValue<int>("Heartbeat:IntervalMinutes", 15);
        var interval = TimeSpan.FromMinutes(intervalMinutes);

        _logger.LogInformation("HeartbeatService started - will report every {Minutes} minutes", intervalMinutes);

        // Wait a bit before first heartbeat to let the app initialize
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                LogHeartbeat();
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in HeartbeatService");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("HeartbeatService stopped");
    }

    private void LogHeartbeat()
    {
        var uptime = DateTime.UtcNow - _startTime;

        using var process = Process.GetCurrentProcess();

        // Get memory in MB
        var workingSetMb = process.WorkingSet64 / 1024.0 / 1024.0;
        var privateMemoryMb = process.PrivateMemorySize64 / 1024.0 / 1024.0;

        // Get GC stats
        var gcGen0 = GC.CollectionCount(0);
        var gcGen1 = GC.CollectionCount(1);
        var gcGen2 = GC.CollectionCount(2);
        var gcMemoryMb = GC.GetTotalMemory(false) / 1024.0 / 1024.0;

        _logger.LogInformation("╔{Rule}╗", new string('═', BoxWidth));
        _logger.LogInformation("║{Row}║", Row("💓 HEARTBEAT - Agent is running"));
        _logger.LogInformation("╠{Rule}╣", new string('═', BoxWidth));
        _logger.LogInformation("║{Row}║", Row($"Uptime: {FormatUptime(uptime)}"));
        _logger.LogInformation("║{Row}║", Row($"Memory: Working Set {workingSetMb:F1} MB, Private {privateMemoryMb:F1} MB"));
        _logger.LogInformation("║{Row}║", Row($"GC Memory: {gcMemoryMb:F1} MB"));
        _logger.LogInformation("║{Row}║", Row($"GC Collections: Gen0={gcGen0}, Gen1={gcGen1}, Gen2={gcGen2}"));
        _logger.LogInformation("║{Row}║", Row($"Threads: {process.Threads.Count}"));
        _logger.LogInformation("╚{Rule}╝", new string('═', BoxWidth));
    }

    private const int BoxWidth = 63;

    private static string Row(string text)
    {
        var content = "  " + text;
        return content.Length >= BoxWidth
            ? content[..BoxWidth]
            : content.PadRight(BoxWidth);
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
        {
            return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
        }
        else if (uptime.TotalHours >= 1)
        {
            return $"{(int)uptime.TotalHours}h {uptime.Minutes}m";
        }
        else
        {
            return $"{uptime.Minutes}m {uptime.Seconds}s";
        }
    }
}
