using System;
using CreatioHelper.Domain.Entities;
using CreatioHelper.Infrastructure.Services.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CreatioHelper.UnitTests;

public class ConflictResolutionCausalityTests
{
    private static ConflictResolutionEngine NewEngine() =>
        new(NullLogger<ConflictResolutionEngine>.Instance);

    private static FileMetadata File(BepVectorClock version, DateTime modified, long size = 10, byte[]? hash = null) =>
        new()
        {
            FileName = "file.txt",
            DeviceId = "dev",
            VersionVector = version,
            ModifiedTime = modified,
            Size = size,
            Hash = hash ?? new byte[] { 1, 2, 3 },
        };

    private static BepVectorClock Clock(string device, params (string dev, int _)[] _unused)
    {
        var c = new BepVectorClock();
        c.Increment(BepVectorClock.ShortIdFromString(device));
        return c;
    }

    [Fact]
    public void SendReceive_CausallyNewerLocal_WinsEvenWithOlderMtime()
    {
        // Local is causally newer, but has an OLDER modification time than remote.
        // Causal ordering must win — the old mtime-based tie-break must not apply.
        var baseClock = new BepVectorClock();
        baseClock.Increment(BepVectorClock.ShortIdFromString("dev-a"));

        var localClock = baseClock.Copy();
        localClock.Increment(BepVectorClock.ShortIdFromString("dev-a")); // strictly newer

        var local = File(localClock, modified: new DateTime(2020, 1, 1), size: 5, hash: new byte[] { 9 });
        var remote = File(baseClock, modified: new DateTime(2030, 1, 1), size: 999, hash: new byte[] { 7 });

        var resolution = NewEngine().ResolveConflictByFolderType(local, remote, SyncFolderType.SendReceive);

        Assert.Equal(ConflictAction.UseLocal, resolution.Action);
        Assert.Same(local, resolution.Winner);
    }

    [Fact]
    public void SendReceive_CausallyNewerRemote_Wins()
    {
        var baseClock = new BepVectorClock();
        baseClock.Increment(BepVectorClock.ShortIdFromString("dev-a"));

        var remoteClock = baseClock.Copy();
        remoteClock.Increment(BepVectorClock.ShortIdFromString("dev-a"));

        var local = File(baseClock, modified: new DateTime(2030, 1, 1));
        var remote = File(remoteClock, modified: new DateTime(2020, 1, 1));

        var resolution = NewEngine().ResolveConflictByFolderType(local, remote, SyncFolderType.SendReceive);

        Assert.Equal(ConflictAction.UseRemote, resolution.Action);
        Assert.Same(remote, resolution.Winner);
    }

    [Fact]
    public void SendReceive_ConcurrentEdits_CreatesConflictCopy()
    {
        var localClock = new BepVectorClock();
        localClock.Increment(BepVectorClock.ShortIdFromString("dev-a"));
        var remoteClock = new BepVectorClock();
        remoteClock.Increment(BepVectorClock.ShortIdFromString("dev-b"));

        var local = File(localClock, modified: new DateTime(2025, 1, 1), hash: new byte[] { 1 });
        var remote = File(remoteClock, modified: new DateTime(2025, 6, 1), hash: new byte[] { 2 });

        var resolution = NewEngine().ResolveConflictByFolderType(local, remote, SyncFolderType.SendReceive);

        Assert.Equal(ConflictAction.CreateConflictCopy, resolution.Action);
    }

    [Fact]
    public void ReceiveOnly_AlwaysPrefersRemote_RegardlessOfCausality()
    {
        var localClock = new BepVectorClock();
        localClock.Increment(BepVectorClock.ShortIdFromString("dev-a"));
        localClock.Increment(BepVectorClock.ShortIdFromString("dev-a")); // local causally newer

        var remoteClock = new BepVectorClock();
        remoteClock.Increment(BepVectorClock.ShortIdFromString("dev-a"));

        var local = File(localClock, modified: new DateTime(2030, 1, 1));
        var remote = File(remoteClock, modified: new DateTime(2020, 1, 1));

        var resolution = NewEngine().ResolveConflictByFolderType(local, remote, SyncFolderType.ReceiveOnly);

        Assert.Equal(ConflictAction.UseRemote, resolution.Action);
    }

    [Fact]
    public void Detection_And_Resolution_Agree_OnConcurrency()
    {
        var engine = NewEngine();
        var localClock = new BepVectorClock();
        localClock.Increment(BepVectorClock.ShortIdFromString("dev-a"));
        var remoteClock = new BepVectorClock();
        remoteClock.Increment(BepVectorClock.ShortIdFromString("dev-b"));

        var local = File(localClock, modified: new DateTime(2025, 1, 1), hash: new byte[] { 1 });
        var remote = File(remoteClock, modified: new DateTime(2025, 6, 1), hash: new byte[] { 2 });

        // IsInConflict (detection) and ResolveBidirectionalConflict (resolution) must be
        // consistent: concurrent detected -> conflict copy produced.
        Assert.True(engine.IsInConflict(local, remote));
        Assert.Equal(ConflictAction.CreateConflictCopy,
            engine.ResolveConflictByFolderType(local, remote, SyncFolderType.SendReceive).Action);
    }
}
