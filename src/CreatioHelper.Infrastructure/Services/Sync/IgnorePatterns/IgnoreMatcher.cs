using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.IgnorePatterns;

/// <summary>
/// Main ignore pattern matcher, compatible with Syncthing's Matcher
/// Handles loading .stignore files, pattern matching, and caching
/// </summary>
public class IgnoreMatcher : IDisposable
{
    private readonly ILogger<IgnoreMatcher> _logger;
    private readonly string _basePath;
    private readonly IgnoreCache? _cache;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private List<string> _lines = new();
    private List<IgnorePattern> _patterns = new();
    private string _currentHash = string.Empty;
    private DateTime _lastModified = DateTime.MinValue;
    private char _escapeChar = '\\';
    
    /// <summary>
    /// Gets the current patterns loaded in the matcher
    /// </summary>
    public IReadOnlyList<IgnorePattern> Patterns
    {
        get
        {
            lock (_lock)
            {
                return _patterns.AsReadOnly();
            }
        }
    }
    
    /// <summary>
    /// Gets the lines as read from the .stignore file
    /// </summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lock)
            {
                return _lines.AsReadOnly();
            }
        }
    }

    public IgnoreMatcher(string basePath, ILogger<IgnoreMatcher> logger, bool useCache = true)
    {
        _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cache = useCache ? new IgnoreCache() : null;
    }

    /// <summary>
    /// Loads ignore patterns from .stignore file in the base directory
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var stignoreFile = Path.Combine(_basePath, ".stignore");
        await LoadFromFileAsync(stignoreFile, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads ignore patterns from a specific file
    /// </summary>
    public async Task LoadFromFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                _logger.LogDebug("Ignore file not found: {FilePath}", filePath);
                lock (_lock)
                {
                    ClearPatterns();
                }
                return;
            }

            var fileInfo = new FileInfo(filePath);
            var newModified = fileInfo.LastWriteTimeUtc;
            
            // Check if file has changed since last load
            lock (_lock)
            {
                if (newModified == _lastModified && _patterns.Count > 0)
                {
                    _logger.LogDebug("Ignore file unchanged: {FilePath}", filePath);
                    return;
                }
            }

            var lines = await File.ReadAllLinesAsync(filePath, cancellationToken).ConfigureAwait(false);
            var newHash = ComputeHash(lines);
            
            lock (_lock)
            {
                if (newHash == _currentHash)
                {
                    _logger.LogDebug("Ignore file content unchanged: {FilePath}", filePath);
                    _lastModified = newModified;
                    return;
                }
            }

            await ParseLinesAsync(lines, Path.GetDirectoryName(filePath) ?? _basePath, cancellationToken).ConfigureAwait(false);
            
            lock (_lock)
            {
                _lastModified = newModified;
                _currentHash = newHash;
                _cache?.Clear(); // Clear cache when patterns change
            }
            
            _logger.LogInformation("Loaded {PatternCount} ignore patterns from {FilePath}", 
                _patterns.Count, filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load ignore patterns from {FilePath}", filePath);
            throw;
        }
    }

    /// <summary>
    /// Tests if a path matches any ignore pattern
    /// </summary>
    public IgnoreResult Match(string path)
    {
        if (string.IsNullOrEmpty(path))
            return IgnoreResult.NotIgnored;

        // Normalize path separators
        var normalizedPath = NormalizePath(path);

        // The .stignore file itself is NEVER synced (per Syncthing spec)
        if (IsStignoreFile(normalizedPath))
        {
            return IgnoreResult.IgnoreAndSkip;
        }

        // Check cache first
        if (_cache?.TryGet(normalizedPath, out var cachedResult) == true)
        {
            return cachedResult;
        }

        IgnoreResult result;
        lock (_lock)
        {
            result = MatchInternal(normalizedPath);
        }

        // Cache the result
        _cache?.Set(normalizedPath, result);

        return result;
    }

    /// <summary>
    /// Tests if a directory path should be ignored.
    /// Adds trailing slash to indicate directory context.
    /// </summary>
    public IgnoreResult MatchDirectory(string path)
    {
        if (string.IsNullOrEmpty(path))
            return IgnoreResult.NotIgnored;

        var normalizedPath = NormalizePath(path);

        // Ensure trailing slash for directory context
        if (!normalizedPath.EndsWith('/'))
        {
            normalizedPath += '/';
        }

        return Match(normalizedPath);
    }

    /// <summary>
    /// Tests if a path is an ignore file that should never be synced.
    /// Per Syncthing spec, .stignore, .stglobalignore, and files included via #include are never synced.
    /// </summary>
    public static bool IsStignoreFile(string normalizedPath)
    {
        var filename = normalizedPath.Contains('/')
            ? normalizedPath[(normalizedPath.LastIndexOf('/') + 1)..]
            : normalizedPath;

        return filename.Equals(".stignore", StringComparison.OrdinalIgnoreCase) ||
               filename.Equals(".stglobalignore", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if any parent directory of the path is ignored.
    /// This is useful for determining if a file should be skipped during traversal.
    /// </summary>
    public bool IsAnyParentIgnored(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var normalizedPath = NormalizePath(path);
        var parts = normalizedPath.Split('/');

        var currentPath = string.Empty;
        for (int i = 0; i < parts.Length - 1; i++) // Exclude the file itself
        {
            currentPath = currentPath.Length > 0 ? currentPath + "/" + parts[i] : parts[i];

            var result = MatchDirectory(currentPath);
            if (result.IsIgnored() && result.CanSkipDir())
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Gets cache statistics for monitoring
    /// </summary>
    public CacheStats? GetCacheStats() => _cache?.GetStats();

    private async Task ParseLinesAsync(string[] lines, string baseDirectory, CancellationToken cancellationToken)
    {
        var newLines = new List<string>(lines);
        var newPatterns = new List<IgnorePattern>();
        var parseContext = new ParseContext { EscapeChar = '\\' };
        var processedFiles = new HashSet<string>();

        // Pre-process lines to handle line continuations
        var processedLines = PreprocessLineContinuations(lines, parseContext.EscapeChar);

        await ParseLinesRecursiveAsync(processedLines, baseDirectory, newPatterns, parseContext, processedFiles, cancellationToken).ConfigureAwait(false);

        lock (_lock)
        {
            _lines = newLines;
            _patterns = newPatterns;
            _escapeChar = parseContext.EscapeChar;
        }
    }

    /// <summary>
    /// Preprocesses lines to handle line continuations (backslash at end of line).
    /// Per Syncthing spec, a line ending with the escape character continues on the next line.
    /// </summary>
    private static string[] PreprocessLineContinuations(string[] lines, char escapeChar)
    {
        var result = new List<string>();
        var currentLine = new StringBuilder();

        foreach (var line in lines)
        {
            if (currentLine.Length > 0)
            {
                // Continuing from previous line - trim leading whitespace
                currentLine.Append(line.TrimStart());
            }
            else
            {
                currentLine.Append(line);
            }

            // Check if line ends with escape char (and not escaped itself)
            var trimmedLine = currentLine.ToString().TrimEnd();
            if (trimmedLine.Length > 0 && trimmedLine[^1] == escapeChar &&
                !IsLineEndEscaped(trimmedLine, escapeChar))
            {
                // Remove trailing escape char and continue to next line
                currentLine.Clear();
                currentLine.Append(trimmedLine[..^1]);
                continue;
            }

            result.Add(currentLine.ToString());
            currentLine.Clear();
        }

        // Handle case where file ends with continuation
        if (currentLine.Length > 0)
        {
            result.Add(currentLine.ToString());
        }

        return result.ToArray();
    }

    /// <summary>
    /// Checks if the trailing escape character is itself escaped (making it a literal).
    /// </summary>
    private static bool IsLineEndEscaped(string line, char escapeChar)
    {
        if (line.Length < 2)
            return false;

        // Count consecutive escape chars at the end
        int count = 0;
        for (int i = line.Length - 1; i >= 0 && line[i] == escapeChar; i--)
        {
            count++;
        }

        // If even number (including the trailing one), the last one is escaped
        return count % 2 == 0;
    }

    private async Task ParseLinesRecursiveAsync(
        string[] lines, 
        string baseDirectory, 
        List<IgnorePattern> patterns, 
        ParseContext parseContext,
        HashSet<string> processedFiles,
        CancellationToken cancellationToken)
    {
        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            
            var parsed = IgnorePatternFactory.ParseLine(line, parseContext.EscapeChar);
            
            if (parsed.NewEscapeChar.HasValue)
            {
                parseContext.EscapeChar = parsed.NewEscapeChar.Value;
                _logger.LogDebug("Updated escape character to: {EscapeChar}", parseContext.EscapeChar);
                continue;
            }
            
            if (parsed.IncludeFile != null)
            {
                var includePath = Path.IsPathRooted(parsed.IncludeFile)
                    ? parsed.IncludeFile
                    : Path.Combine(baseDirectory, parsed.IncludeFile);
                
                var normalizedIncludePath = Path.GetFullPath(includePath);
                
                if (processedFiles.Contains(normalizedIncludePath))
                {
                    _logger.LogWarning("Circular include detected: {IncludePath}", normalizedIncludePath);
                    continue;
                }
                
                if (File.Exists(normalizedIncludePath))
                {
                    processedFiles.Add(normalizedIncludePath);
                    _logger.LogDebug("Including patterns from: {IncludePath}", normalizedIncludePath);
                    
                    var includeLines = await File.ReadAllLinesAsync(normalizedIncludePath, cancellationToken).ConfigureAwait(false);
                    await ParseLinesRecursiveAsync(includeLines, Path.GetDirectoryName(normalizedIncludePath) ?? baseDirectory, patterns, parseContext, processedFiles, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    _logger.LogWarning("Include file not found: {IncludePath}", normalizedIncludePath);
                }
                continue;
            }
            
            patterns.AddRange(parsed.Patterns);
        }
    }

    private IgnoreResult MatchInternal(string normalizedPath)
    {
        // In Syncthing: last matching pattern wins (important for negation patterns)
        IgnoreResult lastResult = IgnoreResult.NotIgnored;
        bool hasMatch = false;
        
        foreach (var pattern in _patterns)
        {
            if (pattern.Matches(normalizedPath))
            {
                lastResult = pattern.Result;
                hasMatch = true;
                
                _logger.LogTrace("Path {Path} matched pattern {Pattern} with result {Result}", 
                    normalizedPath, pattern.Pattern, lastResult);
            }
        }
        
        if (hasMatch)
        {
            // Add directory skip optimization for ignored directories
            if (lastResult.IsIgnored() && normalizedPath.EndsWith('/'))
            {
                lastResult |= IgnoreResult.CanSkipDir;
            }
            
            return lastResult;
        }

        return IgnoreResult.NotIgnored;
    }

    private void ClearPatterns()
    {
        _lines.Clear();
        _patterns.Clear();
        _currentHash = string.Empty;
        _lastModified = DateTime.MinValue;
        _cache?.Clear();
    }

    private static string NormalizePath(string path)
    {
        // Normalize separators to forward slashes (Syncthing standard)
        return path.Replace('\\', '/').Trim('/');
    }

    private static string ComputeHash(string[] lines)
    {
        var content = string.Join('\n', lines);
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    /// <summary>
    /// Disposes the matcher and cancels any ongoing operations
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cancellationTokenSource.Cancel(); } catch (ObjectDisposedException) { }
        try { _cancellationTokenSource.Dispose(); } catch (ObjectDisposedException) { }
    }

    private bool _disposed;
}

/// <summary>
/// Helper class to hold mutable parse context state
/// </summary>
internal class ParseContext
{
    public char EscapeChar { get; set; } = '\\';
}