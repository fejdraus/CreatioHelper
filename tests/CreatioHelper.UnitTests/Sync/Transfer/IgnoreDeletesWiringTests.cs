using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync.Transfer;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CreatioHelper.UnitTests.Sync.Transfer;

public class IgnoreDeletesWiringTests
{
    private static SyncFolder FolderWithIgnoreDelete(bool ignoreDelete)
    {
        var folder = SyncFolder.Create(new SyncFolderSettings
        {
            Id = "f1",
            Label = "F1",
            Path = "/data",
            IgnoreDelete = ignoreDelete
        });
        return folder;
    }

    [Fact]
    public void IgnoreDeleteFromConfig_ReachesTheEntity()
    {
        var xml = new ConfigXmlFolder { Id = "f1", Label = "F1", Path = "/data", IgnoreDelete = true };
        var folder = SyncFolder.Create(xml.ToSyncFolderSettings());
        Assert.True(folder.IgnoreDelete);
    }

    [Fact]
    public void HandlerBlocksDelete_WhenIgnoreDeleteEnabled()
    {
        var handler = new IgnoreDeletesHandler(NullLogger<IgnoreDeletesHandler>.Instance);
        var folder = FolderWithIgnoreDelete(true);

        Assert.False(handler.ShouldApplyDelete(folder, "docs/report.txt", "DEVICE-A"));
    }

    [Fact]
    public void HandlerAllowsDelete_WhenIgnoreDeleteDisabled()
    {
        var handler = new IgnoreDeletesHandler(NullLogger<IgnoreDeletesHandler>.Instance);
        var folder = FolderWithIgnoreDelete(false);

        Assert.True(handler.ShouldApplyDelete(folder, "docs/report.txt", "DEVICE-A"));
    }

    [Fact]
    public async Task IgnoredDeletes_AreRecorded()
    {
        var handler = new IgnoreDeletesHandler(NullLogger<IgnoreDeletesHandler>.Instance);
        var folder = FolderWithIgnoreDelete(true);

        await handler.RecordIgnoredDeleteAsync(folder, "docs/report.txt", "DEVICE-A", CancellationToken.None);

        var stats = handler.GetStats(folder.Id);
        Assert.True(stats.TotalIgnoredDeletes >= 1);
    }
}
