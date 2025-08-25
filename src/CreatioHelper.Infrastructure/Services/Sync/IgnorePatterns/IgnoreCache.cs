using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CreatioHelper.Infrastructure.Services.Sync.IgnorePatterns;

/// <summary>
/// LRU cache for ignore pattern matching results, similar to Syncthing's cache implementation
/// Provides significant performance improvements for repeated path matching
/// </summary>
public class IgnoreCache
{
    private readonly int _maxSize;
    private readonly ConcurrentDictionary<string, CacheNode> _cache;
    private readonly object _lruLock = new();
    private CacheNode? _head;
    private CacheNode? _tail;
    private int _currentSize;

    public IgnoreCache(int maxSize = 8192)
    {
        _maxSize = maxSize;
        _cache = new ConcurrentDictionary<string, CacheNode>();
    }

    /// <summary>
    /// Gets a cached result for the given path
    /// </summary>
    public bool TryGet(string path, out IgnoreResult result)
    {
        result = IgnoreResult.NotIgnored;
        
        if (_cache.TryGetValue(path, out var node))
        {
            // Move to front of LRU list
            lock (_lruLock)
            {
                MoveToFront(node);
            }
            
            result = node.Result;
            return true;
        }
        
        return false;
    }

    /// <summary>
    /// Adds a result to the cache
    /// </summary>
    public void Set(string path, IgnoreResult result)
    {
        lock (_lruLock)
        {
            if (_cache.TryGetValue(path, out var existingNode))
            {
                // Update existing node
                existingNode.Result = result;
                MoveToFront(existingNode);
                return;
            }

            // Create new node
            var newNode = new CacheNode(path, result);
            _cache[path] = newNode;
            
            // Add to front of LRU list
            if (_head == null)
            {
                _head = _tail = newNode;
            }
            else
            {
                newNode.Next = _head;
                _head.Previous = newNode;
                _head = newNode;
            }
            
            _currentSize++;

            // Evict if over capacity
            while (_currentSize > _maxSize && _tail != null)
            {
                EvictLeastRecentlyUsed();
            }
        }
    }

    /// <summary>
    /// Clears all cached entries
    /// </summary>
    public void Clear()
    {
        lock (_lruLock)
        {
            _cache.Clear();
            _head = _tail = null;
            _currentSize = 0;
        }
    }

    /// <summary>
    /// Gets current cache statistics
    /// </summary>
    public CacheStats GetStats()
    {
        lock (_lruLock)
        {
            return new CacheStats(_currentSize, _maxSize, _cache.Count);
        }
    }

    private void MoveToFront(CacheNode node)
    {
        if (node == _head)
            return;

        // Remove from current position
        if (node.Previous != null)
            node.Previous.Next = node.Next;
        if (node.Next != null)
            node.Next.Previous = node.Previous;
        if (node == _tail)
            _tail = node.Previous;

        // Move to front
        node.Previous = null;
        node.Next = _head;
        if (_head != null)
            _head.Previous = node;
        _head = node;
        
        if (_tail == null)
            _tail = node;
    }

    private void EvictLeastRecentlyUsed()
    {
        if (_tail == null)
            return;

        var nodeToEvict = _tail;
        _cache.TryRemove(nodeToEvict.Path, out _);

        _tail = nodeToEvict.Previous;
        if (_tail != null)
            _tail.Next = null;
        else
            _head = null;

        _currentSize--;
    }

    /// <summary>
    /// Node in the LRU linked list
    /// </summary>
    private class CacheNode
    {
        public string Path { get; }
        public IgnoreResult Result { get; set; }
        public CacheNode? Previous { get; set; }
        public CacheNode? Next { get; set; }

        public CacheNode(string path, IgnoreResult result)
        {
            Path = path;
            Result = result;
        }
    }
}

/// <summary>
/// Cache statistics for monitoring and debugging
/// </summary>
public record CacheStats(int CurrentSize, int MaxSize, int DictionaryCount)
{
    public double FillPercentage => MaxSize > 0 ? (double)CurrentSize / MaxSize * 100 : 0;
    
    public override string ToString()
        => $"Cache: {CurrentSize}/{MaxSize} ({FillPercentage:F1}%), Dict: {DictionaryCount}";
}