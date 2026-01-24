namespace CreatioHelper.Infrastructure.Services.FileSystem;

public interface IJunctionPointHandler
{
    bool IsJunctionPoint(string path);
    string? GetJunctionTarget(string path);
    bool IsSymlink(string path);
    bool IsReparsePoint(string path);
}
