using System.Collections.Concurrent;
using System.Security.Cryptography;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Entities.Events;
using Microsoft.Extensions.Logging;
using EventType = CreatioHelper.Domain.Entities.Events.SyncEventType;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// File system watcher and change detection (based on Syncthing fs watcher)
/// Inspired by Syncthing's lib/fs and lib/scanner packages
/// </summary>
public class FileWatcher : IDisposable
{
    private readonly ILogger<FileWatcher> _logger;
    private readonly AdaptiveBlockSizer _blockSizer;
    private readonly IEventLogger? _eventLogger;
    private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new();
    private readonly ConcurrentDictionary<string, Timer> _scanTimers = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastScans = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, SyncFileInfo>> _folderFiles = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public event EventHandler<FileChangedEventArgs>? FileChanged;
    public event EventHandler<FolderScanCompletedEventArgs>? FolderScanCompleted;

    public FileWatcher(ILogger<FileWatcher> logger, AdaptiveBlockSizer blockSizer, IEventLogger? eventLogger = null)
    {
        _logger = logger;
        _blockSizer = blockSizer;
        _eventLogger = eventLogger;
    }

    public void WatchFolder(SyncFolder folder)
    {
        if (_watchers.ContainsKey(folder.Id))
        {
            return; // Already watching
        }

        try
        {
            var watcher = new FileSystemWatcher(folder.Path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | 
                              NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.CreationTime
            };

            watcher.Created += (sender, e) => OnFileSystemEvent(folder.Id, e.FullPath, FileChangeType.Created);
            watcher.Changed += (sender, e) => OnFileSystemEvent(folder.Id, e.FullPath, FileChangeType.Modified);
            watcher.Deleted += (sender, e) => OnFileSystemEvent(folder.Id, e.FullPath, FileChangeType.Deleted);
            watcher.Renamed += (sender, e) => OnFileSystemEvent(folder.Id, e.FullPath, FileChangeType.Renamed, e.OldFullPath);

            watcher.EnableRaisingEvents = folder.FSWatcherEnabled;
            _watchers[folder.Id] = watcher;

            // Set up periodic scan timer
            if (folder.RescanIntervalS > 0)
            {
                var timer = new Timer(
                    _ => _ = ScanFolderAsync(folder),
                    null,
                    TimeSpan.FromSeconds(folder.RescanIntervalS),
                    TimeSpan.FromSeconds(folder.RescanIntervalS)
                );
                _scanTimers[folder.Id] = timer;
            }

            _logger.LogInformation("Started watching folder {FolderId} at {Path}", folder.Id, folder.Path);
            
            // Log folder watch start event
            _eventLogger?.LogEvent(EventType.FolderWatchStateChanged, 
                new { FolderId = folder.Id, Path = folder.Path, Watching = true }, 
                $"Started watching folder {folder.Id}", 
                null, folder.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start watching folder {FolderId}", folder.Id);
        }
    }

    public void StopWatchingFolder(string folderId)
    {
        if (_watchers.TryRemove(folderId, out var watcher))
        {
            watcher.Dispose();
        }

        if (_scanTimers.TryRemove(folderId, out var timer))
        {
            timer.Dispose();
        }

        _logger.LogInformation("Stopped watching folder {FolderId}", folderId);
        
        // Log folder watch stop event
        _eventLogger?.LogEvent(EventType.FolderWatchStateChanged, 
            new { FolderId = folderId, Watching = false }, 
            $"Stopped watching folder {folderId}", 
            null, folderId);
    }

    public async Task<List<SyncFileInfo>> ScanFolderAsync(SyncFolder folder)
    {
        var files = new List<SyncFileInfo>();
        
        try
        {
            _logger.LogDebug("Starting scan of folder {FolderId}", folder.Id);
            var startTime = DateTime.UtcNow;

            if (!Directory.Exists(folder.Path))
            {
                _logger.LogWarning("Folder path does not exist: {Path}", folder.Path);
                return files;
            }

            var currentFiles = await ScanDirectoryRecursiveAsync(folder.Id, folder.Path, folder.Path, new List<string>());
            
            // Get or create file tracking for this folder
            var folderFileMap = _folderFiles.GetOrAdd(folder.Id, _ => new ConcurrentDictionary<string, SyncFileInfo>());
            
            // Update current files in tracking map
            var currentFilePaths = new HashSet<string>();
            foreach (var file in currentFiles)
            {
                currentFilePaths.Add(file.RelativePath);
                folderFileMap.AddOrUpdate(file.RelativePath, file, (_, _) => file);
            }
            
            // Mark files that are no longer present as deleted (but keep them for Index messages)
            foreach (var trackedFile in folderFileMap.Values.ToList())
            {
                if (!currentFilePaths.Contains(trackedFile.RelativePath) && !trackedFile.IsDeleted)
                {
                    _logger.LogDebug("File no longer exists, marking as deleted: {FileName}", trackedFile.RelativePath);
                    trackedFile.MarkAsDeleted();
                    // Update vector clock for deletion
                    trackedFile.Vector.Increment(Environment.MachineName); // Simple device ID for now
                }
            }
            
            // Return all files (including deleted ones for Index messages)
            files = folderFileMap.Values.ToList();
            
            _lastScans[folder.Id] = DateTime.UtcNow;
            var duration = DateTime.UtcNow - startTime;

            _logger.LogInformation("Completed scan of folder {FolderId}: {FileCount} total files ({ActiveCount} active, {DeletedCount} deleted) in {Duration}ms", 
                folder.Id, files.Count, files.Count(f => !f.IsDeleted), files.Count(f => f.IsDeleted), duration.TotalMilliseconds);

            // Log folder scan completed event
            _eventLogger?.LogEvent(EventType.FolderScanProgress, 
                new { 
                    FolderId = folder.Id, 
                    TotalFiles = files.Count, 
                    ActiveFiles = files.Count(f => !f.IsDeleted), 
                    DeletedFiles = files.Count(f => f.IsDeleted), 
                    DurationMs = duration.TotalMilliseconds 
                }, 
                $"Folder scan completed for {folder.Id}: {files.Count} files in {duration.TotalMilliseconds:F0}ms", 
                null, folder.Id);
            
            FolderScanCompleted?.Invoke(this, new FolderScanCompletedEventArgs(folder.Id, files, duration));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning folder {FolderId}", folder.Id);
            
            // Log folder scan error event
            _eventLogger?.LogEvent(EventType.FolderErrors, 
                new { FolderId = folder.Id, Error = ex.Message }, 
                $"Error scanning folder {folder.Id}: {ex.Message}", 
                null, folder.Id);
        }

        return files;
    }

    private async Task<List<SyncFileInfo>> ScanDirectoryRecursiveAsync(
        string folderId, 
        string directoryPath, 
        string basePath, 
        List<string> ignorePatterns)
    {
        var files = new List<SyncFileInfo>();

        try
        {
            // Scan files in current directory
            foreach (var filePath in Directory.GetFiles(directoryPath))
            {
                if (ShouldIgnoreFile(filePath, basePath, ignorePatterns))
                    continue;

                var fileInfo = await CreateFileInfoAsync(folderId, filePath, basePath);
                if (fileInfo != null)
                {
                    files.Add(fileInfo);
                }
            }

            // Scan subdirectories
            foreach (var subdirectoryPath in Directory.GetDirectories(directoryPath))
            {
                if (ShouldIgnoreFile(subdirectoryPath, basePath, ignorePatterns))
                    continue;

                var subdirectoryFiles = await ScanDirectoryRecursiveAsync(folderId, subdirectoryPath, basePath, ignorePatterns);
                files.AddRange(subdirectoryFiles);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning("Access denied to directory {Directory}: {Error}", directoryPath, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning directory {Directory}", directoryPath);
        }

        return files;
    }

    private async Task<SyncFileInfo?> CreateFileInfoAsync(string folderId, string filePath, string basePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists) return null;

            var relativePath = Path.GetRelativePath(basePath, filePath);
            // Convert Windows path separators to forward slashes for cross-platform compatibility
            relativePath = relativePath.Replace('\\', '/');
            var syncFileInfo = new SyncFileInfo(folderId, relativePath, relativePath, fileInfo.Length, fileInfo.LastWriteTimeUtc);

            // Calculate file hash
            var hash = await CalculateFileHashAsync(filePath);
            syncFileInfo.UpdateHash(hash);

            // Create blocks for the file using adaptive block sizing
            var blocks = await CreateBlocksAsync(filePath, fileInfo.Length);
            syncFileInfo.SetBlocks(blocks);

            return syncFileInfo;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating file info for {FilePath}", filePath);
            return null;
        }
    }

    private async Task<string> CalculateFileHashAsync(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream);
        return Convert.ToHexString(hash).ToLower();
    }

