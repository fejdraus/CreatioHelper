using System;
using System.Linq;
using System.Runtime.Versioning;
using CreatioHelper.Application.Interfaces;
using Microsoft.Web.Administration;

namespace CreatioHelper.Infrastructure.Services.Configuration;

[SupportedOSPlatform("windows")]
public class IisConfigEditor : IIisConfigEditor
{
    public void SetPhysicalPath(string siteName, string physicalPath)
    {
        if (string.IsNullOrWhiteSpace(siteName)) throw new ArgumentNullException(nameof(siteName));
        if (string.IsNullOrWhiteSpace(physicalPath)) throw new ArgumentNullException(nameof(physicalPath));

        using var manager = new ServerManager();
        var site = manager.Sites.FirstOrDefault(s => s.Name.Equals(siteName, StringComparison.OrdinalIgnoreCase));
        var app = site?.Applications["/"];
        var vdir = app?.VirtualDirectories["/"];
        if (vdir != null)
        {
            vdir.PhysicalPath = physicalPath;
            manager.CommitChanges();
        }
    }

    public void SetAppPool(string siteName, string poolName)
    {
        if (string.IsNullOrWhiteSpace(siteName)) throw new ArgumentNullException(nameof(siteName));
        if (string.IsNullOrWhiteSpace(poolName)) throw new ArgumentNullException(nameof(poolName));

        using var manager = new ServerManager();
        var site = manager.Sites.FirstOrDefault(s => s.Name.Equals(siteName, StringComparison.OrdinalIgnoreCase));
        var app = site?.Applications["/"];
        if (app != null)
        {
            app.ApplicationPoolName = poolName;
            manager.CommitChanges();
        }
    }
}
