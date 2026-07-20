using System.IO;
using CreatioHelper.Infrastructure.Services.Sync.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CreatioHelper.UnitTests;

public class ConfigPathConsistencyTests
{
    [Fact]
    public void ConfigPathIsTheConfigFileInsideTheConfigDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cfgcons_" + Path.GetRandomFileName());
        var service = new ConfigXmlService(NullLogger<ConfigXmlService>.Instance, dir);

        Assert.Equal(dir, service.GetConfigDirectory());
        Assert.Equal(Path.Combine(dir, "config.xml"), service.ConfigPath);
    }

    [Fact]
    public void DefaultConfigDirectoryResolvesToOneKnownLocation()
    {
        var service = new ConfigXmlService(NullLogger<ConfigXmlService>.Instance);

        var dir = service.GetConfigDirectory();

        Assert.False(string.IsNullOrWhiteSpace(dir));
        Assert.EndsWith(Path.Combine(dir, "config.xml"), service.ConfigPath);

        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            Assert.Equal(Path.Combine(localAppData, "CreatioHelper"), dir);
        }
        else
        {
            // On Linux and macOS the directory follows the XDG / Application Support convention,
            // not SpecialFolder.LocalApplicationData, which is exactly what the diagnostics
            // endpoint used to hard-code.
            Assert.NotEqual(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CreatioHelper"),
                dir);
        }
    }
}
