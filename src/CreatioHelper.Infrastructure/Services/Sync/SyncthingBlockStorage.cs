using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using CreatioHelper.Application.Interfaces;
using CreatioHelper.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Syncthing-compatible block storage system for managing physical block data
/// Stores blocks on disk with optional compression and verification
/// Implements content-addressed storage with SHA-256 hash-based file names
/// </summary>
public class SyncthingBlockStorage : IDisposable
{
    private readonly ILogger<SyncthingBlockStorage> _logger;
    private readonly IBlockInfoRepository _blockRepository;
    private readonly string _storageDirectory;
    private readonly SyncthingBlockStorageOptions _options;
    
    // Cache for frequently accessed blocks
    private readonly ConcurrentDictionary<string, CachedBlock> _blockCache = new();
    private readonly object _cacheLock = new();
    private long _cacheMemoryUsage = 0;
    
    // Statistics
    private long _totalBlocksStored = 0;
    private long _totalBytesStored = 0;
    private long _totalCacheHits = 0;
    private long _totalCacheMisses = 0;
    
    public SyncthingBlockStorage(
        ILogger<SyncthingBlockStorage> logger,
        IBlockInfoRepository blockRepository,
        string storageDirectory,
        SyncthingBlockStorageOptions? options = null)
    {
        _logger = logger;
        _blockRepository = blockRepository;
        _storageDirectory = storageDirectory;
        _options = options ?? new SyncthingBlockStorageOptions();
        
        // Create storage directory
        if (!Directory.Exists(_storageDirectory))
        {
            Directory.CreateDirectory(_storageDirectory);
            _logger.LogInformation("Created block storage directory: {StorageDirectory}", _storageDirectory);
        }
        
        _logger.LogInformation("Initialized Syncthing block storage: {StorageDirectory}, cache={CacheSize}MB, compression={Compression}",
            _storageDirectory, _options.MaxCacheMemoryMB, _options.EnableCompression);
    }
    
    /// <summary>
    /// Store a block of data with SHA-256 hash verification
    /// </summary>
    public async Task<bool> StoreBlockAsync(byte[] data, byte[] expectedHash, CancellationToken cancellationToken = default)
    {
        try
        {
            // Verify hash
            var actualHash = SHA256.HashData(data);
            if (!actualHash.SequenceEqual(expectedHash))
            {
                _logger.LogError("Block hash mismatch: expected {ExpectedHash}, got {ActualHash}",
                    Convert.ToHexString(expectedHash), Convert.ToHexString(actualHash));
                return false;
            }
            
            var hashString = Convert.ToHexString(expectedHash).ToLowerInvariant();
            var filePath = GetBlockFilePath(hashString);
            
            // Check if block already exists
            if (File.Exists(filePath))
            {
                _logger.LogTrace("Block {Hash} already exists", hashString[..8]);
                return true;
            }
            
            // Store block data
            var dataToStore = data;
            var compressionType = "none";
            
            if (_options.EnableCompression && data.Length > _options.CompressionThreshold)
            {
                dataToStore = await CompressDataAsync(data, cancellationToken);
                compressionType = "gzip";
                
                _logger.LogTrace("Compressed block {Hash}: {OriginalSize} -> {CompressedSize} bytes ({Ratio:F1}%)",
                    hashString[..8], data.Length, dataToStore.Length, 
                    (double)dataToStore.Length / data.Length * 100);
            }
            
            // Ensure directory exists
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            // Write to temporary file first, then rename (atomic operation)
            var tempFilePath = filePath + ".tmp";
            await File.WriteAllBytesAsync(tempFilePath, dataToStore, cancellationToken);
            File.Move(tempFilePath, filePath);
            
            // Update cache if enabled
            if (_options.EnableCache && data.Length <= _options.MaxCacheBlockSize)
            {
                AddToCache(hashString, data);
            }
            
            // Update statistics
            Interlocked.Increment(ref _totalBlocksStored);
            Interlocked.Add(ref _totalBytesStored, data.Length);
            
            _logger.LogTrace("Stored block {Hash}: {Size} bytes, compression={Compression}",
                hashString[..8], data.Length, compressionType);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store block {Hash}", Convert.ToHexString(expectedHash)[..8]);
            return false;
        }
    }
    
