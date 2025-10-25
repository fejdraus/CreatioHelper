using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;
using OldFolderStatistics = CreatioHelper.Domain.Entities.FolderStatistics;
using OldLastFileInfo = CreatioHelper.Domain.Entities.LastFileInfo;
using NewFolderStatistics = CreatioHelper.Domain.Entities.Statistics.FolderStatistics;
using LastFile = CreatioHelper.Domain.Entities.Statistics.LastFile;

namespace CreatioHelper.Infrastructure.Services.Statistics;

/// <summary>
/// Ссылка на статистику папки (на основе Syncthing FolderStatisticsReference)
/// </summary>
public class FolderStatisticsReference : IFolderStatisticsReference, IDisposable
{
    private readonly string _folderId;
    private readonly ISyncDatabase _database;
    private readonly ILogger _logger;
    
    private readonly object _statisticsLock = new();
    private OldFolderStatistics _statistics;
    
    // Время-скользящие окна для вычисления скорости
    private readonly Queue<(DateTime Time, int FilesProcessed)> _scanRateSamples = new();
    private readonly TimeSpan _rateCalculationWindow = TimeSpan.FromMinutes(5);
    
    private DateTime _lastSyncStartTime;
    private bool _disposed = false;
    
    public FolderStatisticsReference(string folderId, ISyncDatabase database, ILogger logger)
    {
        _folderId = folderId;
        _database = database;
        _logger = logger;
        
        _statistics = new OldFolderStatistics();
        
        // Загружаем существующую статистику из базы данных
        _ = Task.Run(async () => await LoadStatisticsAsync(CancellationToken.None));
    }
    
