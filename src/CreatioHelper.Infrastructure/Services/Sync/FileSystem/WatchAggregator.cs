using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// Event type for file system changes (based on Syncthing watchaggregator)
/// </summary>
[Flags]
public enum FsEventType
{
    None = 0,
    NonRemove = 1,  // Create, Modify, Rename
    Remove = 2,
    Mixed = NonRemove | Remove
}

/// <summary>
/// Aggregated event representing potentially multiple events at and/or recursively
/// below one path until it times out and a scan is scheduled.
/// (Based on Syncthing watchaggregator/aggregator.go)
/// </summary>
public record AggregatedEvent
{
    public DateTime FirstModTime { get; init; }
    public DateTime LastModTime { get; set; }
    public FsEventType EventType { get; set; }
}

/// <summary>
/// Directory node in the event tree structure
/// </summary>
internal class EventDir
{
    public Dictionary<string, AggregatedEvent> Events { get; } = new();
    public Dictionary<string, EventDir> Dirs { get; } = new();

    public int ChildCount => Events.Count + Dirs.Count;

    public DateTime FirstModTime()
    {
        if (ChildCount == 0)
            throw new InvalidOperationException("FirstModTime must not be used on empty EventDir");

        var firstModTime = DateTime.UtcNow;

        foreach (var childDir in Dirs.Values)
        {
            var dirTime = childDir.FirstModTime();
            if (dirTime < firstModTime)
                firstModTime = dirTime;
        }

        foreach (var evt in Events.Values)
        {
            if (evt.FirstModTime < firstModTime)
                firstModTime = evt.FirstModTime;
        }

        return firstModTime;
    }

    public FsEventType GetEventType()
    {
        if (ChildCount == 0)
            throw new InvalidOperationException("EventType must not be used on empty EventDir");

        var evType = FsEventType.None;

        foreach (var childDir in Dirs.Values)
        {
            evType |= childDir.GetEventType();
            if (evType == FsEventType.Mixed)
                return FsEventType.Mixed;
        }

        foreach (var evt in Events.Values)
        {
            evType |= evt.EventType;
            if (evType == FsEventType.Mixed)
                return FsEventType.Mixed;
        }

        return evType;
    }
}

/// <summary>
/// Event counter for tracking removes and non-removes
/// </summary>
internal class EventCounter
{
    public int Removes { get; private set; }
    public int NonRemoves { get; private set; }
    public int Total => Removes + NonRemoves;

    public void Add(FsEventType type, int n)
    {
        if ((type & FsEventType.Remove) != 0)
            Removes += n;
        else
            NonRemoves += n;
    }

    public void Reset()
    {
        Removes = 0;
        NonRemoves = 0;
    }
}

/// <summary>
/// Configuration for watch aggregator
/// </summary>
public record WatchAggregatorOptions
{
    /// <summary>
    /// Time after which an event is scheduled for scanning when no modifications occur.
    /// Default: 10 seconds
    /// </summary>
    public TimeSpan NotifyDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Time after which an event is scheduled for scanning even though modifications occur.
    /// Default: 60 seconds
    /// </summary>
    public TimeSpan NotifyTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum total files to track before switching to full folder scan.
    /// Default: 512
    /// </summary>
    public int MaxFiles { get; init; } = 512;

    /// <summary>
    /// Maximum files per directory before aggregating to parent.
    /// Default: 128
    /// </summary>
    public int MaxFilesPerDir { get; init; } = 128;
}

/// <summary>
/// Input event for the aggregator
/// </summary>
public record FsEvent
{
    public string Name { get; init; } = string.Empty;
    public FsEventType Type { get; init; }
}

/// <summary>
/// Aggregates rapid file system events within time windows to reduce redundant scans.
/// Deduplicates and batches notifications using a directory tree structure.
/// (Based on Syncthing lib/watchaggregator/aggregator.go)
/// </summary>
public class WatchAggregator : IDisposable
{
    private readonly ILogger<WatchAggregator> _logger;
    private readonly WatchAggregatorOptions _options;
    private readonly string _folderId;

    private readonly EventDir _root = new();
    private readonly EventCounter _counts = new();
    private readonly HashSet<string> _inProgress = new();
    private readonly object _lock = new();

    private Timer? _notifyTimer;
    private bool _timerNeedsReset = true;
    private bool _disposed;

    /// <summary>
    /// Event fired when paths are ready to be scanned.
    /// Paths are batched by event type: NonRemove first, then Mixed, then Remove.
    /// </summary>
    public event Action<IReadOnlyList<string>, FsEventType>? PathsReady;

