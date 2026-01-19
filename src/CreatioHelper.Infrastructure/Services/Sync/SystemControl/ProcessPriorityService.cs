using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.SystemControl;

/// <summary>
/// Service for managing process priority.
/// Based on Syncthing's setLowPriority option.
/// </summary>
public interface IProcessPriorityService
{
    /// <summary>
    /// Get current process priority.
    /// </summary>
    ProcessPriorityLevel GetCurrentPriority();

    /// <summary>
    /// Set process priority.
    /// </summary>
    bool SetPriority(ProcessPriorityLevel priority);

    /// <summary>
    /// Set low priority mode (reduces CPU/IO priority).
    /// </summary>
    bool SetLowPriority(bool enabled);

    /// <summary>
    /// Check if low priority mode is enabled.
    /// </summary>
    bool IsLowPriorityEnabled { get; }

    /// <summary>
    /// Get process priority statistics.
    /// </summary>
    ProcessPriorityStats GetStats();

    /// <summary>
    /// Set thread priority for current thread.
    /// </summary>
    bool SetThreadPriority(ThreadPriorityLevel priority);

    /// <summary>
    /// Get current thread priority.
    /// </summary>
    ThreadPriorityLevel GetCurrentThreadPriority();

    /// <summary>
    /// Set I/O priority (Windows only).
    /// </summary>
    bool SetIoPriority(IoPriorityLevel priority);

    /// <summary>
    /// Run action with temporarily lowered priority.
    /// </summary>
    T RunWithLowPriority<T>(Func<T> action);

    /// <summary>
    /// Reset to normal priority.
    /// </summary>
    bool ResetToNormal();
}

/// <summary>
/// Process priority levels.
/// </summary>
public enum ProcessPriorityLevel
{
    Idle,
    BelowNormal,
    Normal,
    AboveNormal,
    High,
    RealTime
}

/// <summary>
/// Thread priority levels.
/// </summary>
public enum ThreadPriorityLevel
{
    Lowest,
    BelowNormal,
    Normal,
    AboveNormal,
    Highest
}

/// <summary>
/// I/O priority levels.
/// </summary>
public enum IoPriorityLevel
{
    VeryLow,
    Low,
    Normal,
    High
}

/// <summary>
/// Process priority statistics.
/// </summary>
public class ProcessPriorityStats
{
    public ProcessPriorityLevel CurrentPriority { get; set; }
    public ProcessPriorityLevel OriginalPriority { get; set; }
    public bool LowPriorityEnabled { get; set; }
    public DateTime? LowPriorityEnabledAt { get; set; }
    public long PriorityChanges { get; set; }
    public TimeSpan TimeInLowPriority { get; set; }
    public string ProcessName { get; set; } = string.Empty;
    public int ProcessId { get; set; }
    public long WorkingSetMemory { get; set; }
    public TimeSpan TotalProcessorTime { get; set; }
}

/// <summary>
/// Configuration for process priority service.
/// </summary>
public class ProcessPriorityConfiguration
{
    /// <summary>
    /// Enable low priority by default.
    /// </summary>
    public bool EnableLowPriorityByDefault { get; set; } = false;

    /// <summary>
    /// Priority level when low priority is enabled.
    /// </summary>
    public ProcessPriorityLevel LowPriorityLevel { get; set; } = ProcessPriorityLevel.BelowNormal;

    /// <summary>
    /// Also lower I/O priority when low priority is enabled.
    /// </summary>
    public bool LowerIoPriority { get; set; } = true;

    /// <summary>
    /// Also lower thread priority when low priority is enabled.
    /// </summary>
    public bool LowerThreadPriority { get; set; } = false;

    /// <summary>
    /// Log priority changes.
    /// </summary>
    public bool LogPriorityChanges { get; set; } = true;
}

/// <summary>
/// Implementation of process priority service.
/// </summary>
public class ProcessPriorityService : IProcessPriorityService
{
    private readonly ILogger<ProcessPriorityService> _logger;
    private readonly ProcessPriorityConfiguration _config;
    private readonly Process _currentProcess;
    private readonly ProcessPriorityLevel _originalPriority;
    private bool _lowPriorityEnabled;
    private DateTime? _lowPriorityEnabledAt;
    private long _priorityChanges;
    private TimeSpan _totalTimeInLowPriority;
    private readonly object _lock = new();

