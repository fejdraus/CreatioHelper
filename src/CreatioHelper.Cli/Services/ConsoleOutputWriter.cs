using CreatioHelper.Shared.Interfaces;

namespace CreatioHelper.Cli.Services;

public class ConsoleOutputWriter : IOutputWriter
{
    private readonly bool _useColor;
    private readonly bool _quiet;

    public ConsoleOutputWriter(bool useColor, bool quiet)
    {
        _useColor = useColor;
        _quiet = quiet;
    }

    public void WriteLine(string message)
    {
        if (_quiet && !message.Contains("[ERROR]"))
        {
            return;
        }

        if (!_useColor)
        {
            Console.WriteLine(message);
            return;
        }

        var previous = Console.ForegroundColor;
        try
        {
            if (message.Contains("[ERROR]"))
            {
                Console.ForegroundColor = ConsoleColor.Red;
            }
            else if (message.Contains("[WARN]") || message.Contains("[WARNING]"))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
            }
            else if (message.Contains("[OK]") || message.Contains("[SUCCESS]"))
            {
                Console.ForegroundColor = ConsoleColor.Green;
            }
            else if (message.Contains("[INFO]"))
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
            }

            Console.WriteLine(message);
        }
        finally
        {
            Console.ForegroundColor = previous;
        }
    }

    public void Clear()
    {
    }
}
