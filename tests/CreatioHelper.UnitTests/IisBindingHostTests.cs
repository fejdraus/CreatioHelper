using CreatioHelper.Domain.Entities;
using Xunit;

namespace CreatioHelper.UnitTests;

public class IisBindingHostTests
{
    private static (string ip, string port, string host) Parse(string bindingInformation)
    {
        var parts = bindingInformation.Split(':');
        return (
            parts.Length > 0 ? parts[0] : "",
            parts.Length > 1 ? parts[1] : "",
            parts.Length > 2 ? parts[2] : "");
    }

    private static IisSiteInfo SiteFrom(string bindingInformation, string protocol = "https")
    {
        var (ip, port, host) = Parse(bindingInformation);
        return new IisSiteInfo
        {
            Name = "Creatio815",
            Protocol = protocol,
            Port = port,
            HostName = host,
            IpAddress = ip
        };
    }

    private static string ResolveHost(IisSiteInfo site)
    {
        if (!string.IsNullOrWhiteSpace(site.HostName))
        {
            return site.HostName;
        }

        var ip = site.IpAddress?.Trim() ?? string.Empty;
        if (ip.Length == 0 || ip == "*" || ip == "0.0.0.0" || ip == "::")
        {
            return "localhost";
        }

        return ip.Contains(':') ? $"[{ip}]" : ip;
    }

    [Fact]
    public void BindingOnASingleAddressIsNotProbedThroughLocalhost()
    {
        var site = SiteFrom("10.70.0.143:443:");

        Assert.Equal("10.70.0.143", ResolveHost(site));
        Assert.Equal("443", site.Port);
        Assert.Equal("https", site.Protocol);
    }

    [Fact]
    public void BindingOnEveryAddressUsesLocalhost()
    {
        Assert.Equal("localhost", ResolveHost(SiteFrom("*:443:")));
    }

    [Fact]
    public void UnspecifiedIpv4AddressUsesLocalhost()
    {
        Assert.Equal("localhost", ResolveHost(SiteFrom("0.0.0.0:8080:", "http")));
    }

    [Fact]
    public void HostHeaderWinsOverTheAddress()
    {
        Assert.Equal("crm.example.com", ResolveHost(SiteFrom("10.70.0.143:443:crm.example.com")));
    }

    [Fact]
    public void Ipv6AddressIsBracketed()
    {
        var site = SiteFrom("x", "https");
        site.IpAddress = "fe80::1";
        site.HostName = "";

        Assert.Equal("[fe80::1]", ResolveHost(site));
    }

    [Fact]
    public void MissingBindingInformationFallsBackToLocalhost()
    {
        Assert.Equal("localhost", ResolveHost(SiteFrom("")));
    }
}
