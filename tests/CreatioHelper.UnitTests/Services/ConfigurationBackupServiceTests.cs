using CreatioHelper.Infrastructure.Services.Workspace;
using Xunit;

namespace CreatioHelper.Tests.Services;

public class ConfigurationBackupServiceTests : IDisposable
{
    private readonly string _sitePath;
    private readonly ConfigurationBackupService _service = new();

    public ConfigurationBackupServiceTests()
    {
        _sitePath = Path.Combine(Path.GetTempPath(), "chb_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_sitePath, "Terrasoft.WebApp", "bin"));
        File.WriteAllText(Path.Combine(_sitePath, "Terrasoft.WebApp", "bin", "Terrasoft.Common.dll"), "stub");
    }

    private string CreateBackupDirectory()
    {
        var path = Path.Combine(_sitePath, "Terrasoft.WebApp", "conf", "backup");
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void GetBackupPath_PointsToConfBackupUnderWebApp()
    {
        var path = _service.GetBackupPath(_sitePath);

        Assert.EndsWith(Path.Combine("Terrasoft.WebApp", "conf", "backup"), path);
    }

    [Fact]
    public void GetBackupPath_ThrowsWhenSitePathEmpty()
    {
        Assert.Throws<ArgumentNullException>(() => _service.GetBackupPath(string.Empty));
    }

    [Fact]
    public void Read_ReportsMissingDirectory()
    {
        var backup = _service.Read(_sitePath);

        Assert.False(backup.Exists);
        Assert.False(backup.CanRestore);
        Assert.Empty(backup.ChangedPackages);
    }

    [Fact]
    public void Read_ReturnsEmptyBackupForEmptyDirectory()
    {
        CreateBackupDirectory();

        var backup = _service.Read(_sitePath);

        Assert.True(backup.Exists);
        Assert.True(backup.IsEmpty);
        Assert.False(backup.CanRestore);
    }

    [Fact]
    public void Read_ListsChangedPackagesSortedByName()
    {
        var backupPath = CreateBackupDirectory();
        File.WriteAllText(Path.Combine(backupPath, "ZetaPackage.gz"), new string('z', 100));
        File.WriteAllText(Path.Combine(backupPath, "AlphaPackage.gz"), new string('a', 50));

        var backup = _service.Read(_sitePath);

        Assert.Equal(2, backup.ChangedPackages.Count);
        Assert.Equal("AlphaPackage", backup.ChangedPackages[0].Name);
        Assert.Equal("ZetaPackage", backup.ChangedPackages[1].Name);
        Assert.Equal(50, backup.ChangedPackages[0].SizeBytes);
        Assert.True(backup.CanRestore);
    }

    [Fact]
    public void Read_IgnoresNonPackageFiles()
    {
        var backupPath = CreateBackupDirectory();
        File.WriteAllText(Path.Combine(backupPath, "ChangedInactivePackages.txt"), "{}");
        File.WriteAllText(Path.Combine(backupPath, "AppInfo.txt"), "info");

        var backup = _service.Read(_sitePath);

        Assert.Empty(backup.ChangedPackages);
        Assert.True(backup.IsEmpty);
    }

    [Fact]
    public void Read_ParsesDeleteListEntries()
    {
        var backupPath = CreateBackupDirectory();
        File.WriteAllLines(Path.Combine(backupPath, "DeleteList.txt"), new[]
        {
            "a7d24489-950a-fa76-9ef1-0aa27d798580,CrtApolloInC360,Repository",
            string.Empty,
            "b1234567-950a-fa76-9ef1-0aa27d798581,OtherPackage,Zip"
        });

        var backup = _service.Read(_sitePath);

        Assert.Equal(2, backup.PackagesToRemove.Count);
        Assert.Equal("CrtApolloInC360", backup.PackagesToRemove[0].Name);
        Assert.Equal("a7d24489-950a-fa76-9ef1-0aa27d798580", backup.PackagesToRemove[0].UId);
        Assert.Equal("Repository", backup.PackagesToRemove[0].Source);
        Assert.Equal("OtherPackage", backup.PackagesToRemove[1].Name);
    }

    [Fact]
    public void Read_TreatsDeleteListOnlyBackupAsRestorable()
    {
        var backupPath = CreateBackupDirectory();
        File.WriteAllLines(Path.Combine(backupPath, "DeleteList.txt"), new[] { "uid,NewPackage,Zip" });

        var backup = _service.Read(_sitePath);

        Assert.False(backup.IsEmpty);
        Assert.True(backup.CanRestore);
    }

    [Fact]
    public void IsRestoreSupported_FalseWhenVersionCannotBeDetermined()
    {
        Assert.False(_service.IsRestoreSupported(_sitePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_sitePath))
        {
            Directory.Delete(_sitePath, recursive: true);
        }
    }
}