    /// <summary>
    /// Retrieve a block of data by hash
    /// </summary>
    public async Task<byte[]?> GetBlockAsync(byte[] hash, CancellationToken cancellationToken = default)
    {
        try
        {
            var hashString = Convert.ToHexString(hash).ToLowerInvariant();
            
            // Check cache first
            if (_options.EnableCache && _blockCache.TryGetValue(hashString, out var cachedBlock))
            {
                cachedBlock.LastAccessed = DateTime.UtcNow;
                Interlocked.Increment(ref _totalCacheHits);
                
                _logger.LogTrace("Cache hit for block {Hash}", hashString[..8]);
                return cachedBlock.Data;
            }
            
            Interlocked.Increment(ref _totalCacheMisses);
            
            var filePath = GetBlockFilePath(hashString);
            if (!File.Exists(filePath))
            {
                _logger.LogTrace("Block {Hash} not found", hashString[..8]);
                return null;
            }
            
            // Read block data
            var fileData = await File.ReadAllBytesAsync(filePath, cancellationToken);
            
            // Check if data is compressed (simple heuristic: check file size vs metadata)
            var blockMetadata = await _blockRepository.GetAsync(hash);
            var isCompressed = blockMetadata != null && fileData.Length < blockMetadata.Size;
            
            byte[] blockData;
            if (isCompressed)
            {
                blockData = await DecompressDataAsync(fileData, cancellationToken);
                _logger.LogTrace("Decompressed block {Hash}: {CompressedSize} -> {OriginalSize} bytes",
                    hashString[..8], fileData.Length, blockData.Length);
            }
            else
            {
                blockData = fileData;
            }
            
            // Verify hash
            if (_options.VerifyOnRead)
            {
                var actualHash = SHA256.HashData(blockData);
                if (!actualHash.SequenceEqual(hash))
                {
                    _logger.LogError("Block corruption detected for {Hash}: hash mismatch", hashString[..8]);
                    return null;
                }
            }
            
            // Update cache
            if (_options.EnableCache && blockData.Length <= _options.MaxCacheBlockSize)
            {
                AddToCache(hashString, blockData);
            }
            
            // Update access time in repository
            _ = Task.Run(async () => {
                try
                {
                    await _blockRepository.UpdateLastAccessedAsync(hash);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to update last accessed time for block {Hash}", hashString[..8]);
                }
            }, cancellationToken);
            
            _logger.LogTrace("Retrieved block {Hash}: {Size} bytes", hashString[..8], blockData.Length);
            return blockData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get block {Hash}", Convert.ToHexString(hash)[..8]);
            return null;
        }
    }
    
    /// <summary>
    /// Check if a block exists in storage
    /// </summary>
    public bool HasBlock(byte[] hash)
    {
        var hashString = Convert.ToHexString(hash).ToLowerInvariant();
        
        // Check cache first
        if (_options.EnableCache && _blockCache.ContainsKey(hashString))
        {
            return true;
        }
        
        // Check file system
        var filePath = GetBlockFilePath(hashString);
        return File.Exists(filePath);
    }
    
    /// <summary>
    /// Delete a block from storage
    /// </summary>
    public Task<bool> DeleteBlockAsync(byte[] hash)
    {
        try
        {
            var hashString = Convert.ToHexString(hash).ToLowerInvariant();
            var filePath = GetBlockFilePath(hashString);
            
            // Remove from cache
            _blockCache.TryRemove(hashString, out var removedBlock);
            if (removedBlock != null)
            {
                Interlocked.Add(ref _cacheMemoryUsage, -removedBlock.Data.Length);
            }
            
            // Delete file
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                _logger.LogTrace("Deleted block {Hash}", hashString[..8]);
                return Task.FromResult(true);
            }
            
            return Task.FromResult(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete block {Hash}", Convert.ToHexString(hash)[..8]);
            return Task.FromResult(false);
        }
    }
    
