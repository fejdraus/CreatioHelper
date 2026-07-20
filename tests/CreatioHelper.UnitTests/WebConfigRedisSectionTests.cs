using System.Collections.Generic;
using System.IO;
using System.Linq;
using CreatioHelper.Infrastructure.Services;
using Xunit;

namespace CreatioHelper.UnitTests;

public class WebConfigRedisSectionTests
{
    private const string WithTerrasoftRedis = @"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
  <terrasoft>
    <redis connectionStringName=""redis"" operationRetryCount=""10"" abortOnConnectFail=""true"" />
  </terrasoft>
</configuration>";

    private const string WithoutRedis = @"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
  <terrasoft>
  </terrasoft>
</configuration>";

    private static string CreateSite(string fileName, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "chrs_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
        if (fileName == "Web.config")
        {
            Directory.CreateDirectory(Path.Combine(dir, "Terrasoft.WebApp", "bin"));
            File.WriteAllText(Path.Combine(dir, "Terrasoft.WebApp", "bin", "Terrasoft.Common.dll"), "");
        }
        return dir;
    }

    [Theory]
    [InlineData("Web.config")]
    [InlineData("Terrasoft.WebHost.dll.config")]
    public void Read_ReturnsAllAttributes(string fileName)
    {
        var dir = CreateSite(fileName, WithTerrasoftRedis);

        var attributes = new WebConfigEditor().ReadRedisSection(dir);

        Assert.NotNull(attributes);
        Assert.Equal(3, attributes!.Count);
        Assert.Equal("redis", attributes.First(a => a.Key == "connectionStringName").Value);
        Assert.Equal("10", attributes.First(a => a.Key == "operationRetryCount").Value);
        Assert.Equal("true", attributes.First(a => a.Key == "abortOnConnectFail").Value);
    }

    [Fact]
    public void GetRedisSectionFileName_ReturnsWebConfig_ForNetFramework()
    {
        var dir = CreateSite("Web.config", WithTerrasoftRedis);
        Assert.Equal("Web.config", new WebConfigEditor().GetRedisSectionFileName(dir));
    }

    [Fact]
    public void GetRedisSectionFileName_PrefersCoreConfig_ForNetCore()
    {
        var dir = CreateSite("Terrasoft.WebHost.dll.config", WithTerrasoftRedis);
        File.WriteAllText(Path.Combine(dir, "Web.config"), WithTerrasoftRedis);

        Assert.Equal("Terrasoft.WebHost.dll.config", new WebConfigEditor().GetRedisSectionFileName(dir));
    }

    [Fact]
    public void GetRedisSectionFileName_ReturnsNull_WhenNoConfigFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chrs_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);

        Assert.Null(new WebConfigEditor().GetRedisSectionFileName(dir));
    }

    [Fact]
    public void Read_ReturnsNull_WhenSectionMissing()
    {
        var dir = CreateSite("Web.config", WithoutRedis);
        Assert.Null(new WebConfigEditor().ReadRedisSection(dir));
    }

    [Fact]
    public void Read_ReturnsNull_WhenNoConfigFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chrs_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        Assert.Null(new WebConfigEditor().ReadRedisSection(dir));
    }

    [Fact]
    public void Write_UpdatesExistingAttributes()
    {
        var dir = CreateSite("Web.config", WithTerrasoftRedis);
        var editor = new WebConfigEditor();

        editor.WriteRedisSection(dir, new List<KeyValuePair<string, string>>
        {
            new("operationRetryCount", "25"),
            new("abortOnConnectFail", "false"),
        });

        var attributes = editor.ReadRedisSection(dir)!;
        Assert.Equal("25", attributes.First(a => a.Key == "operationRetryCount").Value);
        Assert.Equal("false", attributes.First(a => a.Key == "abortOnConnectFail").Value);
    }

    [Fact]
    public void Write_AddsMissingAttribute_AndKeepsExisting()
    {
        var dir = CreateSite("Web.config", WithTerrasoftRedis);
        var editor = new WebConfigEditor();

        editor.WriteRedisSection(dir, new List<KeyValuePair<string, string>>
        {
            new("clientsManager", "Terrasoft.Redis.StackExchangeAdapters.RedisClientsManagerAdapter, Terrasoft.Redis.StackExchangeAdapters"),
        });

        var attributes = editor.ReadRedisSection(dir)!;
        Assert.Contains("StackExchangeAdapters", attributes.First(a => a.Key == "clientsManager").Value);
        Assert.Equal("redis", attributes.First(a => a.Key == "connectionStringName").Value);
    }

    [Fact]
    public void Write_DoesNothing_WhenSectionMissing()
    {
        var dir = CreateSite("Web.config", WithoutRedis);
        var editor = new WebConfigEditor();

        editor.WriteRedisSection(dir, new List<KeyValuePair<string, string>> { new("operationRetryCount", "25") });

        Assert.Null(editor.ReadRedisSection(dir));
        Assert.DoesNotContain("operationRetryCount", File.ReadAllText(Path.Combine(dir, "Web.config")));
    }
}