    public Task<LastFile> GetLastFileAsync(CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            return Task.FromResult(new LastFile
            {
                At = _statistics.LastFile.At,
                Filename = _statistics.LastFile.FileName,
                Deleted = _statistics.LastFile.Deleted
            });
        }
    }
    
    public async Task ReceivedFileAsync(string fileName, bool deleted, long size, string action, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        
        lock (_statisticsLock)
        {
            _statistics.LastFile = new OldLastFileInfo
            {
                At = now,
                FileName = fileName,
                Deleted = deleted,
                Size = size,
                Action = action
            };
            
            // Обновляем счетчики в зависимости от типа операции
            if (deleted)
            {
                if (_statistics.TotalFiles > 0)
                    _statistics.TotalFiles--;
                if (_statistics.TotalSize >= size)
                    _statistics.TotalSize -= size;
            }
            else if (action == "Created")
            {
                _statistics.TotalFiles++;
                _statistics.TotalSize += size;
            }
            
            // Добавляем образец для расчета скорости обработки файлов
            _scanRateSamples.Enqueue((now, 1));
            
            // Удаляем старые образцы
            while (_scanRateSamples.Count > 0 && (now - _scanRateSamples.Peek().Time) > _rateCalculationWindow)
            {
                _scanRateSamples.Dequeue();
            }
            
            // Вычисляем скорость обработки
            if (_scanRateSamples.Count >= 2)
            {
                var timeSpan = _scanRateSamples.Last().Time - _scanRateSamples.First().Time;
                if (timeSpan.TotalSeconds > 0)
                {
                    _statistics.SyncRate = _scanRateSamples.Count / timeSpan.TotalSeconds;
                }
            }
        }
        
        await SaveStatisticsAsync(cancellationToken);
        _logger.LogTrace("Folder {FolderId} received file: {FileName} ({Size} bytes, {Action})", _folderId, fileName, size, action);
    }
    
    public async Task ScanCompletedAsync(CancellationToken cancellationToken = default)
    {
        await ScanCompletedAsync(0, 0, cancellationToken);
    }
    
    public async Task ScanCompletedAsync(long totalFiles, long totalSize, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        
        lock (_statisticsLock)
        {
            _statistics.LastScan = now;
            
            if (totalFiles > 0)
            {
                _statistics.TotalFiles = totalFiles;
                _statistics.LocalFiles = totalFiles; // При сканировании считаем все файлы локальными
            }
            
            if (totalSize > 0)
            {
                _statistics.TotalSize = totalSize;
                _statistics.LocalSize = totalSize;
            }
        }
        
        await SaveStatisticsAsync(cancellationToken);
        _logger.LogTrace("Folder {FolderId} scan completed: {TotalFiles} files, {TotalSize} bytes", _folderId, totalFiles, totalSize);
    }
    
    public Task<DateTime> GetLastScanTimeAsync(CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            return Task.FromResult(_statistics.LastScan);
        }
    }
    
    public Task<NewFolderStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            return Task.FromResult(new NewFolderStatistics
            {
                FolderId = _folderId,
                LastFile = new LastFile
                {
                    At = _statistics.LastFile.At,
                    Filename = _statistics.LastFile.FileName,
                    Deleted = _statistics.LastFile.Deleted
                },
                LastScan = _statistics.LastScan,
                TotalFiles = _statistics.TotalFiles,
                TotalSize = _statistics.TotalSize,
                FilesToSync = _statistics.PendingFiles,
                BytesToSync = _statistics.PendingSize,
                ConflictedFiles = _statistics.Conflicts,
                ErroredFiles = _statistics.Errors,
                SyncProgress = _statistics.CompletionPercentage
            });
        }
    }
    
    /// <summary>
    /// Обновить статистику синхронизации
    /// </summary>
    public async Task UpdateSyncStatisticsAsync(long localFiles, long localSize, long remoteFiles, long remoteSize, 
        long pendingFiles, long pendingSize, double completionPercentage, CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            _statistics.LocalFiles = localFiles;
            _statistics.LocalSize = localSize;
            _statistics.RemoteFiles = remoteFiles;
            _statistics.RemoteSize = remoteSize;
            _statistics.PendingFiles = pendingFiles;
            _statistics.PendingSize = pendingSize;
            _statistics.CompletionPercentage = Math.Max(0, Math.Min(100, completionPercentage));
            
            _statistics.TotalFiles = Math.Max(localFiles, remoteFiles);
            _statistics.TotalSize = Math.Max(localSize, remoteSize);
        }
        
        await SaveStatisticsAsync(cancellationToken);
        _logger.LogTrace("Folder {FolderId} sync statistics updated: {CompletionPercentage:F1}% complete, {PendingFiles} pending", 
            _folderId, completionPercentage, pendingFiles);
    }
    
    /// <summary>
    /// Отметить начало синхронизации
    /// </summary>
    public async Task SyncStartedAsync(CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            _lastSyncStartTime = DateTime.UtcNow;
        }
        
        await Task.CompletedTask;
        _logger.LogTrace("Folder {FolderId} sync started", _folderId);
    }
    
    /// <summary>
    /// Отметить завершение синхронизации
    /// </summary>
    public async Task SyncCompletedAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        
        lock (_statisticsLock)
        {
            _statistics.LastSync = now;
            _statistics.PendingFiles = 0;
            _statistics.PendingSize = 0;
            _statistics.CompletionPercentage = 100.0;
        }
        
        await SaveStatisticsAsync(cancellationToken);
        _logger.LogTrace("Folder {FolderId} sync completed", _folderId);
    }
    
    /// <summary>
    /// Записать конфликт
    /// </summary>
    public async Task RecordConflictAsync(string fileName, CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            _statistics.Conflicts++;
        }
        
        await SaveStatisticsAsync(cancellationToken);
        _logger.LogTrace("Folder {FolderId} recorded conflict for file: {FileName}", _folderId, fileName);
    }
    
    /// <summary>
    /// Записать ошибку
    /// </summary>
    public async Task RecordErrorAsync(string fileName, string error, CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            _statistics.Errors++;
        }
        
        await SaveStatisticsAsync(cancellationToken);
        _logger.LogTrace("Folder {FolderId} recorded error for file {FileName}: {Error}", _folderId, fileName, error);
    }
    
    /// <summary>
    /// Обновить процент завершенности
    /// </summary>
    public async Task UpdateCompletionPercentageAsync(double percentage, CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            _statistics.CompletionPercentage = Math.Max(0, Math.Min(100, percentage));
        }
        
        await SaveStatisticsAsync(cancellationToken);
    }
    
    /// <summary>
    /// Сбросить статистику ошибок и конфликтов
    /// </summary>
    public async Task ClearErrorsAndConflictsAsync(CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            _statistics.Errors = 0;
            _statistics.Conflicts = 0;
        }
        
        await SaveStatisticsAsync(cancellationToken);
        _logger.LogTrace("Cleared errors and conflicts for folder {FolderId}", _folderId);
    }
    
    /// <summary>
    /// Сбросить всю статистику
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        lock (_statisticsLock)
        {
            _statistics = new OldFolderStatistics();
            _scanRateSamples.Clear();
        }
        
        await SaveStatisticsAsync(cancellationToken);
        _logger.LogDebug("Reset statistics for folder {FolderId}", _folderId);
    }
    
    private async Task LoadStatisticsAsync(CancellationToken cancellationToken)
    {
        try
        {
            // В реальной реализации здесь будет загрузка из базы данных
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading statistics for folder {FolderId}: {Message}", _folderId, ex.Message);
        }
    }
    
    private async Task SaveStatisticsAsync(CancellationToken cancellationToken)
    {
        try
        {
            // В реальной реализации здесь будет сохранение в базу данных
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error saving statistics for folder {FolderId}: {Message}", _folderId, ex.Message);
        }
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        
        // Сохраняем статистику при освобождении ресурсов
        _ = Task.Run(async () => await SaveStatisticsAsync(CancellationToken.None));
        
        _logger.LogTrace("Disposed FolderStatisticsReference for {FolderId}", _folderId);
    }
}