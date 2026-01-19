using System.Collections.Concurrent;
using System.Diagnostics;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Entities.Events;
using Microsoft.Extensions.Logging;

using SyncEventType = CreatioHelper.Domain.Entities.Events.SyncEventType;

namespace CreatioHelper.Infrastructure.Services.DeviceManagement;

/// <summary>
/// Implementation of pause coordination for devices and folders.
/// Based on Syncthing's pause handling from lib/model/model.go
/// </summary>
public class PauseCoordinationService : IPauseCoordinationService
{
    private readonly ILogger<PauseCoordinationService> _logger;
    private readonly IDeviceInfoRepository _deviceRepository;
    private readonly IFolderConfigRepository _folderRepository;
    private readonly IEventLogger _eventLogger;

    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task>> _pauseCallbacks = new();
    private readonly TimeSpan _gracefulPauseTimeout;
    private readonly object _statsLock = new();

    private PauseServiceStatistics _statistics = new();

    public PauseCoordinationService(
        ILogger<PauseCoordinationService> logger,
        IDeviceInfoRepository deviceRepository,
        IFolderConfigRepository folderRepository,
        IEventLogger eventLogger,
        TimeSpan? gracefulPauseTimeout = null)
    {
        _logger = logger;
        _deviceRepository = deviceRepository;
        _folderRepository = folderRepository;
        _eventLogger = eventLogger;
        _gracefulPauseTimeout = gracefulPauseTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc />
    public async Task<PauseResult> PauseDeviceAsync(string deviceId, bool graceful = true, CancellationToken cancellationToken = default)
    {
        var result = new PauseResult();
        var sw = Stopwatch.StartNew();

        try
        {
            var device = await _deviceRepository.GetAsync(deviceId);
            if (device == null)
            {
                result.Error = $"Device {deviceId} not found";
                return result;
            }

            if (device.Paused)
            {
                result.AlreadyInState = true;
                result.IsPaused = true;
                result.Success = true;
                return result;
            }

            // If graceful, wait for in-progress operations
            if (graceful)
            {
                result.WaitedForOperations = await WaitForGracefulPauseAsync(deviceId, cancellationToken);
            }

            device.SetPaused(true);
            await _deviceRepository.UpsertAsync(device);

            result.Success = true;
            result.IsPaused = true;

            lock (_statsLock)
            {
                _statistics.TotalDevicePauses++;
                if (graceful && result.WaitedForOperations > 0)
                    _statistics.TotalGracefulWaits++;
            }

            _logger.LogInformation("Paused device {DeviceId} ({DeviceName}), graceful={Graceful}",
                deviceId, device.DeviceName, graceful);

            // Emit event
            _eventLogger.LogEvent(
                SyncEventType.DevicePaused,
                new { DeviceId = deviceId, Graceful = graceful },
                $"Device paused: {device.DeviceName}",
                deviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing device {DeviceId}", deviceId);
            result.Error = ex.Message;
        }
        finally
        {
            result.Duration = sw.Elapsed;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<PauseResult> ResumeDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var result = new PauseResult();
        var sw = Stopwatch.StartNew();

        try
        {
            var device = await _deviceRepository.GetAsync(deviceId);
            if (device == null)
            {
                result.Error = $"Device {deviceId} not found";
                return result;
            }

            if (!device.Paused)
            {
                result.AlreadyInState = true;
                result.IsPaused = false;
                result.Success = true;
                return result;
            }

            device.SetPaused(false);
            await _deviceRepository.UpsertAsync(device);

            result.Success = true;
            result.IsPaused = false;

            lock (_statsLock)
            {
                _statistics.TotalDeviceResumes++;
            }

            _logger.LogInformation("Resumed device {DeviceId} ({DeviceName})",
                deviceId, device.DeviceName);

            // Emit event
            _eventLogger.LogEvent(
                SyncEventType.DeviceResumed,
                new { DeviceId = deviceId },
                $"Device resumed: {device.DeviceName}",
                deviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming device {DeviceId}", deviceId);
            result.Error = ex.Message;
        }
        finally
        {
            result.Duration = sw.Elapsed;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<PauseResult> PauseFolderAsync(string folderId, bool graceful = true, CancellationToken cancellationToken = default)
    {
        var result = new PauseResult();
        var sw = Stopwatch.StartNew();

        try
        {
            var folder = await _folderRepository.GetAsync(folderId);
            if (folder == null)
            {
                result.Error = $"Folder {folderId} not found";
                return result;
            }

            if (folder.Paused)
            {
                result.AlreadyInState = true;
                result.IsPaused = true;
                result.Success = true;
                return result;
            }

            // If graceful, wait for in-progress operations
            if (graceful)
            {
                result.WaitedForOperations = await WaitForGracefulPauseAsync(folderId, cancellationToken);
            }

            folder.SetPaused(true);
            await _folderRepository.UpsertAsync(folder);

            result.Success = true;
            result.IsPaused = true;

            lock (_statsLock)
            {
                _statistics.TotalFolderPauses++;
                if (graceful && result.WaitedForOperations > 0)
                    _statistics.TotalGracefulWaits++;
            }

            _logger.LogInformation("Paused folder {FolderId} ({Label}), graceful={Graceful}",
                folderId, folder.Label, graceful);

            // Emit event
            _eventLogger.LogEvent(
                SyncEventType.FolderPaused,
                new { FolderId = folderId, Graceful = graceful },
                $"Folder paused: {folder.Label}",
                folderId: folderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing folder {FolderId}", folderId);
            result.Error = ex.Message;
        }
        finally
        {
            result.Duration = sw.Elapsed;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<PauseResult> ResumeFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var result = new PauseResult();
        var sw = Stopwatch.StartNew();

        try
        {
            var folder = await _folderRepository.GetAsync(folderId);
            if (folder == null)
            {
                result.Error = $"Folder {folderId} not found";
                return result;
            }

            if (!folder.Paused)
            {
                result.AlreadyInState = true;
                result.IsPaused = false;
                result.Success = true;
                return result;
            }

            folder.SetPaused(false);
            await _folderRepository.UpsertAsync(folder);

            result.Success = true;
            result.IsPaused = false;

            lock (_statsLock)
            {
                _statistics.TotalFolderResumes++;
            }

            _logger.LogInformation("Resumed folder {FolderId} ({Label})",
                folderId, folder.Label);

            // Emit event
            _eventLogger.LogEvent(
                SyncEventType.FolderResumed,
                new { FolderId = folderId },
                $"Folder resumed: {folder.Label}",
                folderId: folderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming folder {FolderId}", folderId);
            result.Error = ex.Message;
        }
        finally
        {
            result.Duration = sw.Elapsed;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<PauseAllResult> PauseAllDevicesAsync(bool graceful = true, CancellationToken cancellationToken = default)
    {
        var result = new PauseAllResult();
        var sw = Stopwatch.StartNew();

        try
        {
            var devices = await _deviceRepository.GetAllAsync();
            foreach (var device in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pauseResult = await PauseDeviceAsync(device.DeviceId, graceful, cancellationToken);
                if (pauseResult.Success)
                {
                    if (pauseResult.AlreadyInState)
                        result.AlreadyInStateCount++;
                    else
                        result.SuccessCount++;
                }
                else
                {
                    result.FailedCount++;
                    if (pauseResult.Error != null)
                        result.Errors[device.DeviceId] = pauseResult.Error;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("PauseAllDevices cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing all devices");
        }
        finally
        {
            result.Duration = sw.Elapsed;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<PauseAllResult> ResumeAllDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = new PauseAllResult();
        var sw = Stopwatch.StartNew();

        try
        {
            var devices = await _deviceRepository.GetAllAsync();
            foreach (var device in devices.Where(d => d.Paused))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var resumeResult = await ResumeDeviceAsync(device.DeviceId, cancellationToken);
                if (resumeResult.Success)
                {
                    if (resumeResult.AlreadyInState)
                        result.AlreadyInStateCount++;
                    else
                        result.SuccessCount++;
                }
                else
                {
                    result.FailedCount++;
                    if (resumeResult.Error != null)
                        result.Errors[device.DeviceId] = resumeResult.Error;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("ResumeAllDevices cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming all devices");
        }
        finally
        {
            result.Duration = sw.Elapsed;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<PauseAllResult> PauseAllFoldersAsync(bool graceful = true, CancellationToken cancellationToken = default)
    {
        var result = new PauseAllResult();
        var sw = Stopwatch.StartNew();

        try
        {
            var folders = await _folderRepository.GetAllAsync();
            foreach (var folder in folders)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pauseResult = await PauseFolderAsync(folder.Id, graceful, cancellationToken);
                if (pauseResult.Success)
                {
                    if (pauseResult.AlreadyInState)
                        result.AlreadyInStateCount++;
                    else
                        result.SuccessCount++;
                }
                else
                {
                    result.FailedCount++;
                    if (pauseResult.Error != null)
                        result.Errors[folder.Id] = pauseResult.Error;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("PauseAllFolders cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error pausing all folders");
        }
        finally
        {
            result.Duration = sw.Elapsed;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<PauseAllResult> ResumeAllFoldersAsync(CancellationToken cancellationToken = default)
    {
        var result = new PauseAllResult();
        var sw = Stopwatch.StartNew();

        try
        {
            var folders = await _folderRepository.GetAllAsync();
            foreach (var folder in folders.Where(f => f.Paused))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var resumeResult = await ResumeFolderAsync(folder.Id, cancellationToken);
                if (resumeResult.Success)
                {
                    if (resumeResult.AlreadyInState)
                        result.AlreadyInStateCount++;
                    else
                        result.SuccessCount++;
                }
                else
                {
                    result.FailedCount++;
                    if (resumeResult.Error != null)
                        result.Errors[folder.Id] = resumeResult.Error;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("ResumeAllFolders cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error resuming all folders");
        }
        finally
        {
            result.Duration = sw.Elapsed;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> IsDevicePausedAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var device = await _deviceRepository.GetAsync(deviceId);
        return device?.Paused ?? false;
    }

    /// <inheritdoc />
    public async Task<bool> IsFolderPausedAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var folder = await _folderRepository.GetAsync(folderId);
        return folder?.Paused ?? false;
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SyncDevice>> GetPausedDevicesAsync(CancellationToken cancellationToken = default)
    {
        var allDevices = await _deviceRepository.GetAllAsync();
        return allDevices.Where(d => d.Paused);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<SyncFolder>> GetPausedFoldersAsync(CancellationToken cancellationToken = default)
    {
        var allFolders = await _folderRepository.GetAllAsync();
        return allFolders.Where(f => f.Paused);
    }

    /// <inheritdoc />
    public void RegisterPauseCallback(string id, Func<CancellationToken, Task> callback)
    {
        _pauseCallbacks[id] = callback;
        _logger.LogDebug("Registered pause callback: {Id}", id);
    }

    /// <inheritdoc />
    public void UnregisterPauseCallback(string id)
    {
        _pauseCallbacks.TryRemove(id, out _);
        _logger.LogDebug("Unregistered pause callback: {Id}", id);
    }

    /// <inheritdoc />
    public PauseServiceStatus GetStatus()
    {
        return new PauseServiceStatus
        {
            RegisteredCallbacks = _pauseCallbacks.Count,
            GracefulPauseTimeout = _gracefulPauseTimeout,
            Statistics = new PauseServiceStatistics
            {
                TotalDevicePauses = _statistics.TotalDevicePauses,
                TotalDeviceResumes = _statistics.TotalDeviceResumes,
                TotalFolderPauses = _statistics.TotalFolderPauses,
                TotalFolderResumes = _statistics.TotalFolderResumes,
                TotalGracefulWaits = _statistics.TotalGracefulWaits
            }
        };
    }

    /// <summary>
    /// Wait for in-progress operations to complete gracefully.
    /// </summary>
    private async Task<int> WaitForGracefulPauseAsync(string id, CancellationToken cancellationToken)
    {
        var relevantCallbacks = _pauseCallbacks
            .Where(kv => kv.Key.Contains(id))
            .ToList();

        if (relevantCallbacks.Count == 0)
            return 0;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_gracefulPauseTimeout);

        var tasks = relevantCallbacks.Select(kv =>
        {
            try
            {
                return kv.Value(timeoutCts.Token);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in pause callback {Id}", kv.Key);
                return Task.CompletedTask;
            }
        });

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Graceful pause timeout for {Id}", id);
        }

        return relevantCallbacks.Count;
    }
}
