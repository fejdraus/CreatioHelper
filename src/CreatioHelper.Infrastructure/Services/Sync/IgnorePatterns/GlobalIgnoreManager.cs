using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Extensions;

namespace CreatioHelper.Infrastructure.Services.Sync.IgnorePatterns;

/// <summary>
/// Manages global ignore patterns that apply to all folders
/// Compatible with Syncthing's global ignore handling
/// </summary>
public class GlobalIgnoreManager : IDisposable
{
    private readonly ILogger<GlobalIgnoreManager> _logger;
    private readonly SyncConfigurationFromFile _config;
    private readonly Dictionary<string, IgnoreMatcher> _globalMatchers = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    public GlobalIgnoreManager(ILogger<GlobalIgnoreManager> logger, IOptions<SyncConfigurationFromFile> config)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config?.Value ?? throw new ArgumentNullException(nameof(config));
    }

    /// <summary>
    /// Loads global ignore patterns for all configured folders
    /// </summary>
    public async Task LoadGlobalPatternsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var tasks = new List<Task>();
            
            foreach (var folder in _config.Folders)
            {
                tasks.Add(LoadFolderGlobalPatternsAsync(folder, cancellationToken));
            }
            
            await Task.WhenAll(tasks).ConfigureAwait(false);
            
            _logger.LogInformation("Loaded global ignore patterns for {FolderCount} folders", 
                _config.Folders.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load global ignore patterns");
            throw;
        }
    }

    /// <summary>
    /// Gets the global ignore matcher for a specific folder
    /// </summary>
    public IgnoreMatcher? GetMatcher(string folderId)
    {
        lock (_lock)
        {
            return _globalMatchers.TryGetValue(folderId, out var matcher) ? matcher : null;
        }
    }

    /// <summary>
    /// Tests if a path should be globally ignored for a folder
    /// </summary>
    public IgnoreResult Match(string folderId, string path)
    {
        var matcher = GetMatcher(folderId);
        if (matcher == null)
            return IgnoreResult.NotIgnored;

        return matcher.Match(path);
    }

    /// <summary>
    /// Gets all global ignore patterns for a folder
    /// </summary>
    public IReadOnlyList<IgnorePattern> GetPatterns(string folderId)
    {
        var matcher = GetMatcher(folderId);
        return matcher?.Patterns ?? Array.Empty<IgnorePattern>();
    }

    /// <summary>
    /// Reloads global ignore patterns for a specific folder
    /// </summary>
    public async Task ReloadFolderAsync(string folderId, CancellationToken cancellationToken = default)
    {
        var folder = _config.Folders.Find(f => f.Id == folderId);
        if (folder == null)
        {
            _logger.LogWarning("Folder not found for reload: {FolderId}", folderId);
            return;
        }

        await LoadFolderGlobalPatternsAsync(folder, cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadFolderGlobalPatternsAsync(SyncConfigurationFromFile.SyncFolderConfig folder, CancellationToken cancellationToken)
    {
        try
        {
            var globalIgnorePath = GetGlobalIgnorePath(folder);
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            var logger = loggerFactory.CreateLogger<IgnoreMatcher>();
            var matcher = new IgnoreMatcher(folder.Path, logger);

            if (File.Exists(globalIgnorePath))
            {
                await matcher.LoadFromFileAsync(globalIgnorePath, cancellationToken).ConfigureAwait(false);
                
                lock (_lock)
                {
                    // Dispose existing matcher if present
                    if (_globalMatchers.TryGetValue(folder.Id, out var existingMatcher))
                    {
                        existingMatcher.Dispose();
                    }
                    
                    _globalMatchers[folder.Id] = matcher;
                }
                
                _logger.LogDebug("Loaded global ignore patterns for folder {FolderId} from {Path}", 
                    folder.Id, globalIgnorePath);
            }
            else
            {
                // Create empty matcher for consistency
                lock (_lock)
                {
                    if (_globalMatchers.TryGetValue(folder.Id, out var existingMatcher))
                    {
                        existingMatcher.Dispose();
                    }
                    
                    _globalMatchers[folder.Id] = matcher;
                }
                
                _logger.LogDebug("No global ignore file found for folder {FolderId}: {Path}", 
                    folder.Id, globalIgnorePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load global ignore patterns for folder {FolderId}", folder.Id);
            throw;
        }
    }

    /// <summary>
    /// Gets the path to the global ignore file for a folder
    /// Follows Syncthing convention: .stglobalignore in folder root
    /// </summary>
    private static string GetGlobalIgnorePath(SyncConfigurationFromFile.SyncFolderConfig folder)
    {
        return Path.Combine(folder.Path, ".stglobalignore");
    }

    /// <summary>
    /// Disposes all matchers and cancels operations
    /// </summary>
    public void Dispose()
    {
        _cancellationTokenSource.Cancel();

        lock (_lock)
        {
            foreach (var matcher in _globalMatchers.Values)
            {
                matcher.Dispose();
            }
            _globalMatchers.Clear();
        }

        _cancellationTokenSource.Dispose();
    }
}

