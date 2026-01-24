namespace CreatioHelper.Infrastructure.Services.FileSystem;

public class SymlinkLoopDetector
{
    private readonly HashSet<string> _visitedRealPaths = new(StringComparer.OrdinalIgnoreCase);

    public bool WouldCreateLoop(string path)
    {
        var realPath = Path.GetFullPath(path);
        return !_visitedRealPaths.Add(realPath);
    }

    public void Reset() => _visitedRealPaths.Clear();
}
