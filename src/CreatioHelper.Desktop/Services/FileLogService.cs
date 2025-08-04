using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CreatioHelper.Services;

public static class FileLogService
{
    private static readonly ConcurrentQueue<LogEntry> _queue = new();
    private static readonly Timer _timer;
    private static readonly SemaphoreSlim _semaphore = new(1, 1);
    
    public static bool Enabled { get; set; }
    public static string LogFilePath { get; set; } = "log.txt";

    static FileLogService()
    {
        // Flush queue every 1 second
        _timer = new Timer(async _ => await FlushQueueAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public static void AppendLine(string line)
    {
        if (!Enabled)
            return;

        _queue.Enqueue(new LogEntry { Text = line + Environment.NewLine, IsClear = false });
    }

    public static void Clear()
    {
        if (!Enabled)
            return;

        _queue.Enqueue(new LogEntry { Text = string.Empty, IsClear = true });
    }

    public static async Task FlushAsync()
    {
        await FlushQueueAsync().ConfigureAwait(false);
    }

    private static async Task FlushQueueAsync()
    {
        if (_queue.IsEmpty || !Enabled)
            return;

        await _semaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            var entries = new List<LogEntry>();
            while (_queue.TryDequeue(out var entry))
            {
                entries.Add(entry);
            }

            if (entries.Count == 0)
                return;

            // Check for clear operations
            bool shouldClear = entries.Any(e => e.IsClear);
            if (shouldClear)
            {
                await File.WriteAllTextAsync(LogFilePath, string.Empty).ConfigureAwait(false);
                // Only process entries after the last clear
                var lastClearIndex = entries.FindLastIndex(e => e.IsClear);
                entries = entries.Skip(lastClearIndex + 1).ToList();
            }

            // Append all remaining entries
            if (entries.Count > 0)
            {
                var content = string.Join("", entries.Where(e => !e.IsClear).Select(e => e.Text));
                if (!string.IsNullOrEmpty(content))
                {
                    await File.AppendAllTextAsync(LogFilePath, content).ConfigureAwait(false);
                }
            }
        }
        catch (Exception)
        {
            // Ignore file I/O errors in logging
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private record LogEntry
    {
        public string Text { get; init; } = string.Empty;
        public bool IsClear { get; init; }
    }
}