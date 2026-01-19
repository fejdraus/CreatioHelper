using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync.Transfer;

/// <summary>
/// Compression mode for data transfer.
/// Based on Syncthing's Compression device configuration.
/// </summary>
public enum CompressionMode
{
    /// <summary>
    /// Never compress data.
    /// </summary>
    Never,

    /// <summary>
    /// Always compress data.
    /// </summary>
    Always,

    /// <summary>
    /// Compress only metadata (index, etc.), not file data.
    /// This is the default in Syncthing.
    /// </summary>
    Metadata
}

/// <summary>
/// Service for handling compression of sync data.
/// Based on Syncthing's compression configuration.
/// </summary>
public interface ICompressionService
{
    /// <summary>
    /// Get the compression mode for a device.
    /// </summary>
    CompressionMode GetCompressionMode(string deviceId);

    /// <summary>
    /// Set the compression mode for a device.
    /// </summary>
    void SetCompressionMode(string deviceId, CompressionMode mode);

    /// <summary>
    /// Check if data should be compressed for a device.
    /// </summary>
    bool ShouldCompress(string deviceId, DataType dataType);

    /// <summary>
    /// Compress data if needed.
    /// </summary>
    byte[] CompressIfNeeded(string deviceId, byte[] data, DataType dataType);

    /// <summary>
    /// Decompress data if compressed.
    /// </summary>
    byte[] DecompressIfNeeded(byte[] data, bool isCompressed);

    /// <summary>
    /// Check if a file extension is compressible.
    /// </summary>
    bool IsCompressible(string fileExtension);

    /// <summary>
    /// Get compression statistics.
    /// </summary>
    CompressionStats GetStats(string deviceId);

    /// <summary>
    /// Get global compression statistics.
    /// </summary>
    GlobalCompressionStats GetGlobalStats();
}

/// <summary>
/// Type of data being transferred.
/// </summary>
public enum DataType
{
    /// <summary>
    /// File data blocks.
    /// </summary>
    FileData,

    /// <summary>
    /// Index/metadata messages.
    /// </summary>
    Metadata,

    /// <summary>
    /// Request messages.
    /// </summary>
    Request,

    /// <summary>
    /// Response messages.
    /// </summary>
    Response
}

/// <summary>
/// Compression statistics for a device.
/// </summary>
public class CompressionStats
{
    public string DeviceId { get; init; } = string.Empty;
    public CompressionMode Mode { get; init; }
    public long BytesBeforeCompression { get; set; }
    public long BytesAfterCompression { get; set; }
    public long CompressedBlocks { get; set; }
    public long UncompressedBlocks { get; set; }
    public long SkippedBlocks { get; set; }

    public double CompressionRatio =>
        BytesBeforeCompression > 0
            ? (double)BytesAfterCompression / BytesBeforeCompression
            : 1.0;

    public long BytesSaved => BytesBeforeCompression - BytesAfterCompression;
}

/// <summary>
/// Global compression statistics.
/// </summary>
public class GlobalCompressionStats
{
    public long TotalBytesBeforeCompression { get; set; }
    public long TotalBytesAfterCompression { get; set; }
    public long TotalCompressedBlocks { get; set; }
    public long TotalUncompressedBlocks { get; set; }
    public int DevicesWithCompression { get; set; }

    public double AverageCompressionRatio =>
        TotalBytesBeforeCompression > 0
            ? (double)TotalBytesAfterCompression / TotalBytesBeforeCompression
            : 1.0;

    public long TotalBytesSaved => TotalBytesBeforeCompression - TotalBytesAfterCompression;
}

/// <summary>
/// Configuration for compression service.
/// </summary>
public class CompressionConfiguration
{
    /// <summary>
    /// Default compression mode for devices.
    /// </summary>
    public CompressionMode DefaultMode { get; set; } = CompressionMode.Metadata;

    /// <summary>
    /// Per-device compression mode overrides.
    /// </summary>
    public Dictionary<string, CompressionMode> DeviceModes { get; } = new();

    /// <summary>
    /// Compression level to use.
    /// </summary>
    public CompressionLevel Level { get; set; } = CompressionLevel.Fastest;

    /// <summary>
    /// Minimum data size to compress (bytes).
    /// </summary>
    public int MinSizeToCompress { get; set; } = 128;

    /// <summary>
    /// File extensions that are already compressed and should be skipped.
    /// </summary>
    public HashSet<string> IncompressibleExtensions { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".gz", ".bz2", ".xz", ".7z", ".rar",
        ".jpg", ".jpeg", ".png", ".gif", ".webp",
        ".mp3", ".mp4", ".mkv", ".avi", ".mov", ".webm",
        ".pdf", ".docx", ".xlsx", ".pptx",
        ".jar", ".apk", ".ipa"
    };

