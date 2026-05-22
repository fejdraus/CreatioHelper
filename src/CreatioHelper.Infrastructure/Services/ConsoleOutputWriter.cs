using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.Infrastructure.Services;

public class ConsoleOutputWriter : IOutputWriter
{
    public event Action? Cleared;
    public void WriteLine(string message) => Console.WriteLine(message);
    public void Clear() { Console.Clear(); Cleared?.Invoke(); }
}
