using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CreatioHelper.Infrastructure.Services.Workspace;
using CreatioHelper.Shared.Interfaces;
using Moq;
using Xunit;

namespace CreatioHelper.UnitTests;

public class QuartzConfigWriteTests
{
    private static string WriteConfig(bool isActive)
    {
        var path = Path.Combine(Path.GetTempPath(), "quartz_" + Path.GetRandomFileName() + ".config");
        File.WriteAllText(path,
            "<?xml version=\"1.0\"?>\n" +
            "<configuration>\n" +
            "  <quartzConfig defaultScheduler=\"BPMonlineQuartzScheduler\">\n" +
            $"    <quartz isActive=\"{isActive.ToString().ToLowerInvariant()}\" />\n" +
            "  </quartzConfig>\n" +
            "</configuration>\n");
        return path;
    }

    private static WorkspacePreparer Preparer() => new(Mock.Of<IOutputWriter>());

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void UpdateOutConfig_LeavesTheFileAlone_WhenTheValueAlreadyMatches()
    {
        var path = WriteConfig(true);
        var before = File.GetLastWriteTimeUtc(path);

        Preparer().UpdateOutConfig(path, true);

        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void UpdateOutConfig_SucceedsWhileAnotherProcessHoldsTheFile_WhenTheValueAlreadyMatches()
    {
        var path = WriteConfig(true);
        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        Preparer().UpdateOutConfig(path, true);

        Assert.Contains("isActive=\"true\"", ReadShared(path));
    }

    [Fact]
    public void UpdateOutConfig_StillWrites_WhenTheValueDiffers()
    {
        var path = WriteConfig(false);

        Preparer().UpdateOutConfig(path, true);

        Assert.Contains("isActive=\"true\"", ReadShared(path));
    }

    [SkippableFact]
    public void UpdateOutConfig_GivesUpWithAClearMessage_WhenTheHolderNeverLetsGo()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "File sharing rules differ outside Windows.");

        var path = WriteConfig(false);
        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var error = Assert.Throws<IOException>(() => Preparer().UpdateOutConfig(path, true));

        Assert.Contains("after 5 attempts", error.Message);
        Assert.Contains(path, error.Message);
    }

    [SkippableFact]
    public void UpdateOutConfig_RidesOutABriefHold_LikeTheOneIisTakesWhileReadingTheFile()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "File sharing rules differ outside Windows.");

        var path = WriteConfig(false);
        var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var released = new ManualResetEventSlim(false);

        var releasing = Task.Run(() =>
        {
            Thread.Sleep(150);
            holder.Dispose();
            released.Set();
        });

        Preparer().UpdateOutConfig(path, true);

        Assert.True(released.IsSet);
        Assert.Contains("isActive=\"true\"", ReadShared(path));
        releasing.Wait();
    }

    [Fact]
    public void UpdateOutConfig_WritesWhileAPermissiveHolderKeepsTheFileOpen()
    {
        var path = WriteConfig(false);
        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        Preparer().UpdateOutConfig(path, true);

        Assert.Contains("isActive=\"true\"", ReadShared(path));
    }

    [Fact]
    public void UpdateOutConfig_ReadsWhileAnotherProcessHoldsTheFileForWriting()
    {
        var path = WriteConfig(true);
        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Preparer().UpdateOutConfig(path, true);

        Assert.Contains("isActive=\"true\"", ReadShared(path));
    }

    [Fact]
    public void UpdateOutConfig_KeepsTheDeclarationAndLeavesNoTrailingBytes()
    {
        var path = WriteConfig(false);
        var sizeBefore = new FileInfo(path).Length;

        Preparer().UpdateOutConfig(path, true);

        var text = ReadShared(path);
        Assert.StartsWith("<?xml version=\"1.0\"?>", text);
        Assert.EndsWith("</configuration>", text.TrimEnd());
        Assert.True(new FileInfo(path).Length <= sizeBefore + 8);
        var reloaded = new System.Xml.XmlDocument();
        reloaded.Load(path);
        Assert.Equal("true", ((System.Xml.XmlElement)reloaded.SelectSingleNode("//quartz")!).GetAttribute("isActive"));
    }

    [Fact]
    public void UpdateOutConfig_ShrinksTheFile_WhenTheNewContentIsShorter()
    {
        var path = WriteConfig(true);
        Preparer().UpdateOutConfig(path, false);

        var text = ReadShared(path);
        Assert.Contains("isActive=\"false\"", text);
        Assert.DoesNotContain("falsee", text);
        Assert.EndsWith("</configuration>", text.TrimEnd());
    }

    [Fact]
    public void UpdateOutConfig_LeavesTheFileAlone_WhenThereIsNoQuartzNode()
    {
        var path = Path.Combine(Path.GetTempPath(), "quartz_" + Path.GetRandomFileName() + ".config");
        File.WriteAllText(path, "<?xml version=\"1.0\"?>\n<configuration>\n</configuration>\n");
        var before = File.GetLastWriteTimeUtc(path);

        Preparer().UpdateOutConfig(path, true);

        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }
}
