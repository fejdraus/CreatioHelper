using System.IO;
using CreatioHelper.Infrastructure.Services;
using Xunit;

namespace CreatioHelper.UnitTests;

public class WebConfigRetryRedisTests
{
    private const string WithKeyTrue = @"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
  <appSettings>
    <add key=""UseStaticFileContent"" value=""true"" />
    <add key=""Feature-UseRetryRedisOperation"" value=""true"" />
  </appSettings>
</configuration>";

    private const string WithKeyFalse = @"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
  <appSettings>
    <add key=""Feature-UseRetryRedisOperation"" value=""false"" />
  </appSettings>
</configuration>";

    private const string WithoutKey = @"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
  <appSettings>
    <add key=""UseStaticFileContent"" value=""true"" />
  </appSettings>
</configuration>";

    private const string WithoutAppSettings = @"<?xml version=""1.0"" encoding=""utf-8""?>
<configuration>
</configuration>";

    private static string CreateSite(string fileName, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "chwc_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, fileName), content);
        return dir;
    }

    [Theory]
    [InlineData("Web.config")]
    [InlineData("Terrasoft.WebHost.dll.config")]
    public void Read_ReturnsTrue_WhenKeyEnabled(string fileName)
    {
        var dir = CreateSite(fileName, WithKeyTrue);
        Assert.True(new WebConfigEditor().ReadRetryRedisOperation(dir));
    }

    [Theory]
    [InlineData("Web.config")]
    [InlineData("Terrasoft.WebHost.dll.config")]
    public void Read_ReturnsFalse_WhenKeyDisabled(string fileName)
    {
        var dir = CreateSite(fileName, WithKeyFalse);
        Assert.False(new WebConfigEditor().ReadRetryRedisOperation(dir));
    }

    [Fact]
    public void Read_ReturnsNull_WhenKeyMissing()
    {
        var dir = CreateSite("Web.config", WithoutKey);
        Assert.Null(new WebConfigEditor().ReadRetryRedisOperation(dir));
    }

    [Fact]
    public void Read_ReturnsNull_WhenNoConfigFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "chwc_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        Assert.Null(new WebConfigEditor().ReadRetryRedisOperation(dir));
    }

    [Fact]
    public void Write_AddsKey_WhenMissing()
    {
        var dir = CreateSite("Web.config", WithoutKey);
        var editor = new WebConfigEditor();

        editor.WriteRetryRedisOperation(dir, true);

        Assert.True(editor.ReadRetryRedisOperation(dir));
        Assert.Contains("UseStaticFileContent", File.ReadAllText(Path.Combine(dir, "Web.config")));
    }

    [Fact]
    public void Write_CreatesAppSettings_WhenSectionMissing()
    {
        var dir = CreateSite("Web.config", WithoutAppSettings);
        var editor = new WebConfigEditor();

        editor.WriteRetryRedisOperation(dir, true);

        Assert.True(editor.ReadRetryRedisOperation(dir));
    }

    [Fact]
    public void Write_UpdatesExistingKey()
    {
        var dir = CreateSite("Web.config", WithKeyTrue);
        var editor = new WebConfigEditor();

        editor.WriteRetryRedisOperation(dir, false);

        Assert.False(editor.ReadRetryRedisOperation(dir));
    }

    [Fact]
    public void Write_PrefersCoreConfig_WhenBothExist()
    {
        var dir = CreateSite("Terrasoft.WebHost.dll.config", WithoutKey);
        File.WriteAllText(Path.Combine(dir, "Web.config"), WithoutKey);
        var editor = new WebConfigEditor();

        editor.WriteRetryRedisOperation(dir, true);

        Assert.Contains("Feature-UseRetryRedisOperation", File.ReadAllText(Path.Combine(dir, "Terrasoft.WebHost.dll.config")));
        Assert.DoesNotContain("Feature-UseRetryRedisOperation", File.ReadAllText(Path.Combine(dir, "Web.config")));
    }
}
