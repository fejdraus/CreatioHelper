using System.Collections.Generic;
using CreatioHelper.Domain.Entities;
using Xunit;

namespace CreatioHelper.UnitTests;

public class ServerSyncStateTests
{
    private static ServerInfo TwoFolderServer() => new()
    {
        Name = "node-1",
        SyncthingDeviceId = "ABCDEFG-HIJKLMN",
        SyncthingFolderIds = new List<string> { "webapp", "bin" }
    };

    [Fact]
    public void DescribeSyncState_SaysUpToDate_WhenEveryFolderIsComplete()
    {
        var server = TwoFolderServer();
        server.UpdateFolderCompletion("webapp", 100, 0, 0, "valid");
        server.UpdateFolderCompletion("bin", 100, 0, 0, "valid");

        Assert.Equal("✅ Up to Date", server.DescribeSyncState());
        Assert.True(server.AreAllFoldersSynced());
    }

    [Fact]
    public void DescribeSyncState_SaysSyncing_WhenAFolderStillNeedsData()
    {
        var server = TwoFolderServer();
        server.UpdateFolderCompletion("webapp", 100, 0, 0, "valid");
        server.UpdateFolderCompletion("bin", 88.2, 1024, 3, "valid");

        Assert.Equal($"🔄 Syncing ({94.1:F1}%)", server.DescribeSyncState());
        Assert.False(server.AreAllFoldersSynced());
    }

    [Fact]
    public void AFolderStateEvent_DoesNotDisturbCompletion()
    {
        var server = TwoFolderServer();
        server.UpdateFolderCompletion("webapp", 100, 0, 0, "valid");
        server.UpdateFolderCompletion("bin", 100, 0, 0, "valid");

        server.UpdateFolderState("bin", "scanning");

        Assert.Equal(0, server.SyncthingNeedBytes);
        Assert.Equal(0, server.SyncthingNeedItems);
        Assert.Equal(100, server.SyncthingCompletionPercent);
        Assert.Equal("✅ Up to Date", server.DescribeSyncState());
    }

    [Fact]
    public void AFinishedItemEvent_DoesNotDisturbCompletion()
    {
        var server = TwoFolderServer();
        server.UpdateFolderCompletion("webapp", 100, 0, 0, "valid");
        server.UpdateFolderCompletion("bin", 100, 0, 0, "valid");

        server.UpdateFolderLastSyncedFile("bin", "Terrasoft.Configuration.dll");

        Assert.Equal(0, server.SyncthingNeedBytes);
        Assert.Equal(100, server.SyncthingCompletionPercent);
        Assert.Equal("✅ Up to Date", server.DescribeSyncState());
        Assert.Equal("Terrasoft.Configuration.dll", server.SyncthingLastSyncedFile);
    }

    [Fact]
    public void AFolderStateEvent_DoesNotSpreadOneFoldersShortfallOntoAnother()
    {
        var server = TwoFolderServer();
        server.UpdateFolderCompletion("webapp", 100, 0, 0, "valid");
        server.UpdateFolderCompletion("bin", 50, 2048, 4, "valid");

        server.UpdateFolderState("webapp", "idle");

        var states = server.GetFolderSyncStates();
        Assert.Equal(100, states["webapp"].CompletionPercent);
        Assert.Equal(0, states["webapp"].NeedBytes);
        Assert.Equal(50, states["bin"].CompletionPercent);
        Assert.Equal(2048, states["bin"].NeedBytes);
        Assert.Equal(2048, server.SyncthingNeedBytes);
    }

    [Fact]
    public void DescribeSyncState_ReportsTheRemoteSide_BeforeCountingBytes()
    {
        var server = TwoFolderServer();
        server.UpdateFolderCompletion("webapp", 100, 0, 0, "valid");
        server.UpdateFolderCompletion("bin", 100, 0, 0, "paused");

        Assert.Equal("⏸️ Paused", server.DescribeSyncState());
    }

    [Fact]
    public void DescribeSyncState_SaysNotConfigured_WithoutADevice()
    {
        var server = new ServerInfo { Name = "node-1" };

        Assert.Equal("⚙️ Not Configured", server.DescribeSyncState());
    }

    [Fact]
    public void AFolderIsOnlyFullySynced_WhenTheRemoteSideIsValid()
    {
        var server = TwoFolderServer();
        server.UpdateFolderCompletion("webapp", 100, 0, 0, "valid");
        server.UpdateFolderCompletion("bin", 100, 0, 0, "unknown");

        Assert.False(server.AreAllFoldersSynced());

        server.UpdateFolderCompletion("bin", 100, 0, 0, "valid");

        Assert.True(server.AreAllFoldersSynced());
    }
}