    public ProcessPriorityService(
        ILogger<ProcessPriorityService> logger,
        ProcessPriorityConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new ProcessPriorityConfiguration();
        _currentProcess = Process.GetCurrentProcess();
        _originalPriority = MapFromSystemPriority(_currentProcess.PriorityClass);

        if (_config.EnableLowPriorityByDefault)
        {
            SetLowPriority(true);
        }
    }

    /// <inheritdoc />
    public bool IsLowPriorityEnabled => _lowPriorityEnabled;

    /// <inheritdoc />
    public ProcessPriorityLevel GetCurrentPriority()
    {
        try
        {
            _currentProcess.Refresh();
            return MapFromSystemPriority(_currentProcess.PriorityClass);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get current process priority");
            return ProcessPriorityLevel.Normal;
        }
    }

    /// <inheritdoc />
    public bool SetPriority(ProcessPriorityLevel priority)
    {
        try
        {
            var systemPriority = MapToSystemPriority(priority);
            _currentProcess.PriorityClass = systemPriority;

            lock (_lock)
            {
                _priorityChanges++;
            }

            if (_config.LogPriorityChanges)
            {
                _logger.LogInformation("Set process priority to {Priority}", priority);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set process priority to {Priority}", priority);
            return false;
        }
    }

    /// <inheritdoc />
    public bool SetLowPriority(bool enabled)
    {
        lock (_lock)
        {
            if (_lowPriorityEnabled == enabled)
            {
                return true;
            }

            var targetPriority = enabled ? _config.LowPriorityLevel : ProcessPriorityLevel.Normal;

            if (!SetPriority(targetPriority))
            {
                return false;
            }

            if (enabled && _config.LowerIoPriority)
            {
                SetIoPriority(IoPriorityLevel.Low);
            }
            else if (!enabled && _config.LowerIoPriority)
            {
                SetIoPriority(IoPriorityLevel.Normal);
            }

            if (enabled && _config.LowerThreadPriority)
            {
                SetThreadPriority(ThreadPriorityLevel.BelowNormal);
            }
            else if (!enabled && _config.LowerThreadPriority)
            {
                SetThreadPriority(ThreadPriorityLevel.Normal);
            }

            if (_lowPriorityEnabled && !enabled && _lowPriorityEnabledAt.HasValue)
            {
                _totalTimeInLowPriority += DateTime.UtcNow - _lowPriorityEnabledAt.Value;
            }

            _lowPriorityEnabled = enabled;
            _lowPriorityEnabledAt = enabled ? DateTime.UtcNow : null;

            _logger.LogInformation("Low priority mode {State}", enabled ? "enabled" : "disabled");

            return true;
        }
    }

    /// <inheritdoc />
    public ProcessPriorityStats GetStats()
    {
        _currentProcess.Refresh();

        var timeInLowPriority = _totalTimeInLowPriority;
        if (_lowPriorityEnabled && _lowPriorityEnabledAt.HasValue)
        {
            timeInLowPriority += DateTime.UtcNow - _lowPriorityEnabledAt.Value;
        }

        return new ProcessPriorityStats
        {
            CurrentPriority = GetCurrentPriority(),
            OriginalPriority = _originalPriority,
            LowPriorityEnabled = _lowPriorityEnabled,
            LowPriorityEnabledAt = _lowPriorityEnabledAt,
            PriorityChanges = _priorityChanges,
            TimeInLowPriority = timeInLowPriority,
            ProcessName = _currentProcess.ProcessName,
            ProcessId = _currentProcess.Id,
            WorkingSetMemory = _currentProcess.WorkingSet64,
            TotalProcessorTime = _currentProcess.TotalProcessorTime
        };
    }

    /// <inheritdoc />
    public bool SetThreadPriority(ThreadPriorityLevel priority)
    {
        try
        {
            Thread.CurrentThread.Priority = MapToSystemThreadPriority(priority);

            if (_config.LogPriorityChanges)
            {
                _logger.LogDebug("Set thread priority to {Priority}", priority);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set thread priority to {Priority}", priority);
            return false;
        }
    }

    /// <inheritdoc />
    public ThreadPriorityLevel GetCurrentThreadPriority()
    {
        return MapFromSystemThreadPriority(Thread.CurrentThread.Priority);
    }

    /// <inheritdoc />
    public bool SetIoPriority(IoPriorityLevel priority)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.LogDebug("I/O priority is only supported on Windows");
            return false;
        }

        try
        {
            // Windows I/O priority requires P/Invoke
            // For now, we log and return true as a stub
            _logger.LogDebug("Setting I/O priority to {Priority} (requires native call)", priority);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set I/O priority to {Priority}", priority);
            return false;
        }
    }

