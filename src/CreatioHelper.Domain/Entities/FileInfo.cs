using CreatioHelper.Domain.Common;

namespace CreatioHelper.Domain.Entities;

/// <summary>
/// Represents file metadata for synchronization (based on Syncthing FileInfo concept)
/// </summary>
public class SyncFileInfo : Entity
{
    public string FolderId { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string RelativePath { get; private set; } = string.Empty;
    public long Size { get; private set; }
    public DateTime ModifiedTime { get; private set; }
    public string Hash { get; private set; } = string.Empty;
    public List<BlockInfo> Blocks { get; private set; } = new();
    public FileType Type { get; private set; } = FileType.Regular;
    public int Permissions { get; private set; }
    public bool IsDeleted { get; private set; }
    public bool IsInvalid { get; private set; }
    public bool IsSymlink { get; private set; }
    public string? SymlinkTarget { get; private set; }
    public VectorClock Vector { get; private set; } = new();
    public string Platform { get; private set; } = string.Empty;
    public long Sequence { get; private set; }

    private SyncFileInfo() { } // For EF Core

    public SyncFileInfo(string folderId, string name, string relativePath, long size, DateTime modifiedTime)
    {
        FolderId = folderId;
        Name = name;
        RelativePath = relativePath;
        Size = size;
        ModifiedTime = modifiedTime;
        Platform = Environment.OSVersion.Platform.ToString();
    }

    public void UpdateHash(string hash)
    {
        Hash = hash;
    }

    public void SetBlocks(List<BlockInfo> blocks)
    {
        Blocks = blocks;
    }

    public void MarkAsDeleted()
    {
        IsDeleted = true;
        Size = 0;
        Blocks.Clear();
    }

    public void MarkAsInvalid()
    {
        IsInvalid = true;
    }

    public void SetSymlink(string target)
    {
        IsSymlink = true;
        SymlinkTarget = target;
        Type = FileType.Symlink;
    }

    public void UpdateVector(VectorClock vector)
    {
        Vector = vector;
    }

    public void SetSequence(long sequence)
    {
        Sequence = sequence;
    }
}

public class BlockInfo
{
    public long Offset { get; set; }
    public int Size { get; set; }
    public string Hash { get; set; } = string.Empty;
    public int WeakHash { get; set; }

    public BlockInfo(long offset, int size, string hash, int weakHash = 0)
    {
        Offset = offset;
        Size = size;
        Hash = hash;
        WeakHash = weakHash;
    }
}

public class VectorClock
{
    public Dictionary<string, long> Counters { get; set; } = new();

    public void Update(string deviceId, long counter)
    {
        Counters[deviceId] = Math.Max(Counters.GetValueOrDefault(deviceId), counter);
    }

    public void Increment(string deviceId)
    {
        var currentValue = Counters.GetValueOrDefault(deviceId);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var newValue = Math.Max(currentValue + 1, timestamp);
        Counters[deviceId] = newValue;
    }

    public void Merge(VectorClock other)
    {
        foreach (var (deviceId, value) in other.Counters)
        {
            Counters[deviceId] = Math.Max(Counters.GetValueOrDefault(deviceId), value);
        }
    }

    public long GetCounter(string deviceId)
    {
        return Counters.TryGetValue(deviceId, out var counter) ? counter : 0;
    }

    public bool IsNewerThan(VectorClock other)
    {
        var allDevices = Counters.Keys.Union(other.Counters.Keys).ToHashSet();
        
        bool thisNewer = false;
        bool otherNewer = false;

        foreach (var deviceId in allDevices)
        {
            var thisValue = GetCounter(deviceId);
            var otherValue = other.GetCounter(deviceId);

            if (thisValue > otherValue)
            {
                thisNewer = true;
            }
            else if (otherValue > thisValue)
            {
                otherNewer = true;
            }
        }

        return thisNewer && !otherNewer;
    }

    public bool IsConcurrentWith(VectorClock other)
    {
        var allDevices = Counters.Keys.Union(other.Counters.Keys).ToHashSet();
        
        bool thisNewer = false;
        bool otherNewer = false;

        foreach (var deviceId in allDevices)
        {
            var thisValue = GetCounter(deviceId);
            var otherValue = other.GetCounter(deviceId);

            if (thisValue > otherValue)
            {
                thisNewer = true;
            }
            else if (otherValue > thisValue)
            {
                otherNewer = true;
            }
        }

        return thisNewer && otherNewer; // Conflict situation
    }
}

public enum FileType
{
    Regular,
    Directory,
    Symlink,
    SymlinkFile,
    SymlinkDirectory
}