    public WatchAggregator(
        ILogger<WatchAggregator> logger,
        string folderId,
        WatchAggregatorOptions? options = null)
    {
        _logger = logger;
        _folderId = folderId;
        _options = options ?? new WatchAggregatorOptions();

        _notifyTimer = new Timer(OnTimerElapsed, null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// Add a new file system event to be aggregated
    /// </summary>
    public void AddEvent(FsEvent evt)
    {
        lock (_lock)
        {
            if (_disposed) return;

            // If already scanning entire folder, drop event
            if (_root.Events.ContainsKey("."))
            {
                _logger.LogTrace("[{FolderId}] Will scan entire folder anyway; dropping: {Name}", _folderId, evt.Name);
                return;
            }

            // Skip paths we are currently modifying
            if (_inProgress.Contains(evt.Name))
            {
                _logger.LogTrace("[{FolderId}] Skipping path we modified: {Name}", _folderId, evt.Name);
                return;
            }

            AggregateEvent(evt, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// Mark a path as being modified by us (will be ignored until unmarked)
    /// </summary>
    public void MarkInProgress(string path)
    {
        lock (_lock)
        {
            _inProgress.Add(path);
        }
    }

    /// <summary>
    /// Unmark a path as being modified by us
    /// </summary>
    public void UnmarkInProgress(string path)
    {
        lock (_lock)
        {
            _inProgress.Remove(path);
        }
    }

    private void AggregateEvent(FsEvent evt, DateTime evTime)
    {
        var eventName = evt.Name;
        var eventType = evt.Type;

        // If root event or max files reached, scan entire folder
        if (eventName == "." || _counts.Total == _options.MaxFiles)
        {
            _logger.LogDebug("[{FolderId}] Scan entire folder (event={Name}, count={Count})",
                _folderId, eventName, _counts.Total);

            var firstModTime = evTime;
            if (_root.ChildCount != 0)
            {
                eventType = MergeEventType(eventType, _root.GetEventType());
                firstModTime = _root.FirstModTime();
            }

            _root.Dirs.Clear();
            _root.Events.Clear();
            _root.Events["."] = new AggregatedEvent
            {
                FirstModTime = firstModTime,
                LastModTime = evTime,
                EventType = eventType
            };

            _counts.Reset();
            _counts.Add(eventType, 1);
            ResetTimerIfNeeded();
            return;
        }

        var parentDir = _root;
        var pathSegments = eventName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Root dir can have up to MaxFiles children
        var localMaxFilesPerDir = _options.MaxFiles;
        var currPath = string.Empty;

        // Process parent directories
        for (int i = 0; i < pathSegments.Length - 1; i++)
        {
            var name = pathSegments[i];
            currPath = string.IsNullOrEmpty(currPath) ? name : Path.Combine(currPath, name);

            // Check if parent is already tracked as an event
            if (parentDir.Events.TryGetValue(name, out var existingEvent))
            {
                existingEvent.LastModTime = evTime;
                var merged = MergeEventType(eventType, existingEvent.EventType);
                if (existingEvent.EventType != merged)
                {
                    _counts.Add(existingEvent.EventType, -1);
                    _counts.Add(merged, 1);
                    existingEvent.EventType = merged;
                }
                _logger.LogTrace("[{FolderId}] Parent {Path} already tracked: {Name}", _folderId, currPath, eventName);
                return;
            }

            // Check if parent dir is full
            if (parentDir.ChildCount == localMaxFilesPerDir)
            {
                _logger.LogTrace("[{FolderId}] Parent dir {Path} has {Count} children, tracking it instead: {Name}",
                    _folderId, currPath, localMaxFilesPerDir, eventName);
                AggregateEvent(new FsEvent { Name = Path.GetDirectoryName(currPath) ?? ".", Type = eventType }, evTime);
                return;
            }

            // Get or create child directory
            if (!parentDir.Dirs.TryGetValue(name, out var childDir))
            {
                childDir = new EventDir();
                parentDir.Dirs[name] = childDir;
                _logger.LogTrace("[{FolderId}] Creating EventDir at: {Path}", _folderId, currPath);
            }
            parentDir = childDir;

            // After root, use MaxFilesPerDir
            if (i == 0)
                localMaxFilesPerDir = _options.MaxFilesPerDir;
        }

        // Process the actual file/dir name
        var fileName = pathSegments[^1];

        // Check if already tracked
        if (parentDir.Events.TryGetValue(fileName, out var ev))
        {
            ev.LastModTime = evTime;
            var merged = MergeEventType(eventType, ev.EventType);
            if (ev.EventType != merged)
            {
                _counts.Add(ev.EventType, -1);
                _counts.Add(merged, 1);
                ev.EventType = merged;
            }
            _logger.LogTrace("[{FolderId}] Already tracked: {Name}", _folderId, eventName);
            return;
        }

        // Check if a child dir exists at this name
        var hasChildDir = parentDir.Dirs.TryGetValue(fileName, out var existingChildDir);

        // Check parent capacity
        if (!hasChildDir && parentDir.ChildCount == localMaxFilesPerDir)
        {
            _logger.LogTrace("[{FolderId}] Parent dir full, tracking parent instead: {Name}", _folderId, eventName);
            AggregateEvent(new FsEvent { Name = Path.GetDirectoryName(eventName) ?? ".", Type = eventType }, evTime);
            return;
        }

        var firstMod = evTime;
        if (hasChildDir && existingChildDir != null)
        {
            firstMod = existingChildDir.FirstModTime();
            var merged = MergeEventType(eventType, existingChildDir.GetEventType());
            if (eventType != merged)
            {
                _counts.Add(eventType, -1);
                eventType = merged;
            }
            parentDir.Dirs.Remove(fileName);
        }

        _logger.LogTrace("[{FolderId}] Tracking ({Type}): {Name}", _folderId, eventType, eventName);
        parentDir.Events[fileName] = new AggregatedEvent
        {
            FirstModTime = firstMod,
            LastModTime = evTime,
            EventType = eventType
        };
        _counts.Add(eventType, 1);
        ResetTimerIfNeeded();
    }

    private void ResetTimerIfNeeded()
    {
        if (_timerNeedsReset && _notifyTimer != null)
        {
            _timerNeedsReset = false;
            _notifyTimer.Change(_options.NotifyDelay, Timeout.InfiniteTimeSpan);
            _logger.LogTrace("[{FolderId}] Timer reset to {Delay}", _folderId, _options.NotifyDelay);
        }
    }

    private void OnTimerElapsed(object? state)
    {
        List<(string Path, AggregatedEvent Event)>? oldEvents = null;

        lock (_lock)
        {
            if (_disposed) return;

            if (_counts.Total == 0)
            {
                _logger.LogTrace("[{FolderId}] No tracked events, waiting for new event", _folderId);
                _timerNeedsReset = true;
                return;
            }

            oldEvents = new List<(string, AggregatedEvent)>();
            var now = DateTime.UtcNow;

            PopOldEvents(oldEvents, _root, ".", now, delayRemoves: true);

            // If only removes remaining and no timeout needed, pop them too
            if (_options.NotifyDelay != _options.NotifyTimeout && _counts.NonRemoves == 0 && _counts.Removes > 0)
            {
                PopOldEvents(oldEvents, _root, ".", now, delayRemoves: false);
            }

            if (oldEvents.Count == 0)
            {
                _logger.LogTrace("[{FolderId}] No old events ready", _folderId);
                _notifyTimer?.Change(_options.NotifyDelay, Timeout.InfiniteTimeSpan);
                return;
            }

            _timerNeedsReset = true;
        }

        // Notify outside lock
        if (oldEvents != null && oldEvents.Count > 0)
        {
            NotifyPaths(oldEvents);
        }
    }

    private void PopOldEvents(List<(string Path, AggregatedEvent Event)> to, EventDir dir, string dirPath, DateTime now, bool delayRemoves)
    {
        // Process child directories recursively
        var emptyDirs = new List<string>();
        foreach (var (childName, childDir) in dir.Dirs)
        {
            var childPath = dirPath == "." ? childName : Path.Combine(dirPath, childName);
            PopOldEvents(to, childDir, childPath, now, delayRemoves);
            if (childDir.ChildCount == 0)
                emptyDirs.Add(childName);
        }
        foreach (var emptyDir in emptyDirs)
            dir.Dirs.Remove(emptyDir);

        // Process events
        var oldEventNames = new List<string>();
        foreach (var (name, evt) in dir.Events)
        {
            if (IsOld(evt, now, delayRemoves))
            {
                var eventPath = dirPath == "." ? name : (name == "." ? dirPath : Path.Combine(dirPath, name));
                to.Add((eventPath, evt));
                oldEventNames.Add(name);
                _counts.Add(evt.EventType, -1);
            }
        }
        foreach (var name in oldEventNames)
            dir.Events.Remove(name);
    }

    private bool IsOld(AggregatedEvent evt, DateTime now, bool delayRemoves)
    {
        // Non-removes or when not delaying removes: check if no recent modifications
        if ((!delayRemoves || evt.EventType == FsEventType.NonRemove) &&
            (now - evt.LastModTime) > _options.NotifyDelay / 2)
        {
            return true;
        }

        // Check timeout for events with continuous modifications
        return (now - evt.FirstModTime) > _options.NotifyTimeout;
    }

    private void NotifyPaths(List<(string Path, AggregatedEvent Event)> events)
    {
        _logger.LogDebug("[{FolderId}] Notifying about {Count} aggregated events", _folderId, events.Count);

        // Group by event type
        var byType = events.GroupBy(e => e.Event.EventType)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Path).ToList());

        // Notify in order: NonRemove, Mixed, Remove
        foreach (var evType in new[] { FsEventType.NonRemove, FsEventType.Mixed, FsEventType.Remove })
        {
            if (byType.TryGetValue(evType, out var paths) && paths.Count > 0)
            {
                PathsReady?.Invoke(paths, evType);
            }
        }
    }

    private static FsEventType MergeEventType(FsEventType a, FsEventType b)
    {
        return a | b;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            _notifyTimer?.Dispose();
            _notifyTimer = null;
        }
    }
}
