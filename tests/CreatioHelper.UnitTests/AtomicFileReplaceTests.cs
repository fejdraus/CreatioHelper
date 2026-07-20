using System.IO;
using Xunit;

namespace CreatioHelper.UnitTests;

public class AtomicFileReplaceTests
{
    private static string CreateDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "atomic_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }
    [Fact]
    public void MoveWithOverwrite_ReplacesAnExistingTarget()
    {
        var dir = CreateDir();
        var target = Path.Combine(dir, "file.txt");
        var temp = target + ".tmp";
        File.WriteAllText(target, "old");
        File.WriteAllText(temp, "new");
        File.Move(temp, target, overwrite: true);
        Assert.Equal("new", File.ReadAllText(target));
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void MoveWithOverwrite_LeavesTheTargetInPlace_WhenTheSourceIsLocked()
    {
        var dir = CreateDir();
        var target = Path.Combine(dir, "file.txt");
        var temp = target + ".tmp";
        File.WriteAllText(target, "old");
        File.WriteAllText(temp, "new");
        using (new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.ThrowsAny<IOException>(() => File.Move(temp, target, overwrite: true));
        }
        Assert.True(File.Exists(target));
        Assert.Equal("old", File.ReadAllText(target));
    }

    [Fact]
    public void DeleteThenMove_LosesTheTarget_WhenTheMoveFails()
    {
        var dir = CreateDir();
        var target = Path.Combine(dir, "file.txt");
        var temp = target + ".tmp";
        File.WriteAllText(target, "old");
        File.WriteAllText(temp, "new");
        using (new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            File.Delete(target);
            Assert.ThrowsAny<IOException>(() => File.Move(temp, target));
        }
        Assert.False(File.Exists(target));
    }
    [Fact]
    public void RenameAsideThenRestore_RecoversTheOriginal()
    {
        var dir = CreateDir();
        var current = Path.Combine(dir, "app.exe");
        var backup = current + ".bak";
        File.WriteAllText(current, "running");
        File.WriteAllText(backup, "stale backup");
        File.Move(current, backup, overwrite: true);
        Assert.False(File.Exists(current));

        File.Move(backup, current, overwrite: true);
        Assert.Equal("running", File.ReadAllText(current));
        Assert.False(File.Exists(backup));
    }
}