using System;
using System.IO;
using CreatioHelper.Infrastructure.Services.Sync.FileSystem;
using Xunit;

namespace CreatioHelper.UnitTests.FileSystem;

public class PathContainmentTests
{
    private static string Base => Path.Combine(Path.GetTempPath(), "chpc", "sync");

    [Fact]
    public void SiblingSharingThePrefixIsNotInside()
    {
        var sibling = Path.Combine(Path.GetTempPath(), "chpc", "syncbackup", "secrets.txt");

        Assert.False(PathContainment.IsInside(Base, sibling));
    }

    [Fact]
    public void FileDirectlyInsideIsInside()
    {
        Assert.True(PathContainment.IsInside(Base, Path.Combine(Base, "file.txt")));
    }

    [Fact]
    public void NestedFileIsInside()
    {
        Assert.True(PathContainment.IsInside(Base, Path.Combine(Base, "a", "b", "c.txt")));
    }

    [Fact]
    public void TheDirectoryItselfIsInside()
    {
        Assert.True(PathContainment.IsInside(Base, Base));
    }

    [Fact]
    public void ParentIsNotInside()
    {
        Assert.False(PathContainment.IsInside(Base, Path.Combine(Path.GetTempPath(), "chpc")));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("a/b/../../../escape.txt")]
    public void ResolveRefusesToClimbOut(string relative)
    {
        Assert.Null(PathContainment.Resolve(Base, relative));
    }

    [Fact]
    public void ResolveRefusesAnAbsolutePath()
    {
        var absolute = OperatingSystem.IsWindows() ? @"C:\Windows\win.ini" : "/etc/passwd";

        Assert.Null(PathContainment.Resolve(Base, absolute));
    }

    [Fact]
    public void ResolveRefusesEmptyInput()
    {
        Assert.Null(PathContainment.Resolve(Base, string.Empty));
    }

    [Fact]
    public void ResolveReturnsTheFullPathForSomethingInside()
    {
        var resolved = PathContainment.Resolve(Base, Path.Combine("a", "b.txt"));

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(Path.Combine(Base, "a", "b.txt")), resolved);
    }

    [Fact]
    public void ResolveNormalisesHarmlessRelativeSegments()
    {
        var resolved = PathContainment.Resolve(Base, Path.Combine("a", "..", "b.txt"));

        Assert.Equal(Path.GetFullPath(Path.Combine(Base, "b.txt")), resolved);
    }

    [Fact]
    public void EmptyArgumentsAreNotInside()
    {
        Assert.False(PathContainment.IsInside(string.Empty, Base));
        Assert.False(PathContainment.IsInside(Base, string.Empty));
    }

    [Fact]
    public void TrailingSeparatorOnTheBaseChangesNothing()
    {
        var withSeparator = Base + Path.DirectorySeparatorChar;

        Assert.True(PathContainment.IsInside(withSeparator, Path.Combine(Base, "file.txt")));
        Assert.False(PathContainment.IsInside(
            withSeparator,
            Path.Combine(Path.GetTempPath(), "chpc", "syncbackup", "file.txt")));
    }
}
