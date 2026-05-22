namespace CreatioHelper.Shared.Interfaces;

public interface IOutputWriter
{
    event Action? Cleared;
    void WriteLine(string message);
    void Clear();
}
