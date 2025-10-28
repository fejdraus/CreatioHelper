using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CreatioHelper.Application.Interfaces;

namespace CreatioHelper.Infrastructure.Services.Performance;

/// <summary>
/// Background service that collects system performance metrics.
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
                // Initialize performance counters for Windows
                _cpuCounter = new System.Diagnostics.PerformanceCounter("Processor", "% Processor Time", "_Total");
                _memoryCounter = new System.Diagnostics.PerformanceCounter("Memory", "Available MBytes");
                
                // First NextValue() call initializes counters
                _cpuCounter.NextValue();
                _memoryCounter.NextValue();
                
                _isInitialized = true;
                _logger.LogInformation("SystemMetricsCollector initialized successfully for Windows");
            }
            else if (OperatingSystem.IsLinux())
            {
                // Linux is supported via the /proc filesystem
                _isInitialized = true;
                _logger.LogInformation("SystemMetricsCollector initialized successfully for Linux");
            }
            else if (OperatingSystem.IsMacOS())
            {
                // Basic macOS implementation
                _isInitialized = true;
                _logger.LogInformation("SystemMetricsCollector initialized successfully for macOS");
            }
            else
            {
                // Collect basic metrics even on unsupported platforms
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
                // Graceful service stop
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
            // Cross-platform metrics
            CollectCrossPlatformMetrics();
            
            // Windows-specific metrics (if available)
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
    /// Collects cross-platform metrics available on all operating systems.
    /// </summary>
    private void CollectCrossPlatformMetrics()
    {
        // Process-specific metrics
        var currentProcess = Process.GetCurrentProcess();
        _metrics.SetGauge("process_memory_usage_mb", currentProcess.WorkingSet64 / (1024.0 * 1024.0));
        _metrics.SetGauge("process_thread_count", currentProcess.Threads.Count);
        
        // GC metrics
        _metrics.SetGauge("gc_total_memory_mb", GC.GetTotalMemory(false) / (1024.0 * 1024.0));
        _metrics.SetGauge("gc_generation_0_collections", GC.CollectionCount(0));
        _metrics.SetGauge("gc_generation_1_collections", GC.CollectionCount(1));
        _metrics.SetGauge("gc_generation_2_collections", GC.CollectionCount(2));
        
        // Environment metrics
        _metrics.SetGauge("environment_processor_count", Environment.ProcessorCount);
        _metrics.SetGauge("environment_tick_count", Environment.TickCount64);
        
        // Platform specific metrics
        if (OperatingSystem.IsLinux())
        {
            CollectLinuxMetrics();
        }
        else if (OperatingSystem.IsMacOS())
        {
            CollectMacOsMetrics();
        }
        
        _logger.LogDebug("Cross-platform system metrics collected successfully");
    }

    /// <summary>
    /// Collects Windows-specific metrics using PerformanceCounter.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void CollectWindowsSpecificMetrics()
    {
        try
        {
            // CPU usage (Windows only)
            var cpuUsage = _cpuCounter!.NextValue();
            _metrics.SetGauge("system_cpu_usage", cpuUsage);

            // Memory usage (Windows only)
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
    /// Collects Linux-specific metrics via the /proc filesystem.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private void CollectLinuxMetrics()
    {
        try
        {
            // CPU metrics via /proc/stat
            if (File.Exists("/proc/stat"))
            {
                var cpuUsage = GetLinuxCpuUsage();
                if (cpuUsage.HasValue)
                {
                    _metrics.SetGauge("system_cpu_usage", cpuUsage.Value);
                }
            }

            // Memory metrics via /proc/meminfo
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

            // Load average via /proc/loadavg
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
    /// Collects macOS-specific metrics.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private void CollectMacOsMetrics()
    {
        try
        {
            // For macOS one could use system_profiler, top or vm_stat.
            // This is a basic implementation that can be extended.
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