    /// <inheritdoc />
    public T RunWithLowPriority<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var wasLowPriority = _lowPriorityEnabled;

        try
        {
            if (!wasLowPriority)
            {
                SetLowPriority(true);
            }

            return action();
        }
        finally
        {
            if (!wasLowPriority)
            {
                SetLowPriority(false);
            }
        }
    }

    /// <inheritdoc />
    public bool ResetToNormal()
    {
        return SetPriority(ProcessPriorityLevel.Normal) &&
               SetThreadPriority(ThreadPriorityLevel.Normal) &&
               SetIoPriority(IoPriorityLevel.Normal);
    }

    private static ProcessPriorityClass MapToSystemPriority(ProcessPriorityLevel priority)
    {
        return priority switch
        {
            ProcessPriorityLevel.Idle => ProcessPriorityClass.Idle,
            ProcessPriorityLevel.BelowNormal => ProcessPriorityClass.BelowNormal,
            ProcessPriorityLevel.Normal => ProcessPriorityClass.Normal,
            ProcessPriorityLevel.AboveNormal => ProcessPriorityClass.AboveNormal,
            ProcessPriorityLevel.High => ProcessPriorityClass.High,
            ProcessPriorityLevel.RealTime => ProcessPriorityClass.RealTime,
            _ => ProcessPriorityClass.Normal
        };
    }

    private static ProcessPriorityLevel MapFromSystemPriority(ProcessPriorityClass priority)
    {
        return priority switch
        {
            ProcessPriorityClass.Idle => ProcessPriorityLevel.Idle,
            ProcessPriorityClass.BelowNormal => ProcessPriorityLevel.BelowNormal,
            ProcessPriorityClass.Normal => ProcessPriorityLevel.Normal,
            ProcessPriorityClass.AboveNormal => ProcessPriorityLevel.AboveNormal,
            ProcessPriorityClass.High => ProcessPriorityLevel.High,
            ProcessPriorityClass.RealTime => ProcessPriorityLevel.RealTime,
            _ => ProcessPriorityLevel.Normal
        };
    }

    private static ThreadPriority MapToSystemThreadPriority(ThreadPriorityLevel priority)
    {
        return priority switch
        {
            ThreadPriorityLevel.Lowest => ThreadPriority.Lowest,
            ThreadPriorityLevel.BelowNormal => ThreadPriority.BelowNormal,
            ThreadPriorityLevel.Normal => ThreadPriority.Normal,
            ThreadPriorityLevel.AboveNormal => ThreadPriority.AboveNormal,
            ThreadPriorityLevel.Highest => ThreadPriority.Highest,
            _ => ThreadPriority.Normal
        };
    }

    private static ThreadPriorityLevel MapFromSystemThreadPriority(ThreadPriority priority)
    {
        return priority switch
        {
            ThreadPriority.Lowest => ThreadPriorityLevel.Lowest,
            ThreadPriority.BelowNormal => ThreadPriorityLevel.BelowNormal,
            ThreadPriority.Normal => ThreadPriorityLevel.Normal,
            ThreadPriority.AboveNormal => ThreadPriorityLevel.AboveNormal,
            ThreadPriority.Highest => ThreadPriorityLevel.Highest,
            _ => ThreadPriorityLevel.Normal
        };
    }
}
