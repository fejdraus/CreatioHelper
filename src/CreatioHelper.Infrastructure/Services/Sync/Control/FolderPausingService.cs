using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Control;

/// <summary>
/// Service for pausing and resuming individual folders.
/// Based on Syncthing's Paused folder configuration option.
/// </summary>
public interface IFolderPausingService
{
    /// <summary>
    /// Check if a folder is paused.
    /// </summary>
    bool IsFolderPaused(string folderId);

    /// <summary>
    /// Pause a folder.
    /// </summary>
    Task PauseFolderAsync(string folderId, string? reason = null);

    /// <summary>
    /// Resume a folder.
    /// </summary>
    Task ResumeFolderAsync(string folderId);

    /// <summary>
    /// Toggle folder pause state.
    /// </summary>
    Task<bool> ToggleFolderPauseAsync(string folderId);

    /// <summary>
    /// Get all paused folders.
    /// </summary>
    IReadOnlyList<string> GetPausedFolders();

    /// <summary>
    /// Get pause information for a folder.
    /// </summary>
    FolderPauseInfo? GetPauseInfo(string folderId);

    /// <summary>
    /// Wait until folder is resumed (or cancellation).
    /// </summary>
    Task WaitUntilResumedAsync(string folderId, CancellationToken ct = default);

    /// <summary>
    /// Event raised when a folder is paused.
    /// </summary>
    event EventHandler<FolderPauseEventArgs>? FolderPaused;

    /// <summary>
    /// Event raised when a folder is resumed.
    /// </summary>
    event EventHandler<FolderPauseEventArgs>? FolderResumed;
}

/// <summary>
/// Information about a paused folder.
/// </summary>
public class FolderPauseInfo
{
    public string FolderId { get; init; } = string.Empty;
    public bool IsPaused { get; init; }
    public string? PauseReason { get; init; }
    public DateTime? PausedAt { get; init; }
    public TimeSpan? PauseDuration => PausedAt.HasValue ? DateTime.UtcNow - PausedAt.Value : null;
}