    private async Task<List<BlockInfo>> CreateBlocksAsync(string filePath, long fileSize)
    {
        var blocks = new List<BlockInfo>();

        try
        {
            // Calculate optimal block size using Syncthing's adaptive algorithm
            int blockSize = _blockSizer.CalculateBlockSize(fileSize, useLargeBlocks: true);
            
            _logger.LogDebug("Creating blocks for {FilePath} ({FileSize} bytes) with block size {BlockSize}", 
                filePath, fileSize, AdaptiveBlockSizer.FormatBlockSize(blockSize));

            using var stream = File.OpenRead(filePath);
            var buffer = new byte[blockSize];
            long offset = 0;

            while (offset < stream.Length)
            {
                var bytesToRead = (int)Math.Min(blockSize, stream.Length - offset);
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(0, bytesToRead));

                if (bytesRead > 0)
                {
                    // Calculate both weak and strong hashes for the block
                    var (weakHash, strongHash) = WeakHashCalculator.CalculateBlockHashes(buffer, 0, bytesRead);

                    blocks.Add(new BlockInfo(offset, bytesRead, strongHash, weakHash));
                    offset += bytesRead;
                }
            }

            _logger.LogTrace("Created {BlockCount} blocks for {FilePath} with {BlockSize} block size", 
                blocks.Count, filePath, AdaptiveBlockSizer.FormatBlockSize(blockSize));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating blocks for file {FilePath}", filePath);
        }

