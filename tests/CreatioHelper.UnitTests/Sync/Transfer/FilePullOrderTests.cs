using CreatioHelper.Domain.Entities;
using CreatioHelper.Domain.Enums;
using CreatioHelper.Infrastructure.Services.Sync.Transfer;
using Xunit;

namespace CreatioHelper.UnitTests.Sync.Transfer;

public class FilePullOrderTests
{
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static List<FileMetadata> Sample() =>
    [
        new() { FileName = "c.txt", Size = 30, ModifiedTime = Base.AddDays(1) },
        new() { FileName = "a.txt", Size = 10, ModifiedTime = Base.AddDays(3) },
        new() { FileName = "b.txt", Size = 20, ModifiedTime = Base.AddDays(2) }
    ];

    [Fact]
    public void Alphabetic_SortsByName()
    {
        var result = FilePullOrder.Apply(Sample(), SyncPullOrder.Alphabetic);
        Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, result.Select(f => f.FileName));
    }

    [Fact]
    public void SmallestFirst_SortsBySizeAscending()
    {
        var result = FilePullOrder.Apply(Sample(), SyncPullOrder.SmallestFirst);
        Assert.Equal(new long[] { 10, 20, 30 }, result.Select(f => f.Size));
    }

    [Fact]
    public void LargestFirst_SortsBySizeDescending()
    {
        var result = FilePullOrder.Apply(Sample(), SyncPullOrder.LargestFirst);
        Assert.Equal(new long[] { 30, 20, 10 }, result.Select(f => f.Size));
    }

    [Fact]
    public void OldestFirst_SortsByModifiedAscending()
    {
        var result = FilePullOrder.Apply(Sample(), SyncPullOrder.OldestFirst);
        Assert.Equal(new[] { "c.txt", "b.txt", "a.txt" }, result.Select(f => f.FileName));
    }

    [Fact]
    public void NewestFirst_SortsByModifiedDescending()
    {
        var result = FilePullOrder.Apply(Sample(), SyncPullOrder.NewestFirst);
        Assert.Equal(new[] { "a.txt", "b.txt", "c.txt" }, result.Select(f => f.FileName));
    }

    [Fact]
    public void Random_KeepsEveryFileExactlyOnce()
    {
        var result = FilePullOrder.Apply(Sample(), SyncPullOrder.Random);
        Assert.Equal(3, result.Count);
        Assert.Equal(
            new[] { "a.txt", "b.txt", "c.txt" },
            result.Select(f => f.FileName).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void EmptyInput_ReturnsEmpty()
    {
        Assert.Empty(FilePullOrder.Apply(new List<FileMetadata>(), SyncPullOrder.Alphabetic));
        Assert.Empty(FilePullOrder.Apply(new List<FileMetadata>(), SyncPullOrder.Random));
    }
}
