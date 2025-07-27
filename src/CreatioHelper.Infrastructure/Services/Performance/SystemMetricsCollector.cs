using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CreatioHelper.Application.Interfaces;

namespace CreatioHelper.Infrastructure.Services.Performance;

/// <summary>
/// Фоновый сервис для сбора системных метрик производительности
/// </summary>
public class SystemMetricsCollector : BackgroundService
{
    private readonly IMetricsService _metrics;
    private readonly ILogger<SystemMetricsCollector> _logger;
    private readonly PerformanceCounter? _cpuCounter;
    private readonly PerformanceCounter? _memoryCounter;
    private readonly bool _isInitialized;

    public SystemMetricsCollector(IMetricsService metrics, ILogger<SystemMetricsCollector> logger)
    {
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Инициализация счетчиков производительности с обработкой ошибок
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _memoryCounter = new PerformanceCounter("Memory", "Available MBytes");
                
                // Первый вызов NextValue() для инициализации счетчиков
                _cpuCounter.NextValue();
                _memoryCounter.NextValue();
                
                _isInitialized = true;
                _logger.LogInformation("SystemMetricsCollector initialized successfully");
            }
            else
            {
                _logger.LogWarning("SystemMetricsCollector is only supported on Windows");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize SystemMetricsCollector performance counters");
            _isInitialized = false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_isInitialized)
        {
            _logger.LogWarning("SystemMetricsCollector not initialized, skipping metrics collection");
            return;
        }

        _logger.LogInformation("SystemMetricsCollector started");
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CollectSystemMetrics();
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Нормальная остановка сервиса
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error collecting system metrics");
                await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken); // Retry after 1 minute
            }
        }
        
        _logger.LogInformation("SystemMetricsCollector stopped");
    }

    private async Task CollectSystemMetrics()
    {
        if (!_isInitialized || _cpuCounter == null || _memoryCounter == null)
            return;

        try
        {
            // CPU usage
            var cpuUsage = _cpuCounter.NextValue();
            _metrics.SetGauge("system_cpu_usage", cpuUsage);

            // Memory usage
            var availableMemoryMB = _memoryCounter.NextValue();
            var totalMemoryMB = GC.GetTotalMemory(false) / (1024 * 1024);
            var memoryUsagePercent = availableMemoryMB > 0 ? 
                Math.Max(0, Math.Min(100, ((totalMemoryMB / availableMemoryMB) * 100))) : 0;
            _metrics.SetGauge("system_memory_usage", memoryUsagePercent);

            // Process-specific metrics
            var currentProcess = Process.GetCurrentProcess();
            _metrics.SetGauge("process_memory_usage_mb", currentProcess.WorkingSet64 / (1024 * 1024));
            _metrics.SetGauge("process_thread_count", currentProcess.Threads.Count);

            _logger.LogDebug("System metrics collected: CPU={CpuUsage:F1}%, Memory={MemoryUsage:F1}%", 
                cpuUsage, memoryUsagePercent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect some system metrics");
        }
    }

    public override void Dispose()
    {
        try
        {
            _cpuCounter?.Dispose();
            _memoryCounter?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error disposing performance counters");
        }
        
        base.Dispose();
    }
}