/// <summary>
/// Event args for folder pause events.
/// </summary>
public class FolderPauseEventArgs : EventArgs
{
    public string FolderId { get; init; } = string.Empty;
    public bool IsPaused { get; init; }
    public string? Reason { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Configuration for folder pausing.
/// </summary>
public class FolderPausingConfiguration
{
    /// <summary>
    /// Initially paused folders.
    /// </summary>
    public HashSet<string> InitiallyPausedFolders { get; } = new();

    /// <summary>
    /// Whether to persist pause state across restarts.
    /// </summary>
    public bool PersistPauseState { get; set; } = true;

    /// <summary>
    /// Auto-resume after duration (null = never auto-resume).
    /// </summary>
    public TimeSpan? AutoResumeAfter { get; set; }
}

/// <summary>
/// Implementation of folder pausing service.
/// </summary>
public class FolderPausingService : IFolderPausingService, IDisposable
{
    private readonly ILogger<FolderPausingService> _logger;
    private readonly FolderPausingConfiguration _config;
    private readonly ConcurrentDictionary<string, FolderPauseState> _pauseStates = new();
    private readonly CancellationTokenSource _disposeCts = new();
    private bool _disposed;

    public event EventHandler<FolderPauseEventArgs>? FolderPaused;
    public event EventHandler<FolderPauseEventArgs>? FolderResumed;

    public FolderPausingService(
        ILogger<FolderPausingService> logger,
        FolderPausingConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new FolderPausingConfiguration();

        // Initialize from config
        foreach (var folderId in _config.InitiallyPausedFolders)
        {
            _pauseStates[folderId] = new FolderPauseState
            {
                IsPaused = true,
                PausedAt = DateTime.UtcNow,
                Reason = "Initially paused from configuration"
            };
        }
    }

    /// <inheritdoc />
    public bool IsFolderPaused(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (_pauseStates.TryGetValue(folderId, out var state))
        {
            return state.IsPaused;
        }
        return false;
    }

    /// <inheritdoc />
    public Task PauseFolderAsync(string folderId, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        var state = _pauseStates.GetOrAdd(folderId, _ => new FolderPauseState());

        lock (state)
        {
            if (state.IsPaused)
            {
                _logger.LogDebug("Folder {FolderId} is already paused", folderId);
                return Task.CompletedTask;
            }

            state.IsPaused = true;
            state.PausedAt = DateTime.UtcNow;
            state.Reason = reason;
            state.ResumeEvent.Reset();
        }

        _logger.LogInformation("Paused folder {FolderId}. Reason: {Reason}", folderId, reason ?? "No reason provided");

        // Start auto-resume timer if configured
        if (_config.AutoResumeAfter.HasValue)
        {
            _ = AutoResumeAfterDelayAsync(folderId, _config.AutoResumeAfter.Value);
        }

        RaiseFolderPaused(folderId, reason);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResumeFolderAsync(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (!_pauseStates.TryGetValue(folderId, out var state))
        {
            return Task.CompletedTask;
        }

        lock (state)
        {
            if (!state.IsPaused)
            {
                _logger.LogDebug("Folder {FolderId} is not paused", folderId);
                return Task.CompletedTask;
            }

            state.IsPaused = false;
            state.PausedAt = null;
            state.Reason = null;
            state.ResumeEvent.Set();
        }

        _logger.LogInformation("Resumed folder {FolderId}", folderId);

        RaiseFolderResumed(folderId);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<bool> ToggleFolderPauseAsync(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (IsFolderPaused(folderId))
        {
            await ResumeFolderAsync(folderId);
            return false; // Now not paused
        }
        else
        {
            await PauseFolderAsync(folderId);
            return true; // Now paused
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetPausedFolders()
    {
        var paused = new List<string>();
        foreach (var kvp in _pauseStates)
        {
            if (kvp.Value.IsPaused)
            {
                paused.Add(kvp.Key);
            }
        }
        return paused;
    }

    /// <inheritdoc />
    public FolderPauseInfo? GetPauseInfo(string folderId)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (_pauseStates.TryGetValue(folderId, out var state))
        {
            lock (state)
            {
                return new FolderPauseInfo
                {
                    FolderId = folderId,
                    IsPaused = state.IsPaused,
                    PauseReason = state.Reason,
                    PausedAt = state.PausedAt
                };
            }
        }

        return new FolderPauseInfo
        {
            FolderId = folderId,
            IsPaused = false
        };
    }

    /// <inheritdoc />
    public async Task WaitUntilResumedAsync(string folderId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderId);

        if (!_pauseStates.TryGetValue(folderId, out var state))
        {
            return; // Not paused
        }

        if (!state.IsPaused)
        {
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);

        try
        {
            await state.ResumeEvent.WaitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(FolderPausingService));
        }
    }

    private async Task AutoResumeAfterDelayAsync(string folderId, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _disposeCts.Token);

            if (IsFolderPaused(folderId))
            {
                _logger.LogInformation("Auto-resuming folder {FolderId} after {Delay}", folderId, delay);
                await ResumeFolderAsync(folderId);
            }
        }
        catch (OperationCanceledException)
        {
            // Service is disposing
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during auto-resume of folder {FolderId}", folderId);
        }
    }

    private void RaiseFolderPaused(string folderId, string? reason)
    {
        FolderPaused?.Invoke(this, new FolderPauseEventArgs
        {
            FolderId = folderId,
            IsPaused = true,
            Reason = reason
        });
    }

    private void RaiseFolderResumed(string folderId)
    {
        FolderResumed?.Invoke(this, new FolderPauseEventArgs
        {
            FolderId = folderId,
            IsPaused = false
        });
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _disposeCts.Cancel();
            _disposeCts.Dispose();

            foreach (var state in _pauseStates.Values)
            {
                state.Dispose();
            }
        }
    }

    private class FolderPauseState : IDisposable
    {
        public bool IsPaused { get; set; }
        public DateTime? PausedAt { get; set; }
        public string? Reason { get; set; }
        public ManualResetEventSlim ResumeEvent { get; } = new(true);

        public void Dispose()
        {
            ResumeEvent.Dispose();
        }
    }
}

/// <summary>
/// Extension for ManualResetEventSlim to support async waiting.
/// </summary>
internal static class ManualResetEventSlimExtensions
{
    public static Task WaitAsync(this ManualResetEventSlim mre, CancellationToken ct)
    {
        return Task.Run(() => mre.Wait(ct), ct);
    }
}
