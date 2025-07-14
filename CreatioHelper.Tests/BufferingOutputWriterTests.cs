using System;
using System.Collections.Generic;
using System.Globalization;
using CreatioHelper.Core;

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
}
