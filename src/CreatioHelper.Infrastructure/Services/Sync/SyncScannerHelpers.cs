using System.Security.Cryptography;
using CreatioHelper.Infrastructure.Services.Sync.Scanning;

namespace CreatioHelper.Infrastructure.Services.Sync;

internal static class SyncScannerHelpers
{
    public static ulong StringToShortId(string deviceIdHex)
    {
        try
        {
            var deviceId = Convert.FromHexString(deviceIdHex);
            return System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(
                deviceId.AsSpan(0, Math.Min(8, deviceId.Length)));
        }
        catch
        {
            return 0;
        }
    }

    public static int CalculateBlockSize(long fileSize)
    {
        const int desiredPerFileBlocks = 2000;
        var blockSizes = new[]
        {
            128 * 1024, 256 * 1024, 512 * 1024,
            1024 * 1024, 2048 * 1024, 4096 * 1024,
            8192 * 1024, 16384 * 1024
        };
        foreach (var size in blockSizes)
        {
            if (fileSize < desiredPerFileBlocks * size)
                return size;
        }
        return blockSizes[^1];
    }

    public static async Task<List<BepBlockInfo>> CalculateFileBlocksAsync(string filePath, int blockSize)
    {
        var blocks = new List<BepBlockInfo>();
        using var file = File.OpenRead(filePath);
        var buffer = new byte[blockSize];
        long offset = 0;

        while (offset < file.Length)
        {
            var bytesToRead = (int)Math.Min(blockSize, file.Length - offset);
            var bytesRead = await file.ReadAsync(buffer.AsMemory(0, bytesToRead));
            if (bytesRead > 0)
            {
                var blockData = buffer.AsSpan(0, bytesRead).ToArray();
                var hash = SHA256.HashData(blockData);
                blocks.Add(new BepBlockInfo
                {
                    Offset = offset,
                    Size = bytesRead,
                    Hash = hash
                });
                offset += bytesRead;
            }
        }
        return blocks;
    }

    public static Domain.Enums.SyncPullOrder ParsePullOrder(string order) =>
        Domain.Enums.SyncPullOrders.Parse(order);
}
