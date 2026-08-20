using System.Collections.Generic;
using CreatioHelper.Infrastructure.Logging;
using CreatioHelper.Shared.Interfaces;
using Xunit;

namespace CreatioHelper.UnitTests;

public class OutputSeparatorTests
{
    private sealed class RecordingWriter : IOutputWriter
    {
        public List<string> Lines { get; } = new();
        public int ClearCount { get; private set; }

        public void WriteLine(string message) => Lines.Add(message);
        public void Clear() => ClearCount++;
    }

    [Fact]
    public void SeparatorIsWrittenAsASingleLine()
    {
        IOutputWriter writer = new RecordingWriter();

        writer.WriteSeparator("Deployment");

        Assert.Single(((RecordingWriter)writer).Lines);
    }

    [Fact]
    public void SeparatorCarriesTheTitle()
    {
        var recorder = new RecordingWriter();
        IOutputWriter writer = recorder;

        writer.WriteSeparator("Deployment");

        Assert.Contains("Deployment", recorder.Lines[0]);
    }

    [Fact]
    public void SeparatorDoesNotClearTheLog()
    {
        var recorder = new RecordingWriter();
        IOutputWriter writer = recorder;

        writer.WriteSeparator("Deployment");

        Assert.Equal(0, recorder.ClearCount);
    }

    [Fact]
    public void EarlierOutputSurvivesASeparator()
    {
        var recorder = new RecordingWriter();
        IOutputWriter writer = recorder;

        writer.WriteLine("first operation finished");
        writer.WriteSeparator("Deployment");
        writer.WriteLine("second operation started");

        Assert.Equal(3, recorder.Lines.Count);
        Assert.Equal("first operation finished", recorder.Lines[0]);
    }

    [Fact]
    public void TheBufferingWriterStampsTheSeparatorLikeAnyOtherLine()
    {
        var lines = new List<string>();
        IOutputWriter writer = new BufferingOutputWriter(lines.Add);

        writer.WriteSeparator("Deployment");

        Assert.Single(lines);
        Assert.Contains("Deployment", lines[0]);
    }
}
