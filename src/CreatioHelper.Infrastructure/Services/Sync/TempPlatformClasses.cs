namespace CreatioHelper.Infrastructure.Services.Sync;

/// <summary>
/// Temporary placeholder classes for Protobuf-generated PlatformData structures
/// These will be replaced by proper protobuf generation once compilation issues are resolved
/// </summary>

public class PlatformData
{
    public WindowsData? Windows { get; set; }
    public UnixData? Unix { get; set; }
    public XattrData? Linux { get; set; }
    public XattrData? Darwin { get; set; }
    public XattrData? Freebsd { get; set; }
}

public class WindowsData
{
    public string OwnerName { get; set; } = string.Empty;
    public bool OwnerIsGroup { get; set; }
}

public class UnixData
{
    public string OwnerName { get; set; } = string.Empty;
    public string GroupName { get; set; } = string.Empty;
    public int Uid { get; set; }
    public int Gid { get; set; }
}

public class XattrData
{
    public List<Xattr> Xattrs { get; set; } = new List<Xattr>();
}

public class Xattr
{
    public string Name { get; set; } = string.Empty;
    public byte[] Value { get; set; } = Array.Empty<byte>();
}