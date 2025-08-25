using System.IO.Compression;
using K4os.Compression.LZ4;
using Microsoft.Extensions.Logging;

namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Compression engine for block data (based on Syncthing's compression)
/// Supports LZ4 (primary) and GZIP (fallback) compression algorithms
/// </summary>
public class CompressionEngine
{
    private readonly ILogger<CompressionEngine> _logger;
    
    // Compression thresholds matching Syncthing behavior
    private const int MinCompressionSize = 128; // Don't compress blocks smaller than 128 bytes
    private const float MinCompressionRatio = 0.9f; // Only use compressed data if it's at least 10% smaller

    public CompressionEngine(ILogger<CompressionEngine> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Compresses block data using LZ4 algorithm (Syncthing's preferred method)
    /// </summary>
    /// <param name="data">Uncompressed data</param>
    /// <param name="compressionType">Preferred compression type</param>
    /// <returns>Compressed data and actual compression type used</returns>
    public (byte[] CompressedData, CompressionType ActualType) CompressBlock(byte[] data, CompressionType compressionType = CompressionType.LZ4)
    {
        if (data == null || data.Length == 0)
        {
            return (data ?? Array.Empty<byte>(), CompressionType.None);
        }

        // Don't compress small blocks - overhead not worth it
        if (data.Length < MinCompressionSize)
        {
            _logger.LogTrace("Block too small for compression: {Size} bytes", data.Length);
            return (data, CompressionType.None);
        }

        try
        {
            byte[] compressedData;
            CompressionType actualType;

            switch (compressionType)
            {
                case CompressionType.LZ4:
                    compressedData = CompressWithLZ4(data);
                    actualType = CompressionType.LZ4;
                    break;
                    
                case CompressionType.GZIP:
                    compressedData = CompressWithGZip(data);
                    actualType = CompressionType.GZIP;
                    break;
                    
                case CompressionType.Auto:
                    // Try LZ4 first, fallback to GZIP if needed
                    var lz4Result = CompressWithLZ4(data);
                    var gzipResult = CompressWithGZip(data);
                    
                    if (lz4Result.Length <= gzipResult.Length)
                    {
                        compressedData = lz4Result;
                        actualType = CompressionType.LZ4;
                    }
                    else
                    {
                        compressedData = gzipResult;
                        actualType = CompressionType.GZIP;
                    }
                    break;
                    
                default:
                    return (data, CompressionType.None);
            }

            // Check if compression was worthwhile (Syncthing behavior)
            var compressionRatio = (float)compressedData.Length / data.Length;
            if (compressionRatio >= MinCompressionRatio)
            {
                _logger.LogTrace("Compression not effective: {OriginalSize} -> {CompressedSize} ({Ratio:F2}), using uncompressed",
                    data.Length, compressedData.Length, compressionRatio);
                return (data, CompressionType.None);
            }

            _logger.LogTrace("Block compressed: {OriginalSize} -> {CompressedSize} ({Ratio:F2}) using {Type}",
                data.Length, compressedData.Length, compressionRatio, actualType);

            return (compressedData, actualType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Compression failed, using uncompressed data");
            return (data, CompressionType.None);
        }
    }

    /// <summary>
    /// Decompresses block data based on compression type
    /// </summary>
    /// <param name="compressedData">Compressed data</param>
    /// <param name="compressionType">Compression type used</param>
    /// <param name="expectedSize">Expected size after decompression (for validation)</param>
    /// <returns>Decompressed data</returns>
    public byte[] DecompressBlock(byte[] compressedData, CompressionType compressionType, int? expectedSize = null)
    {
        if (compressedData == null || compressedData.Length == 0)
        {
            return compressedData ?? Array.Empty<byte>();
        }

        if (compressionType == CompressionType.None)
        {
            return compressedData;
        }

        try
        {
            byte[] decompressedData;

            switch (compressionType)
            {
                case CompressionType.LZ4:
                    decompressedData = DecompressWithLZ4(compressedData, expectedSize);
                    break;
                    
                case CompressionType.GZIP:
                    decompressedData = DecompressWithGZip(compressedData);
                    break;
                    
                default:
                    _logger.LogWarning("Unsupported compression type: {Type}", compressionType);
                    return compressedData;
            }

            // Validate decompressed size if expected size provided
            if (expectedSize.HasValue && decompressedData.Length != expectedSize.Value)
            {
                throw new InvalidDataException($"Decompressed size mismatch: expected {expectedSize.Value}, got {decompressedData.Length}");
            }

            _logger.LogTrace("Block decompressed: {CompressedSize} -> {DecompressedSize} using {Type}",
                compressedData.Length, decompressedData.Length, compressionType);

            return decompressedData;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Decompression failed for {Type} compressed data", compressionType);
            throw;
        }
    }

    /// <summary>
    /// Compresses data using LZ4 algorithm (Syncthing's preferred method)
    /// </summary>
    private byte[] CompressWithLZ4(byte[] data)
    {
        // Use LZ4 high compression for better ratios (similar to Syncthing)
        var maxCompressedSize = LZ4Codec.MaximumOutputSize(data.Length);
        var compressedBuffer = new byte[maxCompressedSize];
        
        var compressedSize = LZ4Codec.Encode(
            data, 0, data.Length,
            compressedBuffer, 0, compressedBuffer.Length,
            LZ4Level.L09_HC); // High compression level
        
        if (compressedSize <= 0)
        {
            throw new InvalidOperationException("LZ4 compression failed");
        }
        
        // Trim buffer to actual compressed size
        var result = new byte[compressedSize];
        Array.Copy(compressedBuffer, 0, result, 0, compressedSize);
        return result;
    }

    /// <summary>
    /// Decompresses LZ4 compressed data
    /// </summary>
    private byte[] DecompressWithLZ4(byte[] compressedData, int? expectedSize)
    {
        // If we know the expected size, use it for better performance
        if (expectedSize.HasValue)
        {
            var result = new byte[expectedSize.Value];
            var decompressedSize = LZ4Codec.Decode(
                compressedData, 0, compressedData.Length,
                result, 0, result.Length);
                
            if (decompressedSize != expectedSize.Value)
            {
                throw new InvalidDataException($"LZ4 decompression size mismatch: expected {expectedSize.Value}, got {decompressedSize}");
            }
            
            return result;
        }
        else
        {
            // Fallback: try with reasonable buffer sizes
            var bufferSizes = new[] { compressedData.Length * 4, compressedData.Length * 8, compressedData.Length * 16 };
            
            foreach (var bufferSize in bufferSizes)
            {
                try
                {
                    var buffer = new byte[bufferSize];
                    var decompressedSize = LZ4Codec.Decode(
                        compressedData, 0, compressedData.Length,
                        buffer, 0, buffer.Length);
                        
                    if (decompressedSize > 0)
                    {
                        var result = new byte[decompressedSize];
                        Array.Copy(buffer, 0, result, 0, decompressedSize);
                        return result;
                    }
                }
                catch
                {
                    // Try next buffer size
                }
            }
            
            throw new InvalidDataException("LZ4 decompression failed with all attempted buffer sizes");
        }
    }

    /// <summary>
    /// Compresses data using GZIP algorithm (fallback method)
    /// </summary>
    private byte[] CompressWithGZip(byte[] data)
    {
        using var memoryStream = new MemoryStream();
        using var gzipStream = new GZipStream(memoryStream, CompressionLevel.Optimal);
        
        gzipStream.Write(data);
        gzipStream.Close();
        
        return memoryStream.ToArray();
    }

    /// <summary>
    /// Decompresses GZIP compressed data
    /// </summary>
    private byte[] DecompressWithGZip(byte[] compressedData)
    {
        using var compressedStream = new MemoryStream(compressedData);
        using var gzipStream = new GZipStream(compressedStream, CompressionMode.Decompress);
        using var resultStream = new MemoryStream();
        
        gzipStream.CopyTo(resultStream);
        return resultStream.ToArray();
    }

    /// <summary>
    /// Determines if a block should be compressed based on its characteristics
    /// </summary>
    /// <param name="data">Block data</param>
    /// <returns>True if block should be compressed</returns>
    public bool ShouldCompress(byte[] data)
    {
        if (data == null || data.Length < MinCompressionSize)
        {
            return false;
        }

        // Quick heuristic: check for patterns that indicate the data might be compressible
        // This is a simple approach - in practice, Syncthing does more sophisticated analysis
        var sample = Math.Min(data.Length, 256);
        var distinctBytes = data.Take(sample).Distinct().Count();
        var entropy = (float)distinctBytes / sample;
        
        // If entropy is low (many repeated bytes), compression is likely beneficial
        return entropy < 0.8f;
    }
}

/// <summary>
/// Compression types supported by the engine
/// </summary>
public enum CompressionType
{
    None = 0,    // No compression
    LZ4 = 1,     // LZ4 compression (Syncthing's preferred)
    GZIP = 2,    // GZIP compression (fallback)
    Auto = 99    // Automatically choose best compression
}