        return blocks;
    }

    private bool ShouldIgnoreFile(string filePath, string basePath, List<string> ignorePatterns)
    {
        var relativePath = Path.GetRelativePath(basePath, filePath);
        var fileName = Path.GetFileName(filePath);

        // Check against ignore patterns (simplified glob matching)
        foreach (var pattern in ignorePatterns)
        {
            if (IsPatternMatch(fileName, pattern) || IsPatternMatch(relativePath, pattern))
            {
                return true;
            }
        }

        // Ignore common temp files and hidden files
        if (fileName.StartsWith(".") || fileName.StartsWith("~") || fileName.EndsWith(".tmp"))
        {
            return true;
        }

        return false;
    }

    private bool IsPatternMatch(string text, string pattern)
    {
        // Simplified glob pattern matching
        if (pattern.Contains('*'))
        {
            var regexPattern = pattern.Replace("*", ".*").Replace("?", ".");
            return System.Text.RegularExpressions.Regex.IsMatch(text, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        return string.Equals(text, pattern, StringComparison.OrdinalIgnoreCase);
    }

    private void OnFileSystemEvent(string folderId, string filePath, FileChangeType changeType, string? oldPath = null)
    {
        try
        {
            // Debounce rapid file system events
            _ = Task.Delay(100).ContinueWith(_ =>
            {
                try
                {
                    if (changeType == FileChangeType.Deleted)
                    {
                        // Handle deletion: mark file as deleted in tracking map
                        var folderFileMap = _folderFiles.GetOrAdd(folderId, _ => new ConcurrentDictionary<string, SyncFileInfo>());
                        
                        // Try to find the file in our tracking map by path
                        var deletedFile = folderFileMap.Values.FirstOrDefault(f => f.RelativePath.EndsWith(Path.GetRelativePath(Path.GetDirectoryName(filePath) ?? "", filePath), StringComparison.OrdinalIgnoreCase));
                        
                        if (deletedFile != null && !deletedFile.IsDeleted)
                        {
                            _logger.LogInformation("🗑️ File deleted: {FileName} in folder {FolderId}", deletedFile.RelativePath, folderId);
                        
                        // Log file deletion event
                        _eventLogger?.LogEvent(EventType.LocalChangeDetected, 
                            new { Action = "Deleted", Size = deletedFile.Size }, 
                            $"File deleted: {deletedFile.RelativePath}", 
                            null, folderId, deletedFile.RelativePath);
                            deletedFile.MarkAsDeleted();
                            deletedFile.Vector.Increment(Environment.MachineName); // Update vector clock for deletion
                        }
                    }
                    else if (File.Exists(filePath))
                    {
                        // Handle creation/modification: file will be picked up in next scan
                        _logger.LogDebug("File {ChangeType}: {FilePath} in folder {FolderId}", changeType, filePath, folderId);
                        
                        // Log file change event
                        var eventType = changeType switch
                        {
                            FileChangeType.Created => EventType.LocalChangeDetected,
                            FileChangeType.Modified => EventType.LocalChangeDetected,
                            FileChangeType.Renamed => EventType.LocalChangeDetected,
                            _ => EventType.LocalChangeDetected
                        };
                        
                        // Get file size if file exists for creation/modification events
                        long fileSize = 0;
                        if (changeType != FileChangeType.Deleted && File.Exists(filePath))
                        {
                            try
                            {
                                fileSize = new FileInfo(filePath).Length;
                            }
                            catch
                            {
                                fileSize = 0; // Ignore errors getting file size
                            }
                        }
                        
                        _eventLogger?.LogEvent(eventType, 
                            new { Action = changeType.ToString(), Size = fileSize }, 
                            $"File {changeType.ToString().ToLower()}: {Path.GetFileName(filePath)}", 
                            null, folderId, filePath);
                    }
                    
                    var eventArgs = new FileChangedEventArgs(folderId, filePath, changeType, oldPath, 0);
                    FileChanged?.Invoke(this, eventArgs);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in debounced file system event handler for {FilePath}", filePath);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing file system event for {FilePath}", filePath);
            
            // Log file system event error
            _eventLogger?.LogEvent(EventType.Failure, 
                new { FilePath = filePath, Error = ex.Message }, 
                $"Error processing file system event: {ex.Message}", 
                null, null, filePath);
        }
    }

    public void Dispose()
    {
        _cancellationTokenSource.Cancel();

        foreach (var watcher in _watchers.Values)
        {
            watcher.Dispose();
        }

        foreach (var timer in _scanTimers.Values)
        {
            timer.Dispose();
        }

        _cancellationTokenSource.Dispose();
    }
}

public class FileChangedEventArgs : EventArgs
{
    public string FolderId { get; }
    public string FilePath { get; }
    public FileChangeType ChangeType { get; }
    public string? OldPath { get; }
    public long FileSize { get; }

    public FileChangedEventArgs(string folderId, string filePath, FileChangeType changeType, string? oldPath = null, long fileSize = 0)
    {
        FolderId = folderId;
        FilePath = filePath;
        ChangeType = changeType;
        OldPath = oldPath;
        FileSize = fileSize;
    }
}

public class FolderScanCompletedEventArgs : EventArgs
{
    public string FolderId { get; }
    public List<SyncFileInfo> Files { get; }
    public TimeSpan Duration { get; }

    public FolderScanCompletedEventArgs(string folderId, List<SyncFileInfo> files, TimeSpan duration)
    {
        FolderId = folderId;
        Files = files;
        Duration = duration;
    }
}

public enum FileChangeType
{
    Created,
    Modified,
    Deleted,
    Renamed
}