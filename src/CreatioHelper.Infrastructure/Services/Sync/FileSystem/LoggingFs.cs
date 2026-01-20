using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// Provides a logging wrapper for file system operations.
/// </summary>
/// <remarks>
/// Mirrors the functionality of logfs.go in Syncthing:
/// - Logs all file system operations for debugging
/// - Configurable log level
/// - Performance timing for operations
/// - Operation result tracking
///
/// Useful for:
/// - Debugging file sync issues
/// - Performance analysis
/// - Audit logging
/// - Troubleshooting permission issues
/// </remarks>
public interface ILoggingFileSystem : IDisposable
{
    /// <summary>
    /// Gets the underlying file system (if wrapping another).
    /// </summary>
    ICaseInsensitiveFileSystem? Underlying { get; }

    /// <summary>
    /// Gets or sets the minimum log level for file operations.
    /// </summary>
    LogLevel MinimumLogLevel { get; set; }

    /// <summary>
    /// Gets or sets whether to include timing information.
    /// </summary>
    bool IncludeTiming { get; set; }

    /// <summary>
    /// Gets operation statistics.
    /// </summary>
    FileSystemOperationStats GetStats();

    /// <summary>
    /// Resets operation statistics.
    /// </summary>
    void ResetStats();

    // File operations
    bool FileExists(string path);
    bool DirectoryExists(string path);
    Stream? OpenRead(string path);
    Stream CreateWrite(string path);
    bool Delete(string path);
    bool Move(string sourcePath, string destPath);
    FileInfo? GetFileInfo(string path);
    IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly);
    IEnumerable<string> EnumerateDirectories(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly);
    void CreateDirectory(string path);
    byte[] ReadAllBytes(string path);
    void WriteAllBytes(string path, byte[] bytes);
    string ReadAllText(string path);
    void WriteAllText(string path, string content);
}

/// <summary>
/// Statistics for file system operations.
/// </summary>
public class FileSystemOperationStats
{
    public long Reads { get; set; }
    public long Writes { get; set; }
    public long Deletes { get; set; }
    public long Moves { get; set; }
    public long FileExistsChecks { get; set; }
    public long DirectoryExistsChecks { get; set; }
    public long Enumerations { get; set; }
    public long Errors { get; set; }
    public long TotalBytesRead { get; set; }
    public long TotalBytesWritten { get; set; }
    public TimeSpan TotalReadTime { get; set; }
    public TimeSpan TotalWriteTime { get; set; }

    public override string ToString()
    {
        return $"Reads: {Reads}, Writes: {Writes}, Deletes: {Deletes}, Moves: {Moves}, " +
               $"Exists: {FileExistsChecks + DirectoryExistsChecks}, Enums: {Enumerations}, " +
               $"Errors: {Errors}, BytesRead: {TotalBytesRead}, BytesWritten: {TotalBytesWritten}";
    }
}

/// <summary>
/// Logging file system wrapper for debugging and auditing.
/// </summary>
public class LoggingFs : ILoggingFileSystem
{
    private readonly ILogger _logger;
    private readonly ICaseInsensitiveFileSystem? _underlying;
    private readonly string _basePath;
    private readonly object _statsLock = new();
    private FileSystemOperationStats _stats = new();

    public ICaseInsensitiveFileSystem? Underlying => _underlying;
    public LogLevel MinimumLogLevel { get; set; } = LogLevel.Debug;
    public bool IncludeTiming { get; set; } = true;

    /// <summary>
    /// Creates a logging file system that wraps another file system.
    /// </summary>
    public LoggingFs(ILogger logger, ICaseInsensitiveFileSystem underlying)
    {
        _logger = logger;
        _underlying = underlying;
        _basePath = underlying.BasePath;
    }

    /// <summary>
    /// Creates a standalone logging file system for a directory.
    /// </summary>
    public LoggingFs(ILogger logger, string basePath)
    {
        _logger = logger;
        _basePath = Path.GetFullPath(basePath);
        _underlying = null;
    }

    public FileSystemOperationStats GetStats()
    {
        lock (_statsLock)
        {
            return new FileSystemOperationStats
            {
                Reads = _stats.Reads,
                Writes = _stats.Writes,
                Deletes = _stats.Deletes,
                Moves = _stats.Moves,
                FileExistsChecks = _stats.FileExistsChecks,
                DirectoryExistsChecks = _stats.DirectoryExistsChecks,
                Enumerations = _stats.Enumerations,
                Errors = _stats.Errors,
                TotalBytesRead = _stats.TotalBytesRead,
                TotalBytesWritten = _stats.TotalBytesWritten,
                TotalReadTime = _stats.TotalReadTime,
                TotalWriteTime = _stats.TotalWriteTime
            };
        }
    }

