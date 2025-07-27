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
                // Инициализация счетчиков производительности для Windows
                _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
                _memoryCounter = new PerformanceCounter("Memory", "Available MBytes");
                
                // Первый вызов NextValue() для инициализации счетчиков
                _cpuCounter.NextValue();
                _memoryCounter.NextValue();
                
                _isInitialized = true;
                _logger.LogInformation("SystemMetricsCollector initialized successfully for Windows");
            }
            else if (OperatingSystem.IsLinux())
            {
                // Linux поддерживается через /proc файловую систему
                _isInitialized = true;
                _logger.LogInformation("SystemMetricsCollector initialized successfully for Linux");
            }
            else if (OperatingSystem.IsMacOS())
            {
                // macOS поддерживается (базовая реализация)
                _isInitialized = true;
                _logger.LogInformation("SystemMetricsCollector initialized successfully for macOS");
            }
            else
            {
                // Даже на неподдерживаемых платформах собираем базовые метрики
                _isInitialized = true;
                _logger.LogInformation("SystemMetricsCollector initialized with basic cross-platform metrics");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize SystemMetricsCollector");
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

        var platform = OperatingSystem.IsWindows() ? "Windows" : 
                      OperatingSystem.IsLinux() ? "Linux" : 
                      OperatingSystem.IsMacOS() ? "macOS" : "Unknown";
        
        _logger.LogInformation("SystemMetricsCollector started on {Platform}", platform);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                CollectSystemMetrics();
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

    private void CollectSystemMetrics()
    {
        try
        {
            // Кроссплатформенные метрики
            CollectCrossPlatformMetrics();
            
            // Windows-специфичные метрики (если доступны)
            if (OperatingSystem.IsWindows() && _isInitialized && _cpuCounter != null && _memoryCounter != null)
            {
                CollectWindowsSpecificMetrics();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect some system metrics");
        }
    }

    /// <summary>
    /// Сбор кроссплатформенных метрик, доступных на всех ОС
    /// </summary>
    private void CollectCrossPlatformMetrics()
    {
        // Process-specific metrics (работают на всех платформах)
        var currentProcess = Process.GetCurrentProcess();
        _metrics.SetGauge("process_memory_usage_mb", currentProcess.WorkingSet64 / (1024.0 * 1024.0));
        _metrics.SetGauge("process_thread_count", currentProcess.Threads.Count);
        
        // GC метрики (работают на всех платформах)
        _metrics.SetGauge("gc_total_memory_mb", GC.GetTotalMemory(false) / (1024.0 * 1024.0));
        _metrics.SetGauge("gc_generation_0_collections", GC.CollectionCount(0));
        _metrics.SetGauge("gc_generation_1_collections", GC.CollectionCount(1));
        _metrics.SetGauge("gc_generation_2_collections", GC.CollectionCount(2));
        
        // Environment метрики
        _metrics.SetGauge("environment_processor_count", Environment.ProcessorCount);
        _metrics.SetGauge("environment_tick_count", Environment.TickCount64);
        
        // Платформо-специфичные метрики
        if (OperatingSystem.IsLinux())
        {
            CollectLinuxMetrics();
        }
        else if (OperatingSystem.IsMacOS())
        {
            CollectMacOSMetrics();
        }
        
        _logger.LogDebug("Cross-platform system metrics collected successfully");
    }

    /// <summary>
    /// Сбор Windows-специфичных метрик через PerformanceCounter
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void CollectWindowsSpecificMetrics()
    {
        try
        {
            // CPU usage (только Windows)
            var cpuUsage = _cpuCounter!.NextValue();
            _metrics.SetGauge("system_cpu_usage", cpuUsage);

            // Memory usage (только Windows)
            var availableMemoryMb = _memoryCounter!.NextValue();
            var totalMemoryMb = GC.GetTotalMemory(false) / (1024 * 1024);
            var memoryUsagePercent = availableMemoryMb > 0 ? 
                Math.Max(0, Math.Min(100, ((totalMemoryMb / availableMemoryMb) * 100))) : 0;
            _metrics.SetGauge("system_memory_usage", memoryUsagePercent);
            _metrics.SetGauge("system_available_memory_mb", availableMemoryMb);

            _logger.LogDebug("Windows-specific metrics collected: CPU={CpuUsage:F1}%, Memory={MemoryUsage:F1}%", 
                cpuUsage, memoryUsagePercent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect Windows-specific metrics");
        }
    }

    /// <summary>
    /// Сбор Linux-специфичных метрик через /proc файловую систему
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private void CollectLinuxMetrics()
    {
        try
        {
            // CPU метрики через /proc/stat
            if (File.Exists("/proc/stat"))
            {
                var cpuUsage = GetLinuxCpuUsage();
                if (cpuUsage.HasValue)
                {
                    _metrics.SetGauge("system_cpu_usage", cpuUsage.Value);
                }
            }

            // Memory метрики через /proc/meminfo
            if (File.Exists("/proc/meminfo"))
            {
                var memoryInfo = GetLinuxMemoryInfo();
                if (memoryInfo.HasValue)
                {
                    _metrics.SetGauge("system_memory_usage", memoryInfo.Value.UsagePercent);
                    _metrics.SetGauge("system_available_memory_mb", memoryInfo.Value.AvailableMb);
                    _metrics.SetGauge("system_total_memory_mb", memoryInfo.Value.TotalMb);
                }
            }

            // Load average через /proc/loadavg
            if (File.Exists("/proc/loadavg"))
            {
                var loadAvg = GetLinuxLoadAverage();
                if (loadAvg.HasValue)
                {
                    _metrics.SetGauge("system_load_average_1min", loadAvg.Value);
                }
            }

            _logger.LogDebug("Linux-specific metrics collected successfully");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect Linux-specific metrics: {Error}", ex.Message);
        }
    }

    /// <summary>
    /// Сбор macOS-специфичных метрик
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private void CollectMacOSMetrics()
    {
        try
        {
            // Для macOS можно использовать команды system_profiler, top, vm_stat
            // Здесь базовая реализация, которую можно расширить
            _logger.LogDebug("macOS-specific metrics collection - basic implementation");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to collect macOS-specific metrics: {Error}", ex.Message);
        }
    }

    private double? GetLinuxCpuUsage()
    {
        try
        {
            var lines = File.ReadAllLines("/proc/stat");
            var cpuLine = lines.FirstOrDefault(l => l.StartsWith("cpu "));
            if (cpuLine == null) return null;

            var parts = cpuLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) return null;

            var idle = long.Parse(parts[4]);
            var total = parts.Skip(1).Take(7).Sum(x => long.Parse(x));
            
            return total > 0 ? Math.Round((double)(total - idle) / total * 100, 2) : 0;
        }
        catch
        {
            return null;
        }
    }

    private (double UsagePercent, double AvailableMb, double TotalMb)? GetLinuxMemoryInfo()
    {
        try
        {
            var lines = File.ReadAllLines("/proc/meminfo");
            var memInfo = lines
                .Where(l => l.StartsWith("MemTotal:") || l.StartsWith("MemAvailable:"))
                .ToDictionary(
                    l => l.Split(':')[0],
                    l => long.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1])
                );

            if (!memInfo.ContainsKey("MemTotal") || !memInfo.ContainsKey("MemAvailable"))
                return null;

            var totalKb = memInfo["MemTotal"];
            var availableKb = memInfo["MemAvailable"];
            var usedKb = totalKb - availableKb;

            var totalMb = totalKb / 1024.0;
            var availableMb = availableKb / 1024.0;
            var usagePercent = Math.Round((double)usedKb / totalKb * 100, 2);

            return (usagePercent, availableMb, totalMb);
        }
        catch
        {
            return null;
        }
    }

    private double? GetLinuxLoadAverage()
    {
        try
        {
            var loadAvgText = File.ReadAllText("/proc/loadavg");
            var parts = loadAvgText.Split(' ');
            return parts.Length > 0 && double.TryParse(parts[0], out var loadAvg) ? loadAvg : null;
        }
        catch
        {
            return null;
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
