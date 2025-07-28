namespace CreatioHelper.Shared.Logging;

public static class OutputWriterHandlers
{
    public static Action<string> WriteAction { get; set; } = line => Console.WriteLine(line);
    public static Action ClearAction { get; set; } = () => Console.Clear();
}
