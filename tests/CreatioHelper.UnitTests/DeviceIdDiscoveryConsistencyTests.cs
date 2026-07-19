using System.Collections.Generic;
using CreatioHelper.Infrastructure.Services.Sync;
using Xunit;

namespace CreatioHelper.UnitTests;

public class DeviceIdDiscoveryConsistencyTests
{
    private static byte[] SampleHash()
    {
        var bytes = new byte[32];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)((i * 7) + 3);
        }
        return bytes;
    }

    [Fact]
    public void Generator_And_DiscoveryProtocol_ProduceSameFormat()
    {
        var hash = SampleHash();

        Assert.Equal(
            DiscoveryProtocol.FormatDeviceId(hash),
            DeviceIdGenerator.FormatDeviceId(hash));
    }

    [Fact]
    public void GeneratedId_PassesDiscoveryAnnouncePath()
    {
        var id = DeviceIdGenerator.FormatDeviceId(SampleHash());

        var exception = Record.Exception(() =>
            DiscoveryProtocol.CreateAnnouncePacket(
                id,
                new List<string> { "tcp://127.0.0.1:22000" },
                12345L));

        Assert.Null(exception);
    }
}
