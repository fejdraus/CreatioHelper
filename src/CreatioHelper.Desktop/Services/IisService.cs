
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Threading;
using CreatioHelper.Shared.Utils;
using CreatioHelper.Domain.Entities;
using Microsoft.Web.Administration;

namespace CreatioHelper.Services;

public class IisService
{
    public static bool IsIisAvailable()
    {
        if (!OperatingSystem.IsWindows()) return false;
    
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\InetStp");
            if (key == null) return false;
        }
        catch
        {
            return false;
        }
    
        try
        {
            using var serviceController = new System.ServiceProcess.ServiceController("W3SVC");
            var _ = serviceController.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
    
    public (string PoolStatus, string SiteStatus) GetLocalStatus(string siteName, string poolName)
    {
        if (!OperatingSystem.IsWindows() || !IsIisAvailable())
        {
            return ("Unknown", "Unknown");
        }

        try
        {
            using var manager = new ServerManager();

            var poolStatus = "Unknown";
            if (!string.IsNullOrWhiteSpace(poolName))
            {
                var pool = manager.ApplicationPools[poolName];
                if (pool != null)
                {
                    poolStatus = pool.State.ToString();
                }
            }

            var siteStatus = "Unknown";
            if (!string.IsNullOrWhiteSpace(siteName))
            {
                var site = manager.Sites[siteName];
                if (site != null)
                {
                    siteStatus = site.State.ToString();
                }
            }

            return (poolStatus, siteStatus);
        }
        catch
        {
            return ("Unknown", "Unknown");
        }
    }

    public void LoadIisSites(ObservableCollection<IisSiteInfo> iisSites, Action<bool> onCompletion)
    {
        if (!OperatingSystem.IsWindows() || !IsIisAvailable())
        {
            iisSites.Clear();
            onCompletion(false);
            return;
        }
        
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                using var manager = new ServerManager();
                var sites = manager.Sites.ToList();

                var results = new List<IisSiteInfo>();

                foreach (var site in sites)
                {
                    var binding = site.Bindings.FirstOrDefault(b => b.Protocol == "http")
                        ?? site.Bindings.FirstOrDefault(b => b.Protocol == "https");
                    var bindingParts = (binding?.BindingInformation ?? "").Split(':');
                    var bindingPort = bindingParts.Length > 1 ? bindingParts[1] : "";
                    var bindingHost = bindingParts.Length > 2 ? bindingParts[2] : "";
                    var bindingProtocol = binding?.Protocol ?? "http";

                    var subApp = site.Applications.FirstOrDefault(a => a.Path == "/0");
                    var subAppVdir = subApp?.VirtualDirectories["/"];
                    var rootApp = site.Applications["/"];
                    var rootVdir = rootApp?.VirtualDirectories["/"];
                    string sitePath = rootVdir?.PhysicalPath ?? string.Empty;
                    string subAppPath = subAppVdir?.PhysicalPath ?? string.Empty;
                    string poolName = rootApp?.ApplicationPoolName ?? string.Empty;

                    if (!string.IsNullOrEmpty(sitePath) && !string.IsNullOrEmpty(subAppPath) && !string.IsNullOrEmpty(poolName)
                        && File.Exists(Path.Combine(subAppPath, "Web.config"))
                        && File.Exists(Path.Combine(sitePath, "ConnectionStrings.config"))
                        && File.Exists(Path.Combine(sitePath, "Web.config")))
                    {
                        results.Add(new IisSiteInfo
                        {
                            Id = site.Id,
                            Name = site.Name,
                            Path = sitePath,
                            PoolName = poolName,
                            Version = AppVersionHelper.GetAppVersion(sitePath),
                            Port = bindingPort,
                            HostName = bindingHost,
                            Protocol = bindingProtocol
                        });
                    }

                    foreach (var virtualApp in site.Applications.Where(a => a.Path != "/" && a.Path != "/0"))
                    {
                        var vdir = virtualApp.VirtualDirectories["/"];
                        string virtualPath = vdir?.PhysicalPath ?? string.Empty;
                        if (string.IsNullOrEmpty(virtualPath)) continue;
                        if (!File.Exists(Path.Combine(virtualPath, "ConnectionStrings.config"))) continue;

                        results.Add(new IisSiteInfo
                        {
                            Id = site.Id,
                            Name = $"{site.Name}{virtualApp.Path}",
                            Path = virtualPath,
                            PoolName = virtualApp.ApplicationPoolName ?? string.Empty,
                            Version = AppVersionHelper.GetAppVersion(virtualPath),
                            Port = bindingPort,
                            HostName = bindingHost,
                            Protocol = bindingProtocol
                        });
                    }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    iisSites.Clear();
                    foreach (var site in results)
                        iisSites.Add(site);
                    onCompletion(true);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    iisSites.Clear();
                    iisSites.Add(new IisSiteInfo
                    {
                        Name = $"[Error loading IIS] {ex.Message}",
                        Path = string.Empty,
                        PoolName = string.Empty
                    });
                    onCompletion(false);
                });
            }
        });
    }

}