    public void ResetStats()
    {
        lock (_statsLock)
        {
            _stats = new FileSystemOperationStats();
        }
    }

    private void Log(string operation, string path, bool success = true, string? details = null, TimeSpan? elapsed = null)
    {
        if (!_logger.IsEnabled(MinimumLogLevel))
            return;

        var message = IncludeTiming && elapsed.HasValue
            ? $"FS.{operation}: {path} [{(success ? "OK" : "FAIL")}] ({elapsed.Value.TotalMilliseconds:F2}ms)"
            : $"FS.{operation}: {path} [{(success ? "OK" : "FAIL")}]";

        if (!string.IsNullOrEmpty(details))
        {
            message += $" - {details}";
        }

        _logger.Log(MinimumLogLevel, message);
    }

    private void IncrementStat(Action<FileSystemOperationStats> action)
    {
        lock (_statsLock)
        {
            action(_stats);
        }
    }

    public bool FileExists(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool result;

        try
        {
            result = _underlying != null
                ? _underlying.FileExists(path)
                : File.Exists(GetFullPath(path));

            IncrementStat(s => s.FileExistsChecks++);
            Log("FileExists", path, result, result ? "found" : "not found", sw.Elapsed);
            return result;
        }
        catch (Exception ex)
        {
            IncrementStat(s => { s.FileExistsChecks++; s.Errors++; });
            Log("FileExists", path, false, ex.Message, sw.Elapsed);
            return false;
        }
    }

    public bool DirectoryExists(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool result;

        try
        {
            result = _underlying != null
                ? _underlying.DirectoryExists(path)
                : Directory.Exists(GetFullPath(path));

            IncrementStat(s => s.DirectoryExistsChecks++);
            Log("DirectoryExists", path, result, result ? "found" : "not found", sw.Elapsed);
            return result;
        }
        catch (Exception ex)
        {
            IncrementStat(s => { s.DirectoryExistsChecks++; s.Errors++; });
            Log("DirectoryExists", path, false, ex.Message, sw.Elapsed);
            return false;
        }
    }