    /// <summary>
    /// Get storage statistics
    /// </summary>
    public SyncthingBlockStorageStats GetStatistics()
    {
        var directoryInfo = new DirectoryInfo(_storageDirectory);
        var totalFiles = 0L;
        var totalDiskSize = 0L;
        
        try
        {
            var files = directoryInfo.GetFiles("*", SearchOption.AllDirectories);
            totalFiles = files.Length;
            totalDiskSize = files.Sum(f => f.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to calculate storage statistics");
        }
        
        return new SyncthingBlockStorageStats
        {
            TotalBlocksStored = _totalBlocksStored,
            TotalBytesStored = _totalBytesStored,
            TotalFilesOnDisk = totalFiles,
            TotalDiskUsage = totalDiskSize,
            CacheSize = _blockCache.Count,
            CacheMemoryUsage = _cacheMemoryUsage,
            CacheHitRatio = _totalCacheHits + _totalCacheMisses > 0 ? 
                (double)_totalCacheHits / (_totalCacheHits + _totalCacheMisses) : 0.0
        };
    }
    
    /// <summary>
    /// Clean up cache by removing least recently used blocks
    /// </summary>
    public void CleanupCache()
    {
        if (!_options.EnableCache || _cacheMemoryUsage <= _options.MaxCacheMemoryMB * 1024 * 1024)
        {
            return;
        }
        
        lock (_cacheLock)
        {
            var cacheEntries = _blockCache.ToArray()
                .OrderBy(kv => kv.Value.LastAccessed)
                .ToList();
            
            var removedCount = 0;
            var removedBytes = 0L;
            
            // Remove oldest entries until we're under the memory limit
            foreach (var entry in cacheEntries)
            {
                if (_cacheMemoryUsage <= _options.MaxCacheMemoryMB * 1024 * 1024 * 0.8) // 80% threshold
                {
                    break;
                }
                
                if (_blockCache.TryRemove(entry.Key, out var removedBlock))
                {
                    _cacheMemoryUsage -= removedBlock.Data.Length;
                    removedCount++;
                    removedBytes += removedBlock.Data.Length;
                }
            }
            
            if (removedCount > 0)
            {
                _logger.LogDebug("Cleaned up cache: removed {Count} blocks ({Bytes} bytes), cache now {Size} blocks ({Memory}MB)",
                    removedCount, removedBytes, _blockCache.Count, _cacheMemoryUsage / 1024 / 1024);
            }
        }
    }
    
    private string GetBlockFilePath(string hashString)
    {
        // Use first 2 characters for directory to distribute files
        var subDir = hashString[..2];
        return Path.Combine(_storageDirectory, subDir, hashString);
    }
    
    private void AddToCache(string hashString, byte[] data)
    {
        if (!_options.EnableCache || data.Length > _options.MaxCacheBlockSize)
        {
            return;
        }
        
        lock (_cacheLock)
        {
            if (_blockCache.TryAdd(hashString, new CachedBlock
            {
                Data = data,
                LastAccessed = DateTime.UtcNow
            }))
            {
                _cacheMemoryUsage += data.Length;
                
                // Clean up if over memory limit
                if (_cacheMemoryUsage > _options.MaxCacheMemoryMB * 1024 * 1024)
                {
                    CleanupCache();
                }
            }
        }
    }
    
    private async Task<byte[]> CompressDataAsync(byte[] data, CancellationToken cancellationToken)
    {
        using var memoryStream = new MemoryStream();
        using var gzipStream = new GZipStream(memoryStream, CompressionLevel.Fastest);
        await gzipStream.WriteAsync(data, cancellationToken);
        await gzipStream.FlushAsync(cancellationToken);
        return memoryStream.ToArray();
    }
    
    private async Task<byte[]> DecompressDataAsync(byte[] compressedData, CancellationToken cancellationToken)
    {
        using var memoryStream = new MemoryStream(compressedData);
        using var gzipStream = new GZipStream(memoryStream, CompressionMode.Decompress);
        using var outputStream = new MemoryStream();
        await gzipStream.CopyToAsync(outputStream, cancellationToken);
        return outputStream.ToArray();
    }
    
    public void Dispose()
    {
        _blockCache.Clear();
        _logger.LogDebug("Syncthing block storage disposed");
    }
}

/// <summary>
/// Configuration options for block storage
/// </summary>
public class SyncthingBlockStorageOptions
{
    public bool EnableCompression { get; set; } = true;
    public int CompressionThreshold { get; set; } = 1024; // Compress blocks larger than 1KB
    public bool EnableCache { get; set; } = true;
    public int MaxCacheMemoryMB { get; set; } = 64; // 64MB cache by default
    public int MaxCacheBlockSize { get; set; } = 1024 * 1024; // Don't cache blocks larger than 1MB
    public bool VerifyOnRead { get; set; } = false; // Disable for performance, enable for paranoid mode
}

/// <summary>
/// Statistics for block storage system
/// </summary>
public class SyncthingBlockStorageStats
{
    public long TotalBlocksStored { get; set; }
    public long TotalBytesStored { get; set; }
    public long TotalFilesOnDisk { get; set; }
    public long TotalDiskUsage { get; set; }
    public int CacheSize { get; set; }
    public long CacheMemoryUsage { get; set; }
    public double CacheHitRatio { get; set; }
}

/// <summary>
/// Cached block data with access tracking
/// </summary>
internal class CachedBlock
{
    public byte[] Data { get; set; } = Array.Empty<byte>();
    public DateTime LastAccessed { get; set; }
}