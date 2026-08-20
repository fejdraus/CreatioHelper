namespace CreatioHelper.Shared.Interfaces;

public interface IOutputWriter
{
    void WriteLine(string message);
    void Clear();

    void WriteSeparator(string title)
    {
        var bar = new string('═', 20);
        WriteLine($"{bar} {title} {bar}");
    }
}
