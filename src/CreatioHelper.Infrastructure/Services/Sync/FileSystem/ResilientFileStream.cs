namespace CreatioHelper.Infrastructure.Services.Sync.FileSystem;

/// <summary>
/// Opens files with overlapped (asynchronous) I/O, falling back to a plain handle
/// when the volume refuses it.
///
/// Some volumes - virtual disks and network redirectors in particular - reject
/// overlapped handles sporadically under load, surfacing as
/// "invalid attempt to access a memory address". The same file opens without
/// trouble on a synchronous handle, so the fallback keeps the operation alive
/// instead of failing the file.
/// </summary>
public static class ResilientFileStream
{
    public static FileStream Open(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        int bufferSize,
        FileOptions options,
        Action<Exception>? onFallback = null)
    {
        try
        {
            return new FileStream(path, mode, access, share, bufferSize, options);
        }
        catch (IOException ex)
        {
            onFallback?.Invoke(ex);
            return new FileStream(path, mode, access, share, bufferSize, options & ~FileOptions.Asynchronous);
        }
    }
}