    public Stream? OpenRead(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            Stream? stream;

            if (_underlying != null)
            {
                stream = _underlying.OpenRead(path);
            }
            else
            {
                var fullPath = GetFullPath(path);
                stream = File.Exists(fullPath) ? File.OpenRead(fullPath) : null;
            }

            // Always increment Reads to track the attempt
            IncrementStat(s => s.Reads++);

            if (stream != null)
            {
                Log("OpenRead", path, true, $"length={stream.Length}", sw.Elapsed);
                // Wrap stream to track bytes read
                return new LoggingStream(stream, this, path, isRead: true);
            }
            else
            {
                Log("OpenRead", path, false, "file not found", sw.Elapsed);
                return null;
            }
        }
        catch (Exception ex)
        {
            IncrementStat(s => { s.Reads++; s.Errors++; });
            Log("OpenRead", path, false, ex.Message, sw.Elapsed);
            return null;
        }
    }

    public Stream CreateWrite(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            Stream stream;

            if (_underlying != null)
            {
                stream = _underlying.CreateWrite(path);
            }
            else
            {
                var fullPath = GetFullPath(path);
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                stream = File.Create(fullPath);
            }

            IncrementStat(s => s.Writes++);
            Log("CreateWrite", path, true, null, sw.Elapsed);
            return new LoggingStream(stream, this, path, isRead: false);
        }
        catch (Exception ex)
        {
            IncrementStat(s => { s.Writes++; s.Errors++; });
            Log("CreateWrite", path, false, ex.Message, sw.Elapsed);
            throw;
        }
    }

    public bool Delete(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            bool result;

            if (_underlying != null)
            {
                result = _underlying.Delete(path);
            }
            else
            {
                var fullPath = GetFullPath(path);
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                    result = true;
                }
                else if (Directory.Exists(fullPath))
                {
                    Directory.Delete(fullPath, recursive: true);
                    result = true;
                }
                else
                {
                    result = false;
                }
            }

            IncrementStat(s => s.Deletes++);
            Log("Delete", path, result, null, sw.Elapsed);
            return result;
        }
        catch (Exception ex)
        {
            IncrementStat(s => { s.Deletes++; s.Errors++; });
            Log("Delete", path, false, ex.Message, sw.Elapsed);
            return false;
        }
    }

    public bool Move(string sourcePath, string destPath)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            bool result;

            if (_underlying != null)
            {
                result = _underlying.Move(sourcePath, destPath);
            }
            else
            {
                var fullSourcePath = GetFullPath(sourcePath);
                var fullDestPath = GetFullPath(destPath);

                var destDirectory = Path.GetDirectoryName(fullDestPath);
                if (!string.IsNullOrEmpty(destDirectory) && !Directory.Exists(destDirectory))
                {
                    Directory.CreateDirectory(destDirectory);
                }

                if (File.Exists(fullSourcePath))
                {
                    File.Move(fullSourcePath, fullDestPath, overwrite: true);
                    result = true;
                }
                else if (Directory.Exists(fullSourcePath))
                {
                    Directory.Move(fullSourcePath, fullDestPath);
                    result = true;
                }
                else
                {
                    result = false;
                }
            }

            IncrementStat(s => s.Moves++);
            Log("Move", $"{sourcePath} -> {destPath}", result, null, sw.Elapsed);
            return result;
        }
        catch (Exception ex)
        {
            IncrementStat(s => { s.Moves++; s.Errors++; });
            Log("Move", $"{sourcePath} -> {destPath}", false, ex.Message, sw.Elapsed);
            return false;
        }
    }

    public FileInfo? GetFileInfo(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            FileInfo? info;

            if (_underlying != null)
            {
                info = _underlying.GetFileInfo(path);
            }
            else
            {
                var fullPath = GetFullPath(path);
                info = File.Exists(fullPath) ? new FileInfo(fullPath) : null;
            }

            Log("GetFileInfo", path, info != null, info != null ? $"size={info.Length}" : "not found", sw.Elapsed);
            return info;
        }
        catch (Exception ex)
        {
            IncrementStat(s => s.Errors++);
            Log("GetFileInfo", path, false, ex.Message, sw.Elapsed);
            return null;
        }
    }

    public IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var count = 0;

        try
        {
            IEnumerable<string> files;

            if (_underlying != null)
            {
                files = _underlying.EnumerateFiles(path, searchPattern, searchOption);
            }
            else
            {
                var fullPath = GetFullPath(path);
                if (!Directory.Exists(fullPath))
                {
                    Log("EnumerateFiles", path, false, "directory not found", sw.Elapsed);
                    yield break;
                }
                files = Directory.EnumerateFiles(fullPath, searchPattern, searchOption)
                    .Select(f => Path.GetRelativePath(_basePath, f));
            }

            foreach (var file in files)
            {
                count++;
                yield return file;
            }

            IncrementStat(s => s.Enumerations++);
            Log("EnumerateFiles", path, true, $"count={count}, pattern={searchPattern}", sw.Elapsed);
        }
        finally
        {
            // Logging already done in try block
        }
    }

    public IEnumerable<string> EnumerateDirectories(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var count = 0;

        try
        {
            IEnumerable<string> directories;

            if (_underlying != null)
            {
                directories = _underlying.EnumerateDirectories(path, searchPattern, searchOption);
            }
            else
            {
                var fullPath = GetFullPath(path);
                if (!Directory.Exists(fullPath))
                {
                    Log("EnumerateDirectories", path, false, "directory not found", sw.Elapsed);
                    yield break;
                }
                directories = Directory.EnumerateDirectories(fullPath, searchPattern, searchOption)
                    .Select(d => Path.GetRelativePath(_basePath, d));
            }

            foreach (var directory in directories)
            {
                count++;
                yield return directory;
            }

            IncrementStat(s => s.Enumerations++);
            Log("EnumerateDirectories", path, true, $"count={count}, pattern={searchPattern}", sw.Elapsed);
        }
        finally
        {
            // Logging already done in try block
        }
    }

    public void CreateDirectory(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var fullPath = GetFullPath(path);
            Directory.CreateDirectory(fullPath);
            Log("CreateDirectory", path, true, null, sw.Elapsed);
        }
        catch (Exception ex)
        {
            IncrementStat(s => s.Errors++);
            Log("CreateDirectory", path, false, ex.Message, sw.Elapsed);
            throw;
        }
    }

    public byte[] ReadAllBytes(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var fullPath = GetFullPath(path);
            var bytes = File.ReadAllBytes(fullPath);
            IncrementStat(s =>
            {
                s.Reads++;
                s.TotalBytesRead += bytes.Length;
                s.TotalReadTime += sw.Elapsed;
            });
            Log("ReadAllBytes", path, true, $"size={bytes.Length}", sw.Elapsed);
            return bytes;
        }
        catch (Exception ex)
        {
            IncrementStat(s => { s.Reads++; s.Errors++; });
            Log("ReadAllBytes", path, false, ex.Message, sw.Elapsed);
            throw;
        }
    }

    public void WriteAllBytes(string path, byte[] bytes)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var fullPath = GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllBytes(fullPath, bytes);
            IncrementStat(s =>
            {
                s.Writes++;
                s.TotalBytesWritten += bytes.Length;
                s.TotalWriteTime += sw.Elapsed;
            });
            Log("WriteAllBytes", path, true, $"size={bytes.Length}", sw.Elapsed);
        }
        catch (Exception ex)
        {
            IncrementStat(s => { s.Writes++; s.Errors++; });
            Log("WriteAllBytes", path, false, ex.Message, sw.Elapsed);
            throw;
        }
    }

    public string ReadAllText(string path)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var fullPath = GetFullPath(path);
            var text = File.ReadAllText(fullPath);
            IncrementStat(s =>
            {
                s.Reads++;
                s.TotalBytesRead += System.Text.Encoding.UTF8.GetByteCount(text);
                s.TotalReadTime += sw.Elapsed;
            });
            Log("ReadAllText", path, true, $"length={text.Length}", sw.Elapsed);
            return text;
        }
        catch (Exception ex)
        {
            IncrementStat(s => { s.Reads++; s.Errors++; });
            Log("ReadAllText", path, false, ex.Message, sw.Elapsed);
            throw;
        }
    }

    public void WriteAllText(string path, string content)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var fullPath = GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(fullPath, content);
            var byteCount = System.Text.Encoding.UTF8.GetByteCount(content);
            IncrementStat(s =>
            {
                s.Writes++;
                s.TotalBytesWritten += byteCount;
                s.TotalWriteTime += sw.Elapsed;
            });
            Log("WriteAllText", path, true, $"length={content.Length}", sw.Elapsed);
        }
        catch (Exception ex)
        {
            IncrementStat(s => { s.Writes++; s.Errors++; });
            Log("WriteAllText", path, false, ex.Message, sw.Elapsed);
            throw;
        }
    }

    private string GetFullPath(string path)
    {
        return Path.Combine(_basePath, path);
    }

    public void Dispose()
    {
        _underlying?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Stream wrapper that tracks bytes read/written.
    /// </summary>
    private class LoggingStream : Stream
    {
        private readonly Stream _inner;
        private readonly LoggingFs _fs;
        private readonly string _path;
        private readonly bool _isRead;
        private long _bytesTransferred;

        public LoggingStream(Stream inner, LoggingFs fs, string path, bool isRead)
        {
            _inner = inner;
            _fs = fs;
            _path = path;
            _isRead = isRead;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            _bytesTransferred += read;
            return read;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            _bytesTransferred += count;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();

                _fs.IncrementStat(s =>
                {
                    if (_isRead)
                        s.TotalBytesRead += _bytesTransferred;
                    else
                        s.TotalBytesWritten += _bytesTransferred;
                });

                _fs.Log(
                    _isRead ? "CloseRead" : "CloseWrite",
                    _path,
                    true,
                    $"bytes={_bytesTransferred}");
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// Factory for creating logging file systems.
/// </summary>
public class LoggingFsFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public LoggingFsFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Creates a logging file system that wraps another file system.
    /// </summary>
    public ILoggingFileSystem Create(ICaseInsensitiveFileSystem underlying)
    {
        var logger = _loggerFactory.CreateLogger<LoggingFs>();
        return new LoggingFs(logger, underlying);
    }

    /// <summary>
    /// Creates a standalone logging file system for a directory.
    /// </summary>
    public ILoggingFileSystem Create(string basePath)
    {
        var logger = _loggerFactory.CreateLogger<LoggingFs>();
        return new LoggingFs(logger, basePath);
    }
}
