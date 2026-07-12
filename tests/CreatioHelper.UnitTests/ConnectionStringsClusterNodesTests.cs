using System.Linq;
using CreatioHelper.ViewModels;
using Xunit;

namespace CreatioHelper.UnitTests;

public class ConnectionStringsClusterNodesTests
{
    [Fact]
    public void ParseClusterNodes_SplitsHostsAndPorts()
    {
        var nodes = ConnectionStringsViewModel.ParseClusterNodes("10.0.0.1:6379,10.0.0.2:6380, node3 ,[::1]:6381");

        Assert.Equal(4, nodes.Count);
        Assert.Equal("10.0.0.1", nodes[0].Host);
        Assert.Equal(6379, nodes[0].Port);
        Assert.Equal("10.0.0.2", nodes[1].Host);
        Assert.Equal(6380, nodes[1].Port);
        Assert.Equal("node3", nodes[2].Host);
        Assert.Null(nodes[2].Port);
        Assert.Equal("[::1]", nodes[3].Host);
        Assert.Equal(6381, nodes[3].Port);
    }

    [Fact]
    public void FormatClusterNodes_SkipsEmptyHostsAndZeroPorts()
    {
        var nodes = ConnectionStringsViewModel.ParseClusterNodes("n1:6379,n2");
        nodes.Add(new RedisClusterNodeViewModel { Host = "", Port = 6379 });
        nodes.Add(new RedisClusterNodeViewModel { Host = "n4", Port = 6382 });

        var result = ConnectionStringsViewModel.FormatClusterNodes(nodes);
        Assert.Equal("n1:6379,n2,n4:6382", result);
    }

    [Fact]
    public void ParseAndFormat_RoundTripsOfficialSample()
    {
        const string hosts = "10.1.0.1:7000,10.1.0.2:7001,10.1.0.3:7002,10.1.0.4:7003,10.1.0.5:7004,10.1.0.6:7005";
        var nodes = ConnectionStringsViewModel.ParseClusterNodes(hosts);
        Assert.Equal(6, nodes.Count);
        Assert.Equal(hosts, ConnectionStringsViewModel.FormatClusterNodes(nodes));
    }
}