    /// <summary>
    /// Get effective mode for a device.
    /// </summary>
    public CompressionMode GetEffectiveMode(string deviceId)
    {
        if (DeviceModes.TryGetValue(deviceId, out var mode))
        {
            return mode;
        }
        return DefaultMode;
    }
}

/// <summary>
/// Implementation of compression service.
/// </summary>
public class CompressionService : ICompressionService
{
    private readonly ILogger<CompressionService> _logger;
    private readonly CompressionConfiguration _config;
    private readonly ConcurrentDictionary<string, CompressionStats> _deviceStats = new();

    // Magic bytes to identify compressed data
    private static readonly byte[] CompressedMagic = { 0x1F, 0x8B }; // gzip magic

    public CompressionService(
        ILogger<CompressionService> logger,
        CompressionConfiguration? config = null)
    {
        _logger = logger;
        _config = config ?? new CompressionConfiguration();
    }

    /// <inheritdoc />
    public CompressionMode GetCompressionMode(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        return _config.GetEffectiveMode(deviceId);
    }

    /// <inheritdoc />
    public void SetCompressionMode(string deviceId, CompressionMode mode)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        _config.DeviceModes[deviceId] = mode;
        _logger.LogInformation("Set compression mode for device {DeviceId} to {Mode}", deviceId, mode);
    }

    /// <inheritdoc />
    public bool ShouldCompress(string deviceId, DataType dataType)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        var mode = GetCompressionMode(deviceId);

        return mode switch
        {
            CompressionMode.Never => false,
            CompressionMode.Always => true,
            CompressionMode.Metadata => dataType != DataType.FileData,
            _ => false
        };
    }

    /// <inheritdoc />
    public byte[] CompressIfNeeded(string deviceId, byte[] data, DataType dataType)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(data);

        var stats = GetOrCreateStats(deviceId);

        if (!ShouldCompress(deviceId, dataType))
        {
            stats.UncompressedBlocks++;
            return data;
        }

        if (data.Length < _config.MinSizeToCompress)
        {
            stats.SkippedBlocks++;
            return data;
        }

        try
        {
            var compressed = Compress(data);

            // Only use compressed if it's actually smaller
            if (compressed.Length < data.Length)
            {
                stats.BytesBeforeCompression += data.Length;
                stats.BytesAfterCompression += compressed.Length;
                stats.CompressedBlocks++;
                return compressed;
            }

            stats.SkippedBlocks++;
            return data;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Compression failed, sending uncompressed");
            stats.UncompressedBlocks++;
            return data;
        }
    }

    /// <inheritdoc />
    public byte[] DecompressIfNeeded(byte[] data, bool isCompressed)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!isCompressed)
        {
            return data;
        }

        // Check for gzip magic bytes
        if (data.Length < 2 || data[0] != CompressedMagic[0] || data[1] != CompressedMagic[1])
        {
            return data; // Not actually compressed
        }

        try
        {
            return Decompress(data);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Decompression failed, returning original data");
            return data;
        }
    }

    /// <inheritdoc />
    public bool IsCompressible(string fileExtension)
    {
        if (string.IsNullOrEmpty(fileExtension))
        {
            return true;
        }

        var ext = fileExtension.StartsWith('.') ? fileExtension : "." + fileExtension;
        return !_config.IncompressibleExtensions.Contains(ext);
    }

    /// <inheritdoc />
    public CompressionStats GetStats(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        return GetOrCreateStats(deviceId);
    }

    /// <inheritdoc />
    public GlobalCompressionStats GetGlobalStats()
    {
        var stats = new GlobalCompressionStats();

        foreach (var deviceStats in _deviceStats.Values)
        {
            stats.TotalBytesBeforeCompression += deviceStats.BytesBeforeCompression;
            stats.TotalBytesAfterCompression += deviceStats.BytesAfterCompression;
            stats.TotalCompressedBlocks += deviceStats.CompressedBlocks;
            stats.TotalUncompressedBlocks += deviceStats.UncompressedBlocks;

            if (deviceStats.Mode != CompressionMode.Never)
            {
                stats.DevicesWithCompression++;
            }
        }

        return stats;
    }

    private CompressionStats GetOrCreateStats(string deviceId)
    {
        return _deviceStats.GetOrAdd(deviceId, id => new CompressionStats
        {
            DeviceId = id,
            Mode = _config.GetEffectiveMode(id)
        });
    }

    private byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, _config.Level, leaveOpen: true))
        {
            gzip.Write(data, 0, data.Length);
        }
        return output.ToArray();
    }

    private byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
