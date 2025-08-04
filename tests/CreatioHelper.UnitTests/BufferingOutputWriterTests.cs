using System.Globalization;
using CreatioHelper.Infrastructure.Logging;

namespace CreatioHelper.Tests;

public class BufferingOutputWriterTests
{
    [Fact]
    public void WriteLine_AddsTimestampedLine()
    {
        var lines = new List<string>();
        var writer = new BufferingOutputWriter(lines.Add);
        writer.WriteLine("Hello");

        Assert.Single(lines);
        var line = lines[0];
        Assert.EndsWith(" Hello", line);
        var firstSpace = line.IndexOf(' ');
        Assert.True(DateTime.TryParse(line[..firstSpace], CultureInfo.CurrentCulture, DateTimeStyles.None, out _));
    }

    [Fact]
    public void Clear_InvokesAction()
    {
        bool cleared = false;
        var writer = new BufferingOutputWriter(_ => { }, () => cleared = true);

        writer.Clear();

        Assert.True(cleared);
    }
